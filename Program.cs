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
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.Run();
