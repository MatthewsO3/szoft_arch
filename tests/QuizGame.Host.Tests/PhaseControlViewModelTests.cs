using QuizGame.Contracts;
using QuizGame.Host.ViewModels;

namespace QuizGame.Host.Tests;

public class PhaseControlViewModelTests
{
    [Fact]
    public void PhaseGating_FollowsStrictStateMachineWithoutSkippingOrGoingBack()
    {
        var vm = new PhaseControlViewModel(new FakeRoomControlClient());
        vm.SetRoomOpened(new RoomSettings { MinPlayers = 2 });
        vm.SetConnectionState(true);
        vm.SetPlayerCounts(total: 2, connected: 2);

        // Lobby: only Start game enabled
        Assert.True(vm.IsLobby);
        Assert.False(vm.IsLobbyCompleted);
        Assert.True(vm.StartGameCommand.CanExecute(null));
        Assert.False(vm.NextPhaseCommand.CanExecute(null));
        Assert.False(vm.EndGameCommand.CanExecute(null));

        // Base selection waits for all base picks.
        vm.ApplyPhaseChanged(GamePhase.BaseSelection);
        Assert.False(vm.IsLobby);
        Assert.True(vm.IsLobbyCompleted);
        Assert.True(vm.IsBaseSelection);
        Assert.False(vm.IsBaseSelectionCompleted);
        Assert.False(vm.StartGameCommand.CanExecute(null));
        Assert.False(vm.NextPhaseCommand.CanExecute(null));
        Assert.False(vm.EndGameCommand.CanExecute(null));

        // Conquest can be ended early with Next phase.
        vm.ApplyPhaseChanged(GamePhase.Conquest);
        Assert.True(vm.IsBaseSelectionCompleted);
        Assert.True(vm.IsConquest);
        Assert.False(vm.IsConquestCompleted);
        Assert.False(vm.StartGameCommand.CanExecute(null));
        Assert.True(vm.NextPhaseCommand.CanExecute(null));
        Assert.False(vm.EndGameCommand.CanExecute(null));

        vm.ApplyPhaseChanged(GamePhase.Expansion);
        Assert.False(vm.NextPhaseCommand.CanExecute(null)); // map not full
        vm.ApplyPhaseChanged(GamePhase.Duel);
        Assert.True(vm.EndGameCommand.CanExecute(null));
        Assert.False(vm.NextPhaseCommand.CanExecute(null));

        // Results: all phase transitions disabled
        vm.ApplyPhaseChanged(GamePhase.Results);
        Assert.True(vm.IsConquestCompleted);
        Assert.True(vm.IsResults);
        Assert.False(vm.StartGameCommand.CanExecute(null));
        Assert.False(vm.NextPhaseCommand.CanExecute(null));
        Assert.False(vm.EndGameCommand.CanExecute(null));
    }

    [Fact]
    public void StartGameCommand_CountsConnectedPlayersOnlyAndReflectsUpdatedMinPlayers()
    {
        var vm = new PhaseControlViewModel(new FakeRoomControlClient());
        vm.SetRoomOpened(new RoomSettings { MinPlayers = 2 });
        vm.SetConnectionState(true);

        // 2 total players, only 1 connected -> cannot start
        vm.SetPlayerCounts(total: 2, connected: 1);
        Assert.False(vm.StartGameCommand.CanExecute(null));
        Assert.Contains("1/2", vm.StatusGuidance);

        // Second player reconnects -> can start
        vm.SetPlayerCounts(total: 2, connected: 2);
        Assert.True(vm.StartGameCommand.CanExecute(null));

        // Host updates MinPlayers to 3 -> blocked again
        vm.ApplySettings(new RoomSettings { MinPlayers = 3 });
        Assert.False(vm.StartGameCommand.CanExecute(null));
        Assert.Contains("2/3", vm.StatusGuidance);
    }

    [Fact]
    public async Task Transitions_InvokeHubWithoutOptimisticallyMutatingPhase()
    {
        var client = new FakeRoomControlClient();
        var vm = new PhaseControlViewModel(client);
        vm.SetRoomOpened(new RoomSettings { MinPlayers = 2 });
        vm.SetConnectionState(true);
        vm.SetPlayerCounts(2, 2);

        await vm.StartGameAsync();
        Assert.Equal(1, client.StartGameCalls);
        Assert.Equal(GamePhase.Lobby, vm.Phase); // waits for server PhaseChanged

        vm.ApplyPhaseChanged(GamePhase.Conquest);
        await vm.AdvancePhaseAsync();
        Assert.Equal([GamePhase.Expansion], client.AdvancePhaseTargets);
        Assert.Equal(GamePhase.Conquest, vm.Phase);

        vm.ApplyPhaseChanged(GamePhase.Duel);
        await vm.EndGameAsync();
        Assert.Equal([GamePhase.Expansion, GamePhase.Results], client.AdvancePhaseTargets);
        Assert.Equal(GamePhase.Duel, vm.Phase);
    }

