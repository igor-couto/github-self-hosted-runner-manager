using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using RunnerRoom.Configuration;
using RunnerRoom.Features.Access;

namespace RunnerRoom.Features.GitHub;

internal static class GitHubSyncSetup
{
    internal static void Configure(WebApplicationBuilder builder, RunnerOptions options)
    {
        builder.Services.AddSingleton(new GitHubCredentialProvider(Client(), options.GitHub));
        builder.Services.AddSingleton(s => new GitHubSyncClient(Client(), s.GetRequiredService<GitHubCredentialProvider>()));
        builder.Services.AddSingleton<GitHubSyncService>();
        builder.Services.AddHostedService(s => s.GetRequiredService<GitHubSyncService>());
        builder.Services.AddSingleton<GitHubWorkflowScanner>();
        builder.Services.AddSingleton<GitHubWebhookReceiver>();
    }
    private static HttpClient Client() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
    private sealed record Selection(string[] Repositories, int? HistoryDays, int? MaxRunsPerRepository);
    private sealed record Refresh(string? Repository, long? RunId);
    internal static void Map(WebApplication app, RunnerOptions options)
    {
        app.MapGet("/api/github/sync", (GitHubSyncService sync, HttpContext ctx) =>
        {
            var snapshot = sync.Snapshot();
            return AccessSetup.IsAdmin(ctx, options) ? snapshot : snapshot with { Repositories = snapshot.Repositories.Where(r => r.Selected).ToArray() };
        });
        app.MapPost("/api/github/discover", (HttpContext ctx, GitHubSyncService sync) => Action(ctx, async () => Results.Ok(await sync.DiscoverAsync(ctx.RequestAborted)), "github_discovery")).WithMetadata(new AdminAccess());
        app.MapPut("/api/github/selection", (HttpContext ctx, GitHubSyncService sync) => Action(ctx, async () =>
        {
            var input = await ctx.Request.ReadFromJsonAsync<Selection>(ctx.RequestAborted);
            if (input is null) return Results.BadRequest(new { message = "Choose repositories to follow." });
            await sync.SelectAsync(input.Repositories, ctx.RequestAborted, input.HistoryDays, input.MaxRunsPerRepository);
            return Results.Ok(new { message = "Repository selection saved." });
        }, "github_selection_updated")).WithMetadata(new AdminAccess());
        app.MapPost("/api/github/sync", (HttpContext ctx, GitHubSyncService sync) => Action(ctx, async () =>
        {
            var input = await ctx.Request.ReadFromJsonAsync<Refresh>(ctx.RequestAborted);
            return sync.Queue(input?.Repository, input?.RunId) ? Results.Accepted(value: new { message = "Synchronization queued." }) :
                Results.Json(new { message = "Synchronization is running, paused by GitHub, or needs a configured credential and selected repository." }, statusCode: 409);
        }, "github_sync_requested")).WithMetadata(new OperatorAccess());
        app.MapPost("/api/github/cancel", (HttpContext ctx, GitHubSyncService sync) => Action(ctx, () =>
        { sync.Cancel(); return Task.FromResult<IResult>(Results.Ok(new { message = "Synchronization stopped." })); }, "github_sync_canceled")).WithMetadata(new OperatorAccess());
        app.MapGet("/api/github/workflows", async (string repository, GitHubSyncService sync, GitHubWorkflowScanner scanner, HttpContext ctx) =>
        {
            try
            {
                repository = GitHubSyncClient.Repository(repository);
                if (!sync.Snapshot().Repositories.Any(r => r.Selected && r.FullName == repository)) return Results.BadRequest(new { message = "Follow this repository before inspecting its workflows." });
                if (options.Demo) return Results.Ok(new GitHubWorkflowScan(repository, DateTimeOffset.UtcNow,
                    [new("build.yml", "Build & test", [new("build", "Build", ["self-hosted", "ARM64"], null, false, true)])], ["Illustrative demo requirements."]));
                return Results.Ok(await scanner.ScanAsync(repository, ctx.RequestAborted));
            }
            catch (GitHubSyncException ex) { return Results.BadRequest(new { message = ex.Message }); }
        }).WithMetadata(new OperatorAccess());
        app.MapGet("/api/github/events", async (HttpContext ctx, GitHubSyncService sync) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            string? previous = null;
            // Reconnect regularly so authentication and account permissions are checked again.
            try
            {
                for (var i = 0; i < 12; i++)
                {
                    var state = sync.Snapshot();
                    var current = JsonSerializer.Serialize(new { state.Progress, state.History, state.Api, state.Repositories, state.Warning });
                    await ctx.Response.WriteAsync(current != previous ? "event: sync\ndata: {\"changed\":true}\n\n" : ": heartbeat\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted); previous = current;
                    await Task.Delay(TimeSpan.FromSeconds(5), ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
        });
        app.MapPost("/hooks/github", (HttpContext ctx, GitHubWebhookReceiver receiver) => receiver.Receive(ctx));
    }
    private static async Task<IResult> Action(HttpContext ctx, Func<Task<IResult>> action, string audit)
    {
        if (!ctx.Request.HasJsonContentType() || ctx.Request.Headers.Origin is { Count: > 0 } origin && origin.ToString() != $"{ctx.Request.Scheme}://{ctx.Request.Host}") return Results.StatusCode(403);
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = 32768;
        try
        {
            var result = await action();
            if (result is not IStatusCodeHttpResult status || status.StatusCode is null or >= 200 and < 300)
                ctx.RequestServices.GetRequiredService<AccessAudit>().Add(ctx, audit);
            return result;
        }
        catch (GitHubSyncException ex) { return Results.BadRequest(new { message = ex.Message }); }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException) { return Results.BadRequest(new { message = "Check the request values and try again." }); }
    }
}
