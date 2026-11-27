using System.Text.Json;
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
        Assert.Equal(3, (int)GamePhase.Expansion);
        Assert.Equal(4, (int)GamePhase.Duel);
        Assert.Equal(5, (int)GamePhase.Results);
    }

    [Fact]
    public void HubPath_MatchesContract()
    {
        Assert.Equal("/hubs/game", HubNames.HubPath);
    }

    [Fact]
    public void PickWaitingDto_SerializesWithExpectedCamelCaseProperties()
    {
        var dto = new PickWaitingDto(
            new PlayerSummary { Id = Guid.NewGuid(), Name = "Anna", ColorHex = "#e6194b", Connected = true },
            new DateTimeOffset(2026, 11, 27, 12, 0, 20, TimeSpan.Zero),
            PickPurpose.Base);

        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("picker", out var picker));
        Assert.Equal("Anna", picker.GetProperty("name").GetString());
        Assert.True(doc.RootElement.TryGetProperty("pickDeadlineUtc", out _));
        Assert.Equal((int)PickPurpose.Base, doc.RootElement.GetProperty("purpose").GetInt32());
    }
}
