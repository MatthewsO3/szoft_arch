using QuizGame.Contracts;
using Xunit;

namespace QuizGame.Domain.Tests;

/// <summary>Smoke tests that keep the shared contract honest until real domain tests land.</summary>
public class ContractsTests
{
    [Fact]
    public void GamePhases_AreInProgressionOrder()
    {
        Assert.Equal(0, (int)GamePhase.Lobby);
        Assert.Equal(1, (int)GamePhase.BaseSelection);
        Assert.Equal(2, (int)GamePhase.Conquest);
        Assert.Equal(3, (int)GamePhase.Results);
    }

    [Fact]
    public void HubPath_MatchesContract()
    {
        Assert.Equal("/hubs/game", HubNames.HubPath);
    }
}
