using RunnerRoom;

// The installer checks the bundled runtime before replacing a working installation.
if (args is ["--check-runtime"])
{
    Console.WriteLine($"Runner Room runtime OK ({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier})");
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(new RunnerMonitor(builder.Configuration["RunnersRoot"], builder.Configuration.GetValue<bool>("Demo")));
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
app.MapGet("/api/runners", (RunnerMonitor monitor) => monitor.GetSnapshot());
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.Run();
