using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using QuizGame.Contracts;
using QuizGame.Server.Auth;
using QuizGame.Server.Domain;
using QuizGame.Server.Hubs;
using QuizGame.Server.Services;

namespace QuizGame.Server.Controllers;

[ApiController]
[Route("api/rooms")]
public sealed class RoomsController(
    GameRoomManager rooms,
    JwtTokenService tokens,
    IHubContext<GameHub> hub,
    TimeProvider time,
    ISessionRecorder recorder,
    RoundCoordinator roundCoordinator,
    IConfiguration config) : ControllerBase
{
    /// <summary>Game master creates a room (optional settings body) and gets its 6-digit code.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.GameMasterOnly)]
    public async Task<ActionResult<CreateRoomResponse>> Create([FromBody] RoomSettings? settings = null)
    {
        if (settings is not null && !AreValid(settings))
            return ApiProblem.Create(400, ErrorCodes.InvalidSettings, "Room settings contain an out-of-range value.");

        var room = rooms.CreateRoom(User.GetUserId(), settings);
        await recorder.SessionStartedAsync(room, HttpContext.RequestAborted);
        var response = new CreateRoomResponse(room.SessionId, room.Code, JoinUrl(room.Code));
        return CreatedAtAction(nameof(Get), new { code = room.Code }, response);
    }

    /// <summary>Lightweight check used by the join page.</summary>
    [HttpGet("{code}/exists")]
    [EnableRateLimiting("join")]
    public ActionResult<RoomExistsResponse> Exists(string code) =>
        rooms.TryGet(code, out var room)
            ? new RoomExistsResponse(true, room.Phase)
            : new RoomExistsResponse(false, null);

    /// <summary>Snapshot of a room, for the game master who owns it.</summary>
    [HttpGet("{code}")]
    [Authorize(Policy = Policies.GameMasterOnly)]
    public ActionResult<RoomStateDto> Get(string code)
    {
        if (!TryGetOwnedRoom(code, out var room, out var error)) return error!;
        return room.Snapshot(time.GetUtcNow(), forHost: true);
    }

    /// <summary>Closes a room and tells everybody in it.</summary>
    [HttpDelete("{code}")]
    [Authorize(Policy = Policies.GameMasterOnly)]
    public async Task<IActionResult> Close(string code)
    {
        if (!TryGetOwnedRoom(code, out var room, out var error)) return error!;

        rooms.Remove(room.Code);
        roundCoordinator.Cancel(room.Code);
        await recorder.SessionEndedAsync(room.SessionId);
        await hub.Clients.Group(GroupNames.Room(room.Code)).SendAsync(HubNames.Events.RoomClosed);
        return NoContent();
    }

    /// <summary>A player joins with the room code and a display name.</summary>
    [HttpPost("{code}/join")]
    [EnableRateLimiting("join")]
    public async Task<ActionResult<JoinResponse>> Join(string code, [FromBody] JoinRequest request)
    {
        if (!rooms.TryGet(code, out var room))
            return RoomNotFound();

        var result = room.AddPlayer(request.Name, time.GetUtcNow());
        if (!result.Ok)
        {
            return result.Error switch
            {
                JoinError.InvalidName => ApiProblem.Create(400, ErrorCodes.InvalidName,
                    $"Name must be 1–{GameRoom.MaxNameLength} characters."),
                JoinError.NameTaken => ApiProblem.Create(409, ErrorCodes.NameTaken, "That name is already taken."),
                JoinError.RoomFull => ApiProblem.Create(409, ErrorCodes.RoomFull, "The room is full."),
                _ => ApiProblem.Create(409, ErrorCodes.NotInLobby, "The game has already started."),
            };
        }

        var player = result.Player!;
        await recorder.PlayerJoinedAsync(room.SessionId, player);
        await hub.Clients.Group(GroupNames.Room(room.Code)).SendAsync(HubNames.Events.PlayerJoined, player);
        return BuildJoinResponse(room.Code, player);
    }

    /// <summary>Validates a stored player token and returns fresh session info (plan §7.5).</summary>
    [HttpPost("{code}/rejoin")]
    [Authorize(Policy = Policies.PlayerOnly)]
    public ActionResult<JoinResponse> Rejoin(string code)
    {
        if (User.FindFirstValue(ClaimNames.Room) != code || !rooms.TryGet(code, out var room))
            return RoomNotFound();

        var player = room.GetPlayer(User.GetUserId());
        if (player is null)
            return ApiProblem.Create(404, ErrorCodes.PlayerNotFound, "You are no longer in this room.");

        room.Touch(time.GetUtcNow());
        return BuildJoinResponse(room.Code, player);
    }

    private JoinResponse BuildJoinResponse(string code, PlayerSummary player)
    {
        var token = tokens.CreatePlayerToken(player.Id, code, player.Name, out var expires);
        return new JoinResponse(token, player, expires);
    }

    private bool TryGetOwnedRoom(string code, out GameRoom room, out ActionResult? error)
    {
        error = null;
        if (!rooms.TryGet(code, out room))
        {
            error = RoomNotFound();
            return false;
        }

        if (room.GameMasterId != User.GetUserId())
        {
            error = ApiProblem.Create(403, ErrorCodes.NotAuthorized, "This room belongs to another game master.");
            return false;
        }

        return true;
    }

    private static bool AreValid(RoomSettings s) => RoomSettingsRules.IsValid(s);

    private static ObjectResult RoomNotFound() =>
        ApiProblem.Create(404, ErrorCodes.RoomNotFound, "Room not found.");

    private string JoinUrl(string code)
    {
        var baseUrl = config["PublicBaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = $"{Request.Scheme}://{Request.Host}";
        return $"{baseUrl.TrimEnd('/')}/?code={code}";
    }
}
