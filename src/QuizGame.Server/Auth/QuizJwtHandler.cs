using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace QuizGame.Server.Auth;

public static class QuizJwtDefaults
{
    public const string Scheme = "QuizJwt";
}

/// <summary>
/// Reads the bearer token from the Authorization header. For SignalR paths under /hubs the browser
/// WebSocket cannot send headers, so the token is also accepted from the access_token query string (plan §6.1).
/// </summary>
public sealed class QuizJwtHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    JwtTokenService tokens)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? token = null;

        var header = Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = header["Bearer ".Length..].Trim();
        else if (Request.Path.StartsWithSegments("/hubs"))
            token = Request.Query["access_token"].ToString();

        if (string.IsNullOrEmpty(token))
            return Task.FromResult(AuthenticateResult.NoResult());

        var principal = tokens.Validate(token);
        if (principal is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid or expired token."));

        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
