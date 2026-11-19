namespace QuizGame.Contracts;

/// <summary>GM login / register request (plan §6.1, §6.2).</summary>
public sealed record LoginRequest(string Username, string Password);

/// <summary>Issued JWT plus who it belongs to.</summary>
public sealed record AuthResponse(string Token, string Username, DateTimeOffset ExpiresAt);
