using QuizGame.Contracts;

namespace QuizGame.Server.Domain.Map;

/// <summary>
/// One cell of the map. Layout-agnostic (plan §4.4): the rules only use <see cref="Neighbours"/>,
/// the square grid position is for rendering.
/// </summary>
public sealed class Territory
{
    public required int Index { get; init; }
    public required int Row { get; init; }
    public required int Col { get; init; }
    public required IReadOnlyList<int> Neighbours { get; init; }
    public int Value { get; set; }
    public bool IsBase { get; set; }
    public Guid? OwnerId { get; set; }

    public bool IsFree => OwnerId is null;

    public TerritoryDto ToDto() => new()
    {
        Index = Index, Row = Row, Col = Col, Value = Value, IsBase = IsBase, OwnerPlayerId = OwnerId,
    };
}

/// <summary>The valid territories for a base pick; <see cref="Relaxed"/> when the no-adjacent-bases rule had to be dropped.</summary>
public readonly record struct BasePicks(IReadOnlyList<int> Indices, bool Relaxed);

/// <summary>
/// The map of one room: a square grid with 4-neighbour adjacency (DECISION D6, plan §4.4).
/// Not thread-safe on its own; the owning <c>GameRoom</c> guards it with its lock.
/// </summary>
public sealed class GameMap
{
    private readonly Territory[] _cells;

    internal GameMap(int rows, int cols, int territoryValue)
    {
        Rows = rows;
        Cols = cols;
        _cells = new Territory[rows * cols];
        for (var i = 0; i < _cells.Length; i++)
        {
            var row = i / cols;
            var col = i % cols;
            _cells[i] = new Territory
            {
                Index = i,
                Row = row,
                Col = col,
                Neighbours = MapService.NeighboursOf(rows, cols, i),
                Value = territoryValue,
            };
        }
    }

    public int Rows { get; }
    public int Cols { get; }
    public int Count => _cells.Length;
    public IReadOnlyList<Territory> Territories => _cells;

    public Territory? Get(int index) => index >= 0 && index < _cells.Length ? _cells[index] : null;

    public int OwnedCount => _cells.Count(t => t.OwnerId is not null);

    public Guid? OwnerOf(int index) => Get(index)?.OwnerId;

    public IEnumerable<Territory> OwnedBy(Guid playerId) => _cells.Where(t => t.OwnerId == playerId);

    public MapDto ToDto() => new(Rows, Cols, _cells.Select(t => t.ToDto()).ToList());
}

/// <summary>
/// Grid generation, adjacency and valid-pick computation (plan §7.1 MapService). Pure functions: no I/O,
/// no clock, no randomness (auto-picks take their randomness as a parameter).
/// </summary>
public static class MapService
{
    /// <summary>The map must offer at least this many cells per player so that Conquest has room to play.</summary>
    public const int MinCellsPerPlayer = 3;

    public static (int Rows, int Cols) Dimensions(MapPreset preset) => preset switch
    {
        MapPreset.Small => (5, 5),
        MapPreset.Medium => (6, 8),
        MapPreset.Large => (8, 10),
        _ => (6, 8),
    };

    /// <summary>
    /// Grid size for a room: the chosen preset, but never fewer than <see cref="MinCellsPerPlayer"/> cells per
    /// player. A bigger preset is used when needed, and beyond the largest preset a square grid that fits.
    /// </summary>
    public static (int Rows, int Cols) DimensionsFor(MapPreset preset, int playerCount)
    {
        var needed = Math.Max(1, playerCount) * MinCellsPerPlayer;
        for (var p = preset; p <= MapPreset.Large; p++)
        {
            var (rows, cols) = Dimensions(p);
            if (rows * cols >= needed) return (rows, cols);
        }

        var side = (int)Math.Ceiling(Math.Sqrt(needed));
        return (side, side);
    }

    /// <summary>Creates an all-free map for the room's settings and player count.</summary>
    public static GameMap Create(RoomSettings settings, int playerCount)
    {
        var (rows, cols) = DimensionsFor(settings.MapPreset, playerCount);
        return new GameMap(rows, cols, settings.TerritoryValue);
    }

    /// <summary>4-neighbour adjacency on a rows × cols grid (indices are row-major), in ascending order.</summary>
    public static IReadOnlyList<int> NeighboursOf(int rows, int cols, int index)
    {
        var row = index / cols;
        var col = index % cols;
        var result = new List<int>(4);
        if (row > 0) result.Add(index - cols);
        if (col > 0) result.Add(index - 1);
        if (col < cols - 1) result.Add(index + 1);
        if (row < rows - 1) result.Add(index + cols);
        return result;
    }

    public static bool AreAdjacent(GameMap map, int a, int b) => map.Get(a)?.Neighbours.Contains(b) == true;

    /// <summary>
    /// Free territories a player may take as their base (plan §4.2): every free cell, or, with
    /// <paramref name="disallowAdjacentBases"/>, only those that touch no existing base. If that rule would leave
    /// nothing to pick, it is dropped for this pick so the game cannot stall (<see cref="BasePicks.Relaxed"/>).
    /// </summary>
    public static BasePicks ValidBasePicks(GameMap map, bool disallowAdjacentBases)
    {
        var free = map.Territories.Where(t => t.IsFree).ToList();
        if (!disallowAdjacentBases)
            return new BasePicks(free.Select(t => t.Index).ToList(), false);

        var strict = free
            .Where(t => t.Neighbours.All(n => !map.Territories[n].IsBase))
            .Select(t => t.Index)
            .ToList();
        return strict.Count > 0
            ? new BasePicks(strict, false)
            : new BasePicks(free.Select(t => t.Index).ToList(), free.Count > 0);
    }

    /// <summary>
    /// Free territories a Conquest grab winner may take (plan §4.2 Conquest): a free cell adjacent to a
    /// territory the player already owns, or — if they own none (e.g. their base was lost) — any free cell.
    /// </summary>
    public static IReadOnlyList<int> ValidGrabPicks(GameMap map, Guid playerId)
    {
        var free = map.Territories.Where(t => t.IsFree).ToList();
        var ownsSomething = map.OwnedBy(playerId).Any();
        if (!ownsSomething)
            return free.Select(t => t.Index).ToList();

        return free
            .Where(t => t.Neighbours.Any(n => map.Territories[n].OwnerId == playerId))
            .Select(t => t.Index)
            .ToList();
    }

    /// <summary>Picks one of the valid territories at random for a player who ran out of time.</summary>
    public static int? AutoPick(IReadOnlyList<int> validIndices, Random random) =>
        validIndices.Count == 0 ? null : validIndices[random.Next(validIndices.Count)];

    /// <summary>
    /// Score of a player = sum of the values of the territories they own right now (plan §4.3, derived, not accumulated).
    /// </summary>
    public static int ScoreOf(GameMap map, Guid playerId) => map.OwnedBy(playerId).Sum(t => t.Value);
}
