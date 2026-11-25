using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using QuizGame.Contracts;
using QuizGame.Server.Auth;

namespace QuizGame.Server.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(GameMasterAuthenticator authenticator, JwtTokenService tokens) : ControllerBase
{
    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var gm = await authenticator.AuthenticateAsync(request.Username, request.Password, ct);
        if (gm is null)
            return ApiProblem.Create(StatusCodes.Status401Unauthorized, ErrorCodes.InvalidCredentials, "Wrong username or password.");

        var token = tokens.CreateGameMasterToken(gm.Id, gm.Username, out var expires);
        return new AuthResponse(token, gm.Username, expires);
    }
}
