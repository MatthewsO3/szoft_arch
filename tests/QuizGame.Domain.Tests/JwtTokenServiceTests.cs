using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using QuizGame.Server.Auth;
using Xunit;

namespace QuizGame.Domain.Tests;

public class JwtTokenServiceTests
{
    private const string Key = "unit-test-signing-key-0123456789-abcdef";

    private static JwtTokenService Service(FakeTime? time = null, string key = Key, string audience = "QuizGame") =>
        new(Options.Create(new JwtOptions { Key = key, Audience = audience }), time ?? new FakeTime(Make.T0));

    [Fact]
    public void Player_token_round_trips_with_room_claim()
    {
        var jwt = Service();
        var id = Guid.NewGuid();

        var token = jwt.CreatePlayerToken(id, "123456", "Anna", out _);
        var principal = jwt.Validate(token);

        Assert.NotNull(principal);
        Assert.Equal(id.ToString(), principal!.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(Roles.Player, principal!.FindFirstValue(ClaimTypes.Role));
        Assert.Equal("123456", principal.FindFirstValue(ClaimNames.Room));
        Assert.True(principal.IsInRole(Roles.Player));
    }

    [Fact]
    public void Game_master_token_has_role_and_no_room()
    {
        var jwt = Service();
        var principal = jwt.Validate(jwt.CreateGameMasterToken(Guid.NewGuid(), "admin", out _));

        Assert.True(principal!.IsInRole(Roles.GameMaster));
        Assert.Null(principal.FindFirstValue(ClaimNames.Room));
    }

    [Fact]
    public void Tampered_payload_or_signature_is_rejected()
    {
        var jwt = Service();
        var token = jwt.CreateGameMasterToken(Guid.NewGuid(), "admin", out _);
        var parts = token.Split('.');

        var forgedPayload = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(parts[1])).Replace("GameMaster", "Player")));

        Assert.Null(jwt.Validate($"{parts[0]}.{forgedPayload}.{parts[2]}"));
        Assert.Null(jwt.Validate($"{parts[0]}.{parts[1]}.{parts[2][..^2]}xx"));
        Assert.Null(Service(key: "another-signing-key-0123456789-abcdef").Validate(token));
    }

    [Fact]
    public void Token_with_alg_none_is_rejected()
    {
        var jwt = Service();
        var parts = jwt.CreateGameMasterToken(Guid.NewGuid(), "admin", out _).Split('.');
        var none = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("""{"alg":"none","typ":"JWT"}"""));

        Assert.Null(jwt.Validate($"{none}.{parts[1]}."));
        Assert.Null(jwt.Validate($"{none}.{parts[1]}.{parts[2]}"));
    }

    [Fact]
    public void Expired_token_is_rejected_after_clock_skew()
    {
        var time = new FakeTime(Make.T0);
        var jwt = Service(time);
        var token = jwt.CreatePlayerToken(Guid.NewGuid(), "123456", "Anna", out var expires);

        time.Advance(expires - time.GetUtcNow() + TimeSpan.FromSeconds(30));
        Assert.NotNull(jwt.Validate(token)); // inside 60 s skew

        time.Advance(TimeSpan.FromSeconds(60));
        Assert.Null(jwt.Validate(token));
    }

    [Fact]
    public void Wrong_audience_and_garbage_are_rejected()
    {
        var token = Service().CreateGameMasterToken(Guid.NewGuid(), "admin", out _);

        Assert.Null(Service(audience: "other").Validate(token));
        Assert.Null(Service().Validate(null));
        Assert.Null(Service().Validate(""));
        Assert.Null(Service().Validate("a.b.c"));
        Assert.Null(Service().Validate("not a jwt"));
    }

    [Fact]
    public void Short_signing_key_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => Service(key: "too-short"));
    }
}