    [Fact]
    public void RoundOpenAndDisconnect_DisablePhaseTransitions()
    {
        var vm = new PhaseControlViewModel(new FakeRoomControlClient());
        vm.SetRoomOpened(new RoomSettings { MinPlayers = 2 });
        vm.SetConnectionState(true);
        vm.SetPlayerCounts(2, 2);
        vm.ApplyPhaseChanged(GamePhase.Conquest);

        Assert.True(vm.NextPhaseCommand.CanExecute(null));

        vm.SetRoundOpen(true);
        Assert.False(vm.NextPhaseCommand.CanExecute(null));
        Assert.Contains("round is open", vm.StatusGuidance, StringComparison.OrdinalIgnoreCase);

        vm.RecordRoundClosed(new RoundClosedDto { RoundId = Guid.NewGuid(), Kind = RoundKind.Expansion });
        Assert.True(vm.NextPhaseCommand.CanExecute(null));

        vm.SetConnectionState(false);
        Assert.False(vm.NextPhaseCommand.CanExecute(null));
        Assert.Contains("Connection lost", vm.StatusGuidance);
    }

    [Fact]
    public void PickWaiting_UpdatesGuidanceAndClearsOnMapUpdatedPhaseChangedOrRoundOpen()
    {
        var vm = new PhaseControlViewModel(new FakeRoomControlClient());
        vm.SetRoomOpened(new RoomSettings());
        vm.SetConnectionState(true);
        vm.ApplyPhaseChanged(GamePhase.Conquest);

        vm.ApplyPickWaiting(new PlayerSummary { Id = Guid.NewGuid(), Name = "Anna" });
        Assert.Contains("Anna is picking", vm.StatusGuidance);
        Assert.False(vm.NextPhaseCommand.CanExecute(null));

        vm.ApplyMapUpdated(new MapUpdate([], []));
        Assert.DoesNotContain("Anna is picking", vm.StatusGuidance);
        Assert.True(vm.NextPhaseCommand.CanExecute(null));

        vm.ApplyPickWaiting(new PlayerSummary { Id = Guid.NewGuid(), Name = "Bela" });
        Assert.Contains("Bela is picking", vm.StatusGuidance);
        vm.SetRoundOpen(true);
        Assert.Equal("", vm.ActivePickerName);

        vm.RecordRoundClosed(new RoundClosedDto { RoundId = Guid.NewGuid(), Kind = RoundKind.Conquest });
        vm.ApplyPickWaiting(new PlayerSummary { Id = Guid.NewGuid(), Name = "Cili" });
        vm.ApplyPhaseChanged(GamePhase.Results);
        Assert.Equal("", vm.ActivePickerName);
    }

    [Fact]
    public void ConquestSuggestions_TriggerOnSuggestedMaxRoundsOrAllTerritoriesOwned()
    {
        var p1 = Guid.NewGuid();
        var vm = new PhaseControlViewModel(new FakeRoomControlClient());
        vm.SetRoomOpened(new RoomSettings { ConquestRounds = 2 });
        vm.SetConnectionState(true);
        vm.SetPlayerCounts(2, 2);

        // BaseSelection round closes must not count toward Conquest rounds
        vm.ApplyPhaseChanged(GamePhase.BaseSelection);
        vm.RecordRoundClosed(new RoundClosedDto { RoundId = Guid.NewGuid(), Kind = RoundKind.Expansion });
        Assert.Equal(0, vm.ConquestRoundsCompleted);
        Assert.False(vm.SuggestEndConquest);

        // Enter Conquest with 2-cell map where 1 cell is owned
        vm.ApplyRoomState(new RoomStateDto
        {
            RoomCode = "123456",
            Phase = GamePhase.Conquest,
            Settings = new RoomSettings { ConquestRounds = 2 },
            Players =
            [
                new PlayerSummary { Id = p1, Name = "Anna", Connected = true },
                new PlayerSummary { Id = Guid.NewGuid(), Name = "Bela", Connected = true }
            ],
            Map = new MapDto(1, 2,
            [
                new TerritoryDto { Index = 0, Row = 0, Col = 0, Value = 300, IsBase = true, OwnerPlayerId = p1 },
                new TerritoryDto { Index = 1, Row = 0, Col = 1, Value = 100, IsBase = false, OwnerPlayerId = null }
            ])
        });

        Assert.False(vm.SuggestEndConquest);
        Assert.Equal(2, vm.TotalTerritories);
        Assert.Equal(1, vm.OwnedTerritories);

        // Round limit reached -> suggestion activates
        vm.RecordRoundClosed(new RoundClosedDto { RoundId = Guid.NewGuid(), Kind = RoundKind.Conquest });
        Assert.False(vm.SuggestEndConquest);
        vm.RecordRoundClosed(new RoundClosedDto { RoundId = Guid.NewGuid(), Kind = RoundKind.Conquest });
        Assert.True(vm.ReachedSuggestedMaxRounds);
        Assert.True(vm.SuggestEndConquest);
        Assert.Contains("2/2 rounds", vm.ConquestSuggestionText);

        // All territories owned via MapUpdate takes precedence
        vm.ApplyMapUpdated(new MapUpdate(
            [new TerritoryDto { Index = 1, Row = 0, Col = 1, Value = 100, IsBase = false, OwnerPlayerId = p1 }],
            []));
        Assert.True(vm.AllTerritoriesOwned);
        Assert.True(vm.SuggestEndConquest);
        Assert.Contains("All territories are owned", vm.ConquestSuggestionText);
    }

