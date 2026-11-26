using QuizGame.Contracts;
using QuizGame.Host.ViewModels;

namespace QuizGame.Host.Tests;

public class RoomSettingsViewModelTests
{
    [Fact]
    public void InitialState_UsesPlanDefaultsAndPreRoomMode()
    {
        var client = new FakeRoomControlClient();
        var vm = new RoomSettingsViewModel(client);

        Assert.Equal(MapPreset.Medium, vm.SelectedMapPreset.Value);
        Assert.Equal("100", vm.TerritoryValueText);
        Assert.Equal("300", vm.BaseValueText);
        Assert.Equal("1", vm.WinnersPerRoundText);
        Assert.Equal("2", vm.MinPlayersText);
        Assert.Equal("20", vm.PickTimeoutSecondsText);
        Assert.Equal("20", vm.DefaultQuestionSecondsText);
        Assert.Equal("10", vm.SuggestedMaxRoundsText);
        Assert.False(vm.DisallowAdjacentBases);

        Assert.False(vm.HasRoom);
        Assert.True(vm.CanEdit);
        Assert.False(vm.IsLocked);
        Assert.False(vm.ShowApplyControls);
        Assert.False(vm.HasUnsavedChanges);
        Assert.False(vm.ApplySettingsCommand.CanExecute(null));
        Assert.False(vm.RevertCommand.CanExecute(null));
        Assert.False(vm.ResetDefaultsCommand.CanExecute(null));

        Assert.True(vm.TryBuildSettings(out var built, out var error));
        Assert.Equal("", error);
        Assert.Equal(new RoomSettings(), built);
    }

    [Theory]
    [InlineData("territory", "0")]
    [InlineData("territory", "-5")]
    [InlineData("territory", "abc")]
    [InlineData("territory", "")]
    [InlineData("base", "0")]
    [InlineData("base", "12.5")]
    [InlineData("winners", "0")]
    [InlineData("winners", "-1")]
    [InlineData("minPlayers", "0")]
    [InlineData("minPlayers", "nope")]
    [InlineData("pickTimeout", "0")]
    [InlineData("pickTimeout", "-10")]
    [InlineData("questionSeconds", "0")]
    [InlineData("questionSeconds", " ")]
    [InlineData("maxRounds", "0")]
    [InlineData("maxRounds", "-3")]
    public void TryGetForRoomCreation_InvalidNumericFields_FailWithError(string field, string badValue)
    {
        var vm = new RoomSettingsViewModel(new FakeRoomControlClient());

        switch (field)
        {
            case "territory": vm.TerritoryValueText = badValue; break;
            case "base": vm.BaseValueText = badValue; break;
            case "winners": vm.WinnersPerRoundText = badValue; break;
            case "minPlayers": vm.MinPlayersText = badValue; break;
            case "pickTimeout": vm.PickTimeoutSecondsText = badValue; break;
            case "questionSeconds": vm.DefaultQuestionSecondsText = badValue; break;
            case "maxRounds": vm.SuggestedMaxRoundsText = badValue; break;
        }

        Assert.True(vm.HasUnsavedChanges);
        Assert.True(vm.ResetDefaultsCommand.CanExecute(null));

        var ok = vm.TryGetForRoomCreation(out _);

        Assert.False(ok);
        Assert.NotEmpty(vm.ValidationError);
    }

