using QuizGame.Contracts;
using QuizGame.Server.Domain.Map;
using Xunit;

namespace QuizGame.Domain.Tests;

public class MapServiceTests
{
    [Fact]
    public void Presets_have_the_planned_sizes()
    {
        Assert.Equal((5, 5), MapService.Dimensions(MapPreset.Small));
        Assert.Equal((6, 8), MapService.Dimensions(MapPreset.Medium));
        Assert.Equal((8, 10), MapService.Dimensions(MapPreset.Large));
    }

    [Fact]
    public void Map_grows_when_the_preset_is_too_small_for_the_players()
    {
        Assert.Equal((5, 5), MapService.DimensionsFor(MapPreset.Small, 2));
        Assert.Equal((5, 5), MapService.DimensionsFor(MapPreset.Small, 8));          // 24 needed, 25 available
        Assert.Equal((6, 8), MapService.DimensionsFor(MapPreset.Small, 9));          // 27 needed: next preset
        Assert.Equal((8, 10), MapService.DimensionsFor(MapPreset.Medium, 17));       // 51 needed
        Assert.Equal((8, 10), MapService.DimensionsFor(MapPreset.Large, 26));        // 78 needed
        Assert.Equal((10, 10), MapService.DimensionsFor(MapPreset.Large, 30));       // 90 needed: square grid
        Assert.Equal((6, 8), MapService.DimensionsFor(MapPreset.Medium, 0));         // never below the preset
    }

    [Fact]
    public void Create_gives_a_free_grid_with_row_major_indices()
    {
        var map = MapService.Create(new RoomSettings { MapPreset = MapPreset.Medium, TerritoryValue = 40 }, 4);

        Assert.Equal(6, map.Rows);
        Assert.Equal(8, map.Cols);
        Assert.Equal(48, map.Count);
        Assert.True(map.Territories.All(t => t.IsFree && !t.IsBase && t.Value == 40));
        Assert.True(map.Territories.All(t => t.Index == t.Row * 8 + t.Col));
        Assert.Equal(0, map.OwnedCount);
    }

    [Fact]
    public void Adjacency_is_four_neighbour_and_symmetric()
    {
        var map = MapService.Create(new RoomSettings { MapPreset = MapPreset.Small }, 2); // 5×5

        Assert.Equal(new[] { 1, 5 }, MapService.NeighboursOf(5, 5, 0).ToArray());          // corner
        Assert.Equal(new[] { 0, 2, 6 }, MapService.NeighboursOf(5, 5, 1).ToArray());       // top edge
        Assert.Equal(new[] { 7, 11, 13, 17 }, MapService.NeighboursOf(5, 5, 12).ToArray()); // centre
        Assert.Equal(new[] { 19, 23 }, MapService.NeighboursOf(5, 5, 24).ToArray());       // opposite corner

        foreach (var t in map.Territories)
            foreach (var n in t.Neighbours)
                Assert.True(MapService.AreAdjacent(map, n, t.Index));

        Assert.False(MapService.AreAdjacent(map, 0, 6));   // diagonal
        Assert.False(MapService.AreAdjacent(map, 4, 5));   // row wrap-around
        Assert.False(MapService.AreAdjacent(map, 0, 99));
    }

    [Fact]
    public void Row_ends_do_not_wrap_into_the_next_row()
    {
        Assert.False(MapService.NeighboursOf(6, 8, 7).Contains(8));
        Assert.False(MapService.NeighboursOf(6, 8, 8).Contains(7));
    }

    [Fact]
    public void Valid_base_picks_are_all_free_cells_by_default()
    {
        var map = MapService.Create(new RoomSettings { MapPreset = MapPreset.Small }, 2);
        Own(map, 3, 12);

        var picks = MapService.ValidBasePicks(map, disallowAdjacentBases: false);

        Assert.False(picks.Relaxed);
        Assert.Equal(23, picks.Indices.Count);
        Assert.False(picks.Indices.Contains(3));
        Assert.False(picks.Indices.Contains(12));
    }

    [Fact]
    public void Adjacent_bases_can_be_forbidden()
    {
        var map = MapService.Create(new RoomSettings { MapPreset = MapPreset.Small }, 2);
        Own(map, 12);

        var picks = MapService.ValidBasePicks(map, disallowAdjacentBases: true);

        Assert.False(picks.Relaxed);
        Assert.False(picks.Indices.Contains(12));
        foreach (var n in new[] { 7, 11, 13, 17 }) Assert.False(picks.Indices.Contains(n));
        Assert.True(picks.Indices.Contains(6));      // diagonal is fine
        Assert.Equal(25 - 5, picks.Indices.Count);
    }

    [Fact]
    public void Adjacency_rule_is_dropped_when_it_would_leave_nothing_to_pick()
    {
        var map = MapService.Create(new RoomSettings { MapPreset = MapPreset.Small }, 2);
        for (var i = 0; i < map.Count; i++)
            if ((map.Get(i)!.Row + map.Get(i)!.Col) % 2 == 0) Own(map, i); // checkerboard: every free cell touches a base

        var picks = MapService.ValidBasePicks(map, disallowAdjacentBases: true);

        Assert.True(picks.Relaxed);
        Assert.Equal(map.Territories.Count(t => t.IsFree), picks.Indices.Count);
    }

    [Fact]
    public void A_full_map_has_no_valid_picks()
    {
        var map = MapService.Create(new RoomSettings { MapPreset = MapPreset.Small }, 2);
        for (var i = 0; i < map.Count; i++) Own(map, i);

        var picks = MapService.ValidBasePicks(map, disallowAdjacentBases: true);

        Assert.Empty(picks.Indices);
        Assert.False(picks.Relaxed);
        Assert.Null(MapService.AutoPick(picks.Indices, new Random(1)));
    }

    [Fact]
    public void Auto_pick_takes_one_of_the_valid_cells()
    {
        var valid = new[] { 4, 9, 17 };
        for (var seed = 0; seed < 20; seed++)
            Assert.True(valid.Contains(MapService.AutoPick(valid, new Random(seed))!.Value));
    }

    [Fact]
    public void Score_is_the_sum_of_owned_territory_values()
    {
        var map = MapService.Create(new RoomSettings { MapPreset = MapPreset.Small, TerritoryValue = 100, BaseValue = 300 }, 2);
        var anna = Guid.NewGuid();
        map.Get(0)!.OwnerId = anna; map.Get(0)!.IsBase = true; map.Get(0)!.Value = 300;
        map.Get(1)!.OwnerId = anna;

        Assert.Equal(400, MapService.ScoreOf(map, anna));
        Assert.Equal(0, MapService.ScoreOf(map, Guid.NewGuid()));
    }

    private static void Own(GameMap map, params int[] indices)
    {
        foreach (var i in indices)
        {
            var t = map.Get(i)!;
            t.OwnerId = Guid.NewGuid();
            t.IsBase = true;
        }
    }
}
