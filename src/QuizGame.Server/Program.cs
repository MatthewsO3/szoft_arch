using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using QuizGame.Contracts;
using QuizGame.Server.Auth;
using QuizGame.Server.Data;
using QuizGame.Server.Hubs;
using QuizGame.Server.Services;

// Container healthcheck self-probe: `dotnet QuizGame.Server.dll --health-check`
// calls /health on the locally bound port and exits 0/1. Avoids needing curl in
// the runtime image (plan §11).
if (args.Contains("--health-check"))
{
    return await HealthProbe.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

// ---- Configuration (secrets come from environment / .env, never from committed files) ----
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.Section));
builder.Services.Configure<SeedOptions>(builder.Configuration.GetSection(SeedOptions.Section));
builder.Services.Configure<RoomOptions>(builder.Configuration.GetSection(RoomOptions.Section));
builder.Services.Configure<RoundOptions>(builder.Configuration.GetSection(RoundOptions.Section));

foreach (var required in new[] { "Jwt:Key", "Seed:AdminUsername", "Seed:AdminPassword" })
{
    if (string.IsNullOrWhiteSpace(builder.Configuration[required]))
        throw new InvalidOperationException(
            $"Missing configuration '{required}'. Set it via environment variable '{required.Replace(":", "__")}' (see .env.example).");
}

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// ---- Room system services ----
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton(new PasswordHasher());
builder.Services.AddSingleton<GameMasterAuthenticator>();

// Persistence (plan §5): PostgreSQL via ConnectionStrings__Default. Only Development may run without it
// (in-memory game masters, no history) so the app starts on a laptop without Docker.
var connectionString = builder.Configuration.GetConnectionString("Default");
if (!string.IsNullOrWhiteSpace(connectionString))
    builder.Services.AddEfPersistence(connectionString);
else if (builder.Environment.IsDevelopment())
    builder.Services.AddInMemoryPersistence();
else
    throw new InvalidOperationException("Missing configuration 'ConnectionStrings:Default' (env ConnectionStrings__Default, see .env.example).");
builder.Services.AddSingleton<GameRoomManager>();
builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddSingleton<RoomCommandGate>();
builder.Services.AddSingleton<PhaseCoordinator>();
builder.Services.AddSingleton<RoundCoordinator>();

// Question source for rounds: the question bank (EF Core, or in-memory in Development without a database).
builder.Services.AddSingleton<IQuestionProvider, BankQuestionProvider>();
builder.Services.AddHostedService<RoomCleanupService>();

builder.Services.AddControllers();
builder.Services.AddSignalR(o => o.EnableDetailedErrors = builder.Environment.IsDevelopment());

builder.Services
    .AddAuthentication(QuizJwtDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, QuizJwtHandler>(QuizJwtDefaults.Scheme, null);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.GameMasterOnly, p => p.RequireRole(Roles.GameMaster))
    .AddPolicy(Policies.PlayerOnly, p => p.RequireRole(Roles.Player));

// Behind the Caddy reverse proxy (plan D10) the real client IP / scheme / host arrive as X-Forwarded-*.
// The API port must therefore not be published directly (docker-compose only `expose`s it).
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

// Throttle room-code guessing (join / exists) and brute-force login (plan §8).
// Join limit is generous: a whole classroom may share one public IP.
var joinPerMinute = builder.Configuration.GetValue("RateLimits:JoinPerMinute", 120);
var loginPerMinute = builder.Configuration.GetValue("RateLimits:LoginPerMinute", 10);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("join", ctx => PerIp(ctx, joinPerMinute));
    o.AddPolicy("login", ctx => PerIp(ctx, loginPerMinute));

    static RateLimitPartition<string> PerIp(HttpContext ctx, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1) });
});

var app = builder.Build();

await app.Services.GetRequiredService<IPersistenceInitializer>().InitializeAsync();

app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Serve the player web app from wwwroot (single origin, no CORS).
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Liveness probe used by Docker / Caddy.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapControllers();
app.MapHub<GameHub>(HubNames.HubPath);

app.Run();
return 0;

/// <summary>Minimal self-probe for the Docker healthcheck.</summary>
static class HealthProbe
{
    public static async Task<int> RunAsync()
    {
        // Match the container's bound port (ASPNETCORE_URLS, default 8080).
        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://+:8080";
        var port = urls.Split(':').LastOrDefault()?.TrimEnd('/') ?? "8080";
        var url = $"http://localhost:{port}/health";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = await client.GetAsync(url);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }
}
