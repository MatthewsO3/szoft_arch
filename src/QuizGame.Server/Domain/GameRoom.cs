using QuizGame.Contracts;

namespace QuizGame.Server.Domain;

public enum JoinError
{
    None,
    InvalidName,
    NameTaken,
    RoomFull,
    NotInLobby,
}

/// <summary>Outcome of <see cref="GameRoom.AddPlayer"/>.</summary>
public readonly record struct JoinResult(PlayerSummary? Player, JoinError Error)
{
    public bool Ok => Player is not null;
}

/// <summary>
/// Live state of one room. Pure game logic: no ASP.NET, SignalR or EF dependencies (plan §3).
/// All members are thread-safe (single lock per room).
/// </summary>
public sealed class GameRoom
{
    public const int MaxNameLength = 20;

    private static readonly string[] Palette =
    [
        "#e6194b", "#3cb44b", "#4363d8", "#f58231", "#911eb4", "#46f0f0",
        "#f032e6", "#bcf60c", "#008080", "#9a6324", "#800000", "#808000",
    ];

    private sealed class PlayerState(Guid id, string name, string color, DateTimeOffset joinedAt)
    {
        public Guid Id { get; } = id;
        public string Name { get; } = name;
        public string ColorHex { get; } = color;
        public DateTimeOffset JoinedAt { get; } = joinedAt;
        public bool IsConnected { get; set; }

        public PlayerSummary ToSummary() => new() { Id = Id, Name = Name, ColorHex = ColorHex, Connected = IsConnected };
    }

    private readonly object _gate = new();
    private readonly List<PlayerState> _players = [];
    private GamePhase _phase = GamePhase.Lobby;
    private DateTimeOffset _lastActivity;

    public GameRoom(string code, Guid gameMasterId, RoomSettings settings, int maxPlayers, DateTimeOffset now)
    {
        Code = code;
        GameMasterId = gameMasterId;
        Settings = settings;
        MaxPlayers = maxPlayers;
        CreatedAt = now;
        _lastActivity = now;
        SessionId = Guid.NewGuid();
    }

    public string Code { get; }
    public Guid SessionId { get; }
    public Guid GameMasterId { get; }
    public RoomSettings Settings { get; }
    public int MaxPlayers { get; }
    public DateTimeOffset CreatedAt { get; }

    public GamePhase Phase
    {
        get { lock (_gate) return _phase; }
    }

    public DateTimeOffset LastActivity
    {
        get { lock (_gate) return _lastActivity; }
    }

    public void Touch(DateTimeOffset now)
    {
        lock (_gate) _lastActivity = now;
    }

    /// <summary>Trims and validates a display name (1–20 chars, no control characters).</summary>
    public static bool TryNormalizeName(string? raw, out string name)
    {
        name = (raw ?? string.Empty).Trim();
        if (name.Length is 0 or > MaxNameLength) return false;
        return !name.Any(char.IsControl);
    }

    public JoinResult AddPlayer(string? rawName, DateTimeOffset now)
    {
        if (!TryNormalizeName(rawName, out var name))
            return new JoinResult(null, JoinError.InvalidName);

        lock (_gate)
        {
            if (_phase != GamePhase.Lobby)
                return new JoinResult(null, JoinError.NotInLobby);
            if (_players.Count >= MaxPlayers)
                return new JoinResult(null, JoinError.RoomFull);
            if (_players.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                return new JoinResult(null, JoinError.NameTaken);

            var color = Palette.FirstOrDefault(c => _players.All(p => p.ColorHex != c))
                        ?? Palette[_players.Count % Palette.Length];
            var player = new PlayerState(Guid.NewGuid(), name, color, now);
            _players.Add(player);
            _lastActivity = now;
            return new JoinResult(player.ToSummary(), JoinError.None);
        }
    }

    public PlayerSummary? RemovePlayer(Guid playerId)
    {
        lock (_gate)
        {
            var index = _players.FindIndex(p => p.Id == playerId);
            if (index < 0) return null;
            var dto = _players[index].ToSummary();
            _players.RemoveAt(index);
            return dto;
        }
    }

    public PlayerSummary? GetPlayer(Guid playerId)
    {
        lock (_gate) return _players.FirstOrDefault(p => p.Id == playerId)?.ToSummary();
    }

    /// <summary>Sets presence. Returns the updated player only if the state actually changed.</summary>
    public PlayerSummary? SetConnected(Guid playerId, bool connected)
    {
        lock (_gate)
        {
            var player = _players.FirstOrDefault(p => p.Id == playerId);
            if (player is null || player.IsConnected == connected) return null;
            player.IsConnected = connected;
            return player.ToSummary();
        }
    }

    public RoomStateDto Snapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            return new RoomStateDto
            {
                RoomCode = Code,
                Phase = _phase,
                Settings = Settings,
                Players = _players.Select(p => p.ToSummary()).ToList(),
                ServerUtcNow = now,
            };
        }
    }
}