    [Fact]
    public void ApplyGameFinished_MovesToResultsUpdatesFinalMapAndClearsOpenState()
    {
        var p1 = Guid.NewGuid();
        var vm = new PhaseControlViewModel(new FakeRoomControlClient());
        vm.SetRoomOpened(new RoomSettings());
        vm.SetConnectionState(true);
        vm.ApplyPhaseChanged(GamePhase.Conquest);
        vm.SetRoundOpen(true);
        vm.ApplyPickWaiting(new PlayerSummary { Id = p1, Name = "Anna" });

        vm.ApplyGameFinished(new GameFinishedDto(
            [new FinalStanding { Rank = 1, PlayerId = p1, Name = "Anna", Score = 400, TerritoryCount = 2 }],
            new MapDto(1, 2,
            [
                new TerritoryDto { Index = 0, OwnerPlayerId = p1, IsBase = true, Value = 300 },
                new TerritoryDto { Index = 1, OwnerPlayerId = p1, IsBase = false, Value = 100 }
            ])));

        Assert.Equal(GamePhase.Results, vm.Phase);
        Assert.False(vm.IsRoundOpen);
        Assert.Equal("", vm.ActivePickerName);
        Assert.Equal(2, vm.TotalTerritories);
        Assert.Equal(2, vm.OwnedTerritories);
        Assert.False(vm.EndGameCommand.CanExecute(null));
    }

    [Fact]
    public async Task HubErrors_AndHostErrorFormatter_ProduceReadableMessages()
    {
        string? reportedError = null;
        var client = new FakeRoomControlClient
        {
            StartGameHandler = () => throw new InvalidOperationException(
                "An unexpected error occurred invoking 'StartGame' on the server. HubException: NotEnoughPlayers")
        };
        var vm = new PhaseControlViewModel(client, msg => reportedError = msg);
        vm.SetRoomOpened(new RoomSettings { MinPlayers = 2 });
        vm.SetConnectionState(true);
        vm.SetPlayerCounts(2, 2);

        await vm.StartGameAsync();

        Assert.Equal("Not enough connected players to start the game.", reportedError);
        Assert.False(vm.IsBusy);

        Assert.Equal("This action is not allowed in the current phase.", HostErrorFormatter.FormatMessage("HubException: InvalidPhase"));
        Assert.Equal("That phase transition is not allowed.", HostErrorFormatter.FormatMessage("HubException: InvalidTransition"));
        Assert.Equal("Close the active round before doing that.", HostErrorFormatter.FormatMessage("RoundInProgress"));
        Assert.Equal("There is no active round to close.", HostErrorFormatter.FormatMessage("NoActiveRound"));
        Assert.Equal("Host connection is not attached to a room.", HostErrorFormatter.FormatMessage("NotAttached"));
        Assert.Equal("This room no longer exists.", HostErrorFormatter.FormatMessage("RoomNotFound"));
        Assert.Equal("You are not authorized for this room.", HostErrorFormatter.FormatMessage("NotAuthorized"));
        Assert.Equal("This action is not supported by the connected server yet.",
            HostErrorFormatter.FormatMessage("Failed to invoke 'AdvancePhase' due to an error on the server. HubException: Method does not exist."));
        Assert.Equal("Custom REST error", HostErrorFormatter.FormatMessage("  Custom REST error  "));
    }
}

