using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace QuizGame.Server.Auth;

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    /// <summary>HMAC signing key (≥ 32 bytes). Never committed: set via environment (Jwt__Key).</summary>
    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "QuizGame";
    public string Audience { get; set; } = "QuizGame";
    public int GameMasterTokenHours { get; set; } = 8;
    public int PlayerTokenHours { get; set; } = 12;
    public int ClockSkewSeconds { get; set; } = 60;
}

/// <summary>
/// Minimal HS256 JWT issuer/validator built on the shared framework only (plan §6.1).
/// Algorithm is pinned to HS256 (the header's alg is checked, so "none" is rejected).
/// Can be swapped for Microsoft.AspNetCore.Authentication.JwtBearer without touching callers.
/// </summary>
public sealed class JwtTokenService
{
    private const string HeaderJson = """{"alg":"HS256","typ":"JWT"}""";

    private readonly JwtOptions _options;
    private readonly TimeProvider _time;
    private readonly byte[] _key;

    public JwtTokenService(IOptions<JwtOptions> options, TimeProvider time)
    {
        _options = options.Value;
        _time = time;
        _key = Encoding.UTF8.GetBytes(_options.Key);
        if (_key.Length < 32)
            throw new InvalidOperationException("Jwt:Key must be at least 32 bytes long.");
    }

    public string CreateGameMasterToken(Guid gameMasterId, string username, out DateTimeOffset expiresAt) =>
        Create(gameMasterId, Roles.GameMaster, roomCode: null, username, _options.GameMasterTokenHours, out expiresAt);

    public string CreatePlayerToken(Guid playerId, string roomCode, string name, out DateTimeOffset expiresAt) =>
        Create(playerId, Roles.Player, roomCode, name, _options.PlayerTokenHours, out expiresAt);

    private string Create(Guid subject, string role, string? roomCode, string name, int hours, out DateTimeOffset expiresAt)
    {
        var now = _time.GetUtcNow();
        expiresAt = now.AddHours(hours);

        var payload = new Dictionary<string, object>
        {
            ["sub"] = subject.ToString(),
            ["role"] = role,
            ["name"] = name,
            ["iss"] = _options.Issuer,
            ["aud"] = _options.Audience,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = expiresAt.ToUnixTimeSeconds(),
        };
        if (roomCode is not null) payload[ClaimNames.Room] = roomCode;

        var header = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(HeaderJson));
        var body = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signingInput = $"{header}.{body}";
        return $"{signingInput}.{Sign(signingInput)}";
    }

    /// <summary>Returns the principal for a valid token, or null for anything invalid or expired.</summary>
    public ClaimsPrincipal? Validate(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var parts = token.Split('.');
        if (parts.Length != 3) return null;

        try
        {
            using var header = JsonDocument.Parse(WebEncoders.Base64UrlDecode(parts[0]));
            if (header.RootElement.ValueKind != JsonValueKind.Object ||
                !header.RootElement.TryGetProperty("alg", out var alg) ||
                alg.GetString() != "HS256")
                return null;

            var expected = Encoding.ASCII.GetBytes(Sign($"{parts[0]}.{parts[1]}"));
            if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(parts[2])))
                return null;

            using var payload = JsonDocument.Parse(WebEncoders.Base64UrlDecode(parts[1]));
            var root = payload.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (Str(root, "iss") != _options.Issuer || Str(root, "aud") != _options.Audience) return null;
            if (!root.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var expSeconds)) return null;
            var skew = TimeSpan.FromSeconds(_options.ClockSkewSeconds);
            if (DateTimeOffset.FromUnixTimeSeconds(expSeconds) + skew < _time.GetUtcNow()) return null;

            var sub = Str(root, "sub");
            var role = Str(root, "role");
            if (!Guid.TryParse(sub, out _) || role is not (Roles.GameMaster or Roles.Player)) return null;

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, sub!),
                new(ClaimTypes.Role, role),
                new(ClaimTypes.Name, Str(root, "name") ?? ""),
            };
            if (Str(root, ClaimNames.Room) is { } room) claims.Add(new Claim(ClaimNames.Room, room));

            return new ClaimsPrincipal(new ClaimsIdentity(claims, "QuizJwt", ClaimTypes.Name, ClaimTypes.Role));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private string Sign(string signingInput) =>
        WebEncoders.Base64UrlEncode(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(signingInput)));

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