    [Fact]
    public void SelectedMapPreset_NullCoercion_FallsBackToMediumAndNotifies()
    {
        var vm = new RoomSettingsViewModel(new FakeRoomControlClient());
        var presetChangedCount = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RoomSettingsViewModel.SelectedMapPreset))
                presetChangedCount++;
        };

        vm.SelectedMapPreset = vm.MapPresets.Single(p => p.Value == MapPreset.Large);
        Assert.Equal(MapPreset.Large, vm.SelectedMapPreset.Value);
        var beforeNullCount = presetChangedCount;

        vm.SelectedMapPreset = null!;

        Assert.Equal(MapPreset.Medium, vm.SelectedMapPreset.Value);
        Assert.Equal(beforeNullCount + 1, presetChangedCount);

        // Coercing null when already Medium still notifies WPF so the ComboBox stays bound
        vm.SelectedMapPreset = null!;
        Assert.Equal(MapPreset.Medium, vm.SelectedMapPreset.Value);
        Assert.Equal(beforeNullCount + 2, presetChangedCount);
    }

    [Fact]
    public async Task LobbyEditing_ApplyRevertAndResetDefaults_WorkAndInvokeCallback()
    {
        var client = new FakeRoomControlClient { IsConnected = true };
        RoomSettings? callbackSettings = null;
        var vm = new RoomSettingsViewModel(client, s => callbackSettings = s);

        vm.SetConnectionState(true);
        vm.SetRoomCreated(new RoomSettings());

        Assert.True(vm.HasRoom);
        Assert.True(vm.ShowApplyControls);
        Assert.True(vm.CanEdit);
        Assert.False(vm.HasUnsavedChanges);
        Assert.False(vm.ApplySettingsCommand.CanExecute(null));
        Assert.False(vm.RevertCommand.CanExecute(null));

        // Edit settings in Lobby
        vm.SelectedMapPreset = vm.MapPresets.Single(p => p.Value == MapPreset.Small);
        vm.TerritoryValueText = "120";
        vm.BaseValueText = "450";
        vm.WinnersPerRoundText = "2";
        vm.MinPlayersText = "4";
        vm.PickTimeoutSecondsText = "15";
        vm.DefaultQuestionSecondsText = "25";
        vm.SuggestedMaxRoundsText = "8";
        vm.DisallowAdjacentBases = true;

        Assert.True(vm.HasUnsavedChanges);
        Assert.True(vm.ApplySettingsCommand.CanExecute(null));
        Assert.True(vm.RevertCommand.CanExecute(null));
        Assert.True(vm.ResetDefaultsCommand.CanExecute(null));

        // Revert discards unapplied edits
        vm.RevertCommand.Execute(null);
        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal(MapPreset.Medium, vm.SelectedMapPreset.Value);
        Assert.Equal("100", vm.TerritoryValueText);
        Assert.False(vm.DisallowAdjacentBases);

        // Edit again and apply
        vm.SelectedMapPreset = vm.MapPresets.Single(p => p.Value == MapPreset.Large);
        vm.MinPlayersText = "5";
        vm.DisallowAdjacentBases = true;
        Assert.True(vm.ApplySettingsCommand.CanExecute(null));

        await vm.ApplySettingsAsync();

        var sent = Assert.Single(client.UpdatedSettings);
        Assert.Equal(MapPreset.Large, sent.MapPreset);
        Assert.Equal(5, sent.MinPlayers);
        Assert.True(sent.DisallowAdjacentBases);
        Assert.Equal(sent, vm.AppliedSettings);
        Assert.Equal(sent, callbackSettings);
        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal("Settings applied.", vm.StatusMessage);
        Assert.Equal("", vm.ValidationError);
        Assert.False(vm.ApplySettingsCommand.CanExecute(null));

        // Reset defaults loads default values into form without sending yet
        vm.ResetDefaultsCommand.Execute(null);
        Assert.Equal(MapPreset.Medium, vm.SelectedMapPreset.Value);
        Assert.Equal("2", vm.MinPlayersText);
        Assert.False(vm.DisallowAdjacentBases);
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task ApplySettingsAsync_HubError_FormatsErrorAndKeepsAppliedSettings()
    {
        var client = new FakeRoomControlClient
        {
            UpdateSettingsHandler = _ => throw new InvalidOperationException("HubException: InvalidSettings")
        };
        var vm = new RoomSettingsViewModel(client);
        vm.SetConnectionState(true);
        vm.SetRoomCreated(new RoomSettings { MinPlayers = 2 });

        vm.MinPlayersText = "6";
        await vm.ApplySettingsAsync();

        Assert.Equal(2, vm.AppliedSettings.MinPlayers);
        Assert.Equal("Room settings contain an invalid or out-of-range value.", vm.ValidationError);
        Assert.Equal("", vm.StatusMessage);
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public void SyncFromRoomState_PopulatesFieldsAndClearsDirtyState()
    {
        var vm = new RoomSettingsViewModel(new FakeRoomControlClient());
        vm.MinPlayersText = "9";

        var serverSettings = new RoomSettings
        {
            MapPreset = MapPreset.Small,
            TerritoryValue = 200,
            BaseValue = 600,
            WinnersPerRound = 3,
            MinPlayers = 4,
            PickTimeoutSeconds = 30,
            DefaultQuestionSeconds = 45,
            SuggestedMaxRounds = 15,
            DisallowAdjacentBases = true
        };

        vm.SyncFromRoomState(new RoomStateDto
        {
            RoomCode = "123456",
            Phase = GamePhase.Lobby,
            Settings = serverSettings
        });

        Assert.True(vm.HasRoom);
        Assert.Equal(serverSettings, vm.AppliedSettings);
        Assert.Equal(MapPreset.Small, vm.SelectedMapPreset.Value);
        Assert.Equal("200", vm.TerritoryValueText);
        Assert.Equal("600", vm.BaseValueText);
        Assert.Equal("3", vm.WinnersPerRoundText);
        Assert.Equal("4", vm.MinPlayersText);
        Assert.Equal("30", vm.PickTimeoutSecondsText);
        Assert.Equal("45", vm.DefaultQuestionSecondsText);
        Assert.Equal("15", vm.SuggestedMaxRoundsText);
        Assert.True(vm.DisallowAdjacentBases);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void LeavingLobby_LocksEditingAndRevertsUnappliedChanges()
    {
        var vm = new RoomSettingsViewModel(new FakeRoomControlClient());
        vm.SetConnectionState(true);
        vm.SetRoomCreated(new RoomSettings { MinPlayers = 3 });

        // Make unapplied edit while in Lobby
        vm.MinPlayersText = "7";
        Assert.True(vm.HasUnsavedChanges);
        Assert.True(vm.CanEdit);

        foreach (var lockedPhase in new[] { GamePhase.BaseSelection, GamePhase.Conquest, GamePhase.Results })
        {
            vm.SetPhase(lockedPhase);

            Assert.False(vm.CanEdit);
            Assert.True(vm.IsLocked);
            Assert.Equal("3", vm.MinPlayersText);
            Assert.False(vm.HasUnsavedChanges);
            Assert.False(vm.ApplySettingsCommand.CanExecute(null));
            Assert.False(vm.RevertCommand.CanExecute(null));
            Assert.False(vm.ResetDefaultsCommand.CanExecute(null));
            Assert.Contains("locked", vm.ModeHint, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SetConnectionState_TogglesApplyCommandAvailability()
    {
        var vm = new RoomSettingsViewModel(new FakeRoomControlClient());
        vm.SetRoomCreated(new RoomSettings());
        vm.MinPlayersText = "4";

        Assert.False(vm.ApplySettingsCommand.CanExecute(null));

        vm.SetConnectionState(true);
        Assert.True(vm.ApplySettingsCommand.CanExecute(null));

        vm.SetConnectionState(false);
        Assert.False(vm.ApplySettingsCommand.CanExecute(null));
    }

    [Fact]
    public void ClearRoom_PreservesAppliedSettings_WhileResetToDefaultsClearsEverything()
    {
        var vm = new RoomSettingsViewModel(new FakeRoomControlClient());
        var custom = new RoomSettings { MapPreset = MapPreset.Large, MinPlayers = 4 };
        vm.SetRoomCreated(custom);
        vm.TerritoryValueText = "999";

        vm.ClearRoom();
        Assert.False(vm.HasRoom);
        Assert.True(vm.CanEdit);
        Assert.Equal(custom, vm.AppliedSettings);
        Assert.Equal("100", vm.TerritoryValueText);
        Assert.Equal("4", vm.MinPlayersText);
        Assert.Equal(MapPreset.Large, vm.SelectedMapPreset.Value);

        vm.ResetToDefaults();
        Assert.Equal(new RoomSettings(), vm.AppliedSettings);
        Assert.Equal("2", vm.MinPlayersText);
        Assert.Equal(MapPreset.Medium, vm.SelectedMapPreset.Value);
        Assert.False(vm.IsConnected);
    }
}
