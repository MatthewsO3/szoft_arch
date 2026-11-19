// Container healthcheck self-probe: `dotnet QuizGame.Server.dll --health-check`
// calls /health on the locally bound port and exits 0/1. Avoids needing curl in
// the runtime image (plan §11).
if (args.Contains("--health-check"))
{
    return await HealthProbe.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Serve the player web app from wwwroot (single origin, no CORS).
app.UseDefaultFiles();
app.UseStaticFiles();

// Liveness probe used by Docker / Caddy.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

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
