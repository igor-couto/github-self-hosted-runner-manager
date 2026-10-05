using RunnerRoom;

// The installer checks the bundled runtime before replacing a working installation.
if (args is ["--check-runtime"])
{
    Console.WriteLine($"Runner Room runtime OK ({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier})");
    return;
}

var builder = WebApplication.CreateBuilder(args);
var settingsFile = builder.Configuration["SettingsFile"];
builder.Configuration.AddJsonFile(settingsFile ?? (OperatingSystem.IsLinux() ? "/etc/runner-room/settings.json" : "settings.json"),
    optional: settingsFile is null, reloadOnChange: false).AddEnvironmentVariables().AddCommandLine(args);
var options = builder.Configuration.Get<RunnerOptions>() ?? new();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new GitHubRunnerClient(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(5) }, options.GitHub));
builder.Services.AddSingleton<RunnerMonitor>();
builder.Services.AddSingleton<DetailedSystemMonitor>();
builder.Services.AddHostedService(services => services.GetRequiredService<DetailedSystemMonitor>());
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/runners", (RunnerMonitor monitor) => monitor.GetSnapshotAsync());
app.MapGet("/api/runners/{id}/logs", async (string id, string? file, RunnerMonitor monitor) =>
    await monitor.GetLogsAsync(id, file) is { } logs ? Results.Ok(logs) : Results.NotFound());
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/system", (DetailedSystemMonitor monitor) => monitor.Current is { } snapshot ? Results.Ok(snapshot) : Results.Json(new { message = "Collecting the first system sample." }, statusCode: 503));
app.Run();
