using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace QuizGame.Server.Data;

/// <summary>
/// Used only by `dotnet ef` (migrations add / database update). It lets the tooling build the model without
/// starting the app or needing a running database. Set ConnectionStrings__Default to target a real one.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<QuizDbContext>
{
    public QuizDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
                         ?? "Host=localhost;Port=5432;Database=quizgame;Username=quizgame;Password=design-time-only";

        var options = new DbContextOptionsBuilder<QuizDbContext>().UseNpgsql(connection).Options;
        return new QuizDbContext(options);
    }
}
