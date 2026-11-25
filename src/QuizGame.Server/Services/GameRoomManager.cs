using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using QuizGame.Contracts;
using QuizGame.Server.Domain;

namespace QuizGame.Server.Services;

public sealed class RoomOptions
{
    public const string Section = "Rooms";

    public int MaxPlayers { get; set; } = 30;

    /// <summary>Rooms with no live connection and no activity for this long are removed.</summary>
    public int IdleTimeoutMinutes { get; set; } = 180;
}

/// <summary>Owns all live rooms: code generation, lookup, removal (plan §7.1).</summary>
public sealed class GameRoomManager(IOptions<RoomOptions> options, TimeProvider time)
{
    public const int CodeLength = 6;

    private readonly ConcurrentDictionary<string, GameRoom> _rooms = new();

    public int Count => _rooms.Count;

    public static bool IsValidCode(string? code) =>
        code is { Length: CodeLength } && code.All(char.IsAsciiDigit);

    /// <summary>Creates a room with a collision-checked, cryptographically random 6-digit code.</summary>
    public GameRoom CreateRoom(Guid gameMasterId, RoomSettings? settings = null)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            // 100000..999999: no leading zero, so the code always looks like six digits.
            var code = RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString();
            var room = new GameRoom(code, gameMasterId, settings ?? new RoomSettings(), options.Value.MaxPlayers, time.GetUtcNow());
            if (_rooms.TryAdd(code, room)) return room;
        }

        throw new InvalidOperationException("Could not allocate a free room code.");
    }

    public bool TryGet(string? code, out GameRoom room)
    {
        if (IsValidCode(code) && _rooms.TryGetValue(code!, out var found))
        {
            room = found;
            return true;
        }

        room = null!;
        return false;
    }

    public bool Remove(string code) => _rooms.TryRemove(code, out _);

    /// <summary>Removes and returns rooms idle longer than the timeout that <paramref name="isInUse"/> does not protect.</summary>
    public IReadOnlyList<GameRoom> RemoveIdleRooms(Func<GameRoom, bool> isInUse)
    {
        var cutoff = time.GetUtcNow() - TimeSpan.FromMinutes(options.Value.IdleTimeoutMinutes);
        var removed = new List<GameRoom>();
        foreach (var (code, room) in _rooms)
        {
            if (room.LastActivity < cutoff && !isInUse(room) && _rooms.TryRemove(code, out _))
                removed.Add(room);
        }

        return removed;
    }
}
