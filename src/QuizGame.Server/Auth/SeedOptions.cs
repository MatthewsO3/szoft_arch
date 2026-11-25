namespace QuizGame.Server.Auth;

/// <summary>Seed game master account (.env: Seed__AdminUsername / Seed__AdminPassword, plan §5.2).</summary>
public sealed class SeedOptions
{
    public const string Section = "Seed";

    public string AdminUsername { get; set; } = "";

    /// <summary>Set via environment (Seed__AdminPassword). Never committed.</summary>
    public string AdminPassword { get; set; } = "";
}
