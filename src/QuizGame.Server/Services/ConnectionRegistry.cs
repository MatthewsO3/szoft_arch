using QuizGame.Contracts;
using QuizGame.Server.Auth;
using QuizGame.Server.Domain;

namespace QuizGame.Server.Services;

public sealed record ConnectionEntry(string ConnectionId, string RoomCode, string Role, Guid? PlayerId);

/// <summary>
/// Maps SignalR connections to (room, role, player) and keeps player presence in sync (plan §7.1).
/// A player may briefly have two connections during a reconnect; they are "connected" while any remains.
/// Lock order is always registry → room.
/// </summary>
public sealed class ConnectionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ConnectionEntry> _byConnection = [];

    public ConnectionEntry? Get(string connectionId)
    {
        lock (_gate) return _byConnection.GetValueOrDefault(connectionId);
    }

    public bool HasConnections(string roomCode)
    {
        lock (_gate) return _byConnection.Values.Any(c => c.RoomCode == roomCode);
    }

    /// <summary>All live connections of a room (host and players), e.g. to push each one its own snapshot.</summary>
    public IReadOnlyList<ConnectionEntry> GetRoomConnections(string roomCode)
    {
        lock (_gate) return _byConnection.Values.Where(c => c.RoomCode == roomCode).ToList();
    }

    /// <summary>Connection ids of the given players in a room (a player may have two during a reconnect).</summary>
    public IReadOnlyList<string> GetPlayerConnections(string roomCode, IReadOnlyCollection<Guid> playerIds)
    {
        lock (_gate)
            return _byConnection.Values
                .Where(c => c.RoomCode == roomCode && c.PlayerId is { } id && playerIds.Contains(id))
                .Select(c => c.ConnectionId)
                .ToList();
    }

    /// <summary>Registers a connection. Returns the player DTO if their presence flipped to connected.</summary>
    public PlayerSummary? Connect(GameRoom room, ConnectionEntry info)
    {
        lock (_gate)
        {
            _byConnection[info.ConnectionId] = info;
            return info.PlayerId is { } playerId ? room.SetConnected(playerId, true) : null;
        }
    }

    /// <summary>
    /// Unregisters a connection. Returns its info and, if it was the player's last connection,
    /// the player DTO whose presence flipped to disconnected.
    /// </summary>
    public (ConnectionEntry? Info, PlayerSummary? Changed) Disconnect(string connectionId, GameRoomManager rooms)
    {
        lock (_gate)
        {
            if (!_byConnection.Remove(connectionId, out var info)) return (null, null);
            if (info.PlayerId is not { } playerId) return (info, null);

            var stillConnected = _byConnection.Values.Any(c => c.RoomCode == info.RoomCode && c.PlayerId == playerId);
            if (stillConnected || !rooms.TryGet(info.RoomCode, out var room)) return (info, null);
            return (info, room.SetConnected(playerId, false));
        }
    }

    /// <summary>Removes every connection of a player (kick / leave) and returns their connection ids.</summary>
    public IReadOnlyList<string> DetachPlayer(string roomCode, Guid playerId)
    {
        lock (_gate)
        {
            var ids = _byConnection.Values
                .Where(c => c.RoomCode == roomCode && c.PlayerId == playerId)
                .Select(c => c.ConnectionId)
                .ToList();
            foreach (var id in ids) _byConnection.Remove(id);
            return ids;
        }
    }
}
