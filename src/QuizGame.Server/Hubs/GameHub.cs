using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using QuizGame.Contracts;
using QuizGame.Server.Auth;
using QuizGame.Server.Domain;
using QuizGame.Server.Services;

namespace QuizGame.Server.Hubs;

/// <summary>
/// The single game hub. Group membership comes from the token claims, never from client arguments,
/// so a player can only ever join the room their token was issued for (plan §6.3).
/// Handles room presence and question rounds; phase and territory methods arrive with the map.
/// </summary>
[Authorize]
public sealed class GameHub(
    GameRoomManager rooms,
    ConnectionRegistry registry,
    TimeProvider time,
    ISessionRecorder recorder,
    RoundCoordinator rounds,
    ILogger<GameHub> logger) : Hub
{
    public override async Task OnConnectedAsync()
    {
        // Players are attached from their token on every (re)connect. Game masters attach with HostAttach.
        if (Context.User?.FindFirstValue(ClaimTypes.Role) == Roles.Player)
            await AttachPlayerAsync();

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var (info, changed) = registry.Disconnect(Context.ConnectionId, rooms);
        if (info is not null && rooms.TryGet(info.RoomCode, out var room))
        {
            room.Touch(time.GetUtcNow());
            if (changed is not null)
                await Clients.Group(GroupNames.Room(info.RoomCode)).SendAsync(HubNames.Events.PlayerConnectionChanged, changed);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Game master attaches this connection to a room they own (also used after a host reconnect).</summary>
    [Authorize(Policy = Policies.GameMasterOnly)]
    public async Task HostAttach(string roomCode)
    {
        if (!rooms.TryGet(roomCode, out var room))
            throw new HubException(ErrorCodes.RoomNotFound);
        if (room.GameMasterId != Context.User!.GetUserId())
            throw new HubException(ErrorCodes.NotAuthorized);

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupNames.Room(room.Code));
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupNames.Gm(room.Code));
        registry.Connect(room, new ConnectionEntry(Context.ConnectionId, room.Code, Roles.GameMaster, null));
        room.Touch(time.GetUtcNow());

        await rounds.SendStateAsync(room, Clients.Caller, null);
    }

    /// <summary>
    /// Re-attach after a disconnect; the server replies with a fresh <c>RoomState</c> (api-contract §2).
    /// Players are already attached on connect, so this is idempotent.
    /// </summary>
    public async Task Rejoin()
    {
        if (Context.User?.FindFirstValue(ClaimTypes.Role) == Roles.Player)
        {
            await AttachPlayerAsync();
            return;
        }

        var info = registry.Get(Context.ConnectionId);
        if (info is null || !rooms.TryGet(info.RoomCode, out var room))
            throw new HubException(ErrorCodes.NotAttached);
        await rounds.SendStateAsync(room, Clients.Caller, null);
    }

    [Authorize(Policy = Policies.GameMasterOnly)]
    public async Task KickPlayer(Guid playerId)
    {
        var info = registry.Get(Context.ConnectionId);
        if (info is not { Role: Roles.GameMaster } || !rooms.TryGet(info.RoomCode, out var room))
            throw new HubException(ErrorCodes.NotAttached);

        var removed = room.RemovePlayer(playerId) ?? throw new HubException(ErrorCodes.PlayerNotFound);

        logger.LogInformation("Player {PlayerId} kicked from room {Room}", playerId, room.Code);
        await recorder.PlayerLeftAsync(room.SessionId, playerId);
        await RemovePlayerConnectionsAsync(room.Code, playerId, HubNames.Events.Kicked);
        await Clients.Group(GroupNames.Room(room.Code)).SendAsync(HubNames.Events.PlayerLeft, removed);
    }

    [Authorize(Policy = Policies.PlayerOnly)]
    public async Task LeaveRoom()
    {
        var info = registry.Get(Context.ConnectionId);
        if (info?.PlayerId is not { } playerId || !rooms.TryGet(info.RoomCode, out var room))
            throw new HubException(ErrorCodes.NotAttached);

        var removed = room.RemovePlayer(playerId);
        if (removed is not null) await recorder.PlayerLeftAsync(room.SessionId, playerId);
        await RemovePlayerConnectionsAsync(room.Code, playerId, notifyEvent: null);
        if (removed is not null)
            await Clients.Group(GroupNames.Room(room.Code)).SendAsync(HubNames.Events.PlayerLeft, removed);
    }

    /// <summary>Host opens a question round (plan §4.2). The server pushes <c>QuestionStarted</c> to each audience.</summary>
    [Authorize(Policy = Policies.GameMasterOnly)]
    public async Task SendQuestion(SendQuestionRequest request)
    {
        var room = HostRoom();
        if (request is null) throw new HubException(ErrorCodes.InvalidRound);

        var error = await rounds.StartAsync(room, request, Context.ConnectionAborted);
        if (error is not null) throw new HubException(error);
    }

    /// <summary>Host ends the round early; <c>RoundClosed</c> goes to the whole room.</summary>
    [Authorize(Policy = Policies.GameMasterOnly)]
    public async Task CloseRound()
    {
        var room = HostRoom();
        if (!await rounds.CloseAsync(room)) throw new HubException(ErrorCodes.NoActiveRound);
    }

    /// <summary>A player's answer. The receive time is stamped here, on entry, never taken from the client (plan §7.2).</summary>
    [Authorize(Policy = Policies.PlayerOnly)]
    public async Task<AnswerAccepted> SubmitAnswer(SubmitAnswerRequest request)
    {
        var receivedAt = time.GetUtcNow();

        var info = registry.Get(Context.ConnectionId);
        if (info?.PlayerId is not { } playerId || !rooms.TryGet(info.RoomCode, out var room))
            throw new HubException(ErrorCodes.NotAttached);
        if (request is null) throw new HubException(ErrorCodes.InvalidAnswer);

        var error = await rounds.SubmitAsync(room, playerId, request, receivedAt);
        if (error is not null) throw new HubException(error);
        return new AnswerAccepted(request.RoundId);
    }

    /// <summary>Latency / keepalive and clock sync (plan §7.3).</summary>
    public DateTimeOffset Ping() => time.GetUtcNow();

    private GameRoom HostRoom()
    {
        var info = registry.Get(Context.ConnectionId);
        if (info is not { Role: Roles.GameMaster } || !rooms.TryGet(info.RoomCode, out var room))
            throw new HubException(ErrorCodes.NotAttached);
        return room;
    }

    private async Task AttachPlayerAsync()
    {
        var code = Context.User!.FindFirstValue(ClaimNames.Room);
        var playerId = Context.User!.GetUserId();

        if (!rooms.TryGet(code, out var room))
        {
            await Clients.Caller.SendAsync(HubNames.Events.Error,
                new ErrorDto(ErrorCodes.RoomNotFound, "This room no longer exists."));
            return;
        }

        if (room.GetPlayer(playerId) is null)
        {
            await Clients.Caller.SendAsync(HubNames.Events.Error,
                new ErrorDto(ErrorCodes.PlayerNotFound, "You are no longer in this room."));
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupNames.Room(room.Code));
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupNames.Players(room.Code));

        var changed = registry.Connect(room, new ConnectionEntry(Context.ConnectionId, room.Code, Roles.Player, playerId));
        room.Touch(time.GetUtcNow());
        if (changed is not null)
            await Clients.Group(GroupNames.Room(room.Code)).SendAsync(HubNames.Events.PlayerConnectionChanged, changed);

        await rounds.SendStateAsync(room, Clients.Caller, playerId);
    }

    private async Task RemovePlayerConnectionsAsync(string code, Guid playerId, string? notifyEvent)
    {
        var connectionIds = registry.DetachPlayer(code, playerId);
        if (notifyEvent is not null && connectionIds.Count > 0)
            await Clients.Clients(connectionIds).SendAsync(notifyEvent);

        foreach (var id in connectionIds)
        {
            await Groups.RemoveFromGroupAsync(id, GroupNames.Room(code));
            await Groups.RemoveFromGroupAsync(id, GroupNames.Players(code));
        }
    }
}
