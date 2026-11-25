using System.Security.Claims;

namespace QuizGame.Server.Auth;

public static class Policies
{
    public const string GameMasterOnly = nameof(GameMasterOnly);
    public const string PlayerOnly = nameof(PlayerOnly);
}

public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
}
