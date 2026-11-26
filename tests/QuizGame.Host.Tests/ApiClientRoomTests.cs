using System.Net;
using System.Text.Json;
using QuizGame.Contracts;
using QuizGame.Host.Services;

namespace QuizGame.Host.Tests;

public class ApiClientRoomTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task CreateRoomAsync_WithoutSettings_SendsNullBodyAndDeserializesResponse()
    {
        var sessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var expected = new CreateRoomResponse(sessionId, "123456", "https://example.test/?code=123456");

        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(StubHttpMessageHandler.JsonResponse(HttpStatusCode.Created, JsonSerializer.Serialize(expected, WebJson))));

        using var client = new ApiClient("http://localhost:5084", handler) { Token = "gm-jwt" };

        var actual = await client.CreateRoomAsync();

        Assert.Equal(expected, actual);
        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("/api/rooms", call.PathAndQuery);
        Assert.Equal("Bearer gm-jwt", call.Authorization);
        Assert.Null(call.Body);
    }

    [Fact]
    public async Task CreateRoomAsync_WithSettings_SerializesRoomSettingsAndDeserializesResponse()
    {
        var sessionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var expected = new CreateRoomResponse(sessionId, "654321", "http://localhost:5084/?code=654321");

        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(StubHttpMessageHandler.JsonResponse(HttpStatusCode.Created, JsonSerializer.Serialize(expected, WebJson))));

        using var client = new ApiClient("http://localhost:5084", handler) { Token = "gm-jwt" };

        var settings = new RoomSettings
        {
            MapPreset = MapPreset.Large,
            TerritoryValue = 150,
            BaseValue = 500,
            ConquestRounds = 2,
            MinPlayers = 3,
            PickTimeoutSeconds = 25,
            DefaultQuestionSeconds = 30,
            DuelRounds = 12,
            DisallowAdjacentBases = true
        };

        var actual = await client.CreateRoomAsync(settings);

        Assert.Equal(expected, actual);
        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("/api/rooms", call.PathAndQuery);
        Assert.NotNull(call.Body);

        var deserialized = JsonSerializer.Deserialize<RoomSettings>(call.Body!, WebJson);
        Assert.Equal(settings, deserialized);

        using var doc = JsonDocument.Parse(call.Body!);
        Assert.Equal((int)MapPreset.Large, doc.RootElement.GetProperty("mapPreset").GetInt32());
        Assert.True(doc.RootElement.GetProperty("disallowAdjacentBases").GetBoolean());
    }

    [Fact]
    public async Task CreateRoomAsync_WithInvalidSettings_SurfacesProblemDetailsAsApiException()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(StubHttpMessageHandler.JsonResponse(
                HttpStatusCode.BadRequest,
                """{"title":"Bad Request","detail":"Room settings contain an out-of-range value."}""")));

        using var client = new ApiClient("http://localhost:5084", handler) { Token = "gm-jwt" };

        var ex = await Assert.ThrowsAsync<ApiException>(() => client.CreateRoomAsync(new RoomSettings { MinPlayers = 0 }));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("Room settings contain an out-of-range value.", ex.Message);
    }
}

