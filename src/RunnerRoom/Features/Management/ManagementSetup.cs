using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

using RunnerRoom.Configuration;
using RunnerRoom.Features.Access;
using RunnerRoom.Features.Runners;

namespace RunnerRoom.Features.Management;

internal static class ManagementSetup
{
    internal static void Configure(WebApplicationBuilder builder, RunnerOptions options)
    {
        builder.Services.AddSingleton(new ManagementGitHub(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(35) }, options));
        builder.Services.AddSingleton<RunnerManagement>();
        builder.Services.AddHostedService(s => s.GetRequiredService<RunnerManagement>());
        // Also discover new installations for monitoring, history and alerts.
        if (options.Management.Enabled && options.Management.RootDirectory is { } root)
            options.RunnersRoots = (options.RunnersRoots.Length > 0 ? options.RunnersRoots : options.RunnersRoot is { } single ? [single] : []).Append(root).Distinct().ToArray();
    }
    internal static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/management");
        group.MapGet("/", (RunnerManagement management) => Results.Ok(management.Snapshot())).WithMetadata(new OperatorAccess());
        group.MapGet("/imports", async (RunnerMonitor monitor) => Results.Ok((await monitor.GetSnapshotAsync()).Runners.Select(r => new { r.Id, r.DisplayName, r.Path, r.ServiceName, r.ProcessStatus }))).WithMetadata(new AdminAccess());
        group.MapGet("/groups/result", (RunnerManagement management) => Results.Ok(new { result = management.GroupResult })).WithMetadata(new AdminAccess());
        Post<CreateRunners>(group, "/create", (m, r, c) => m.Create(r, c));
        Post<ImportRunner>(group, "/import", (m, r, c) => m.Import(r, c));
        Post<RunnerAction>(group, "/action", (m, r, c) => m.Act(r, c), operatorAccess: true);
        Post<ConfigureRunner>(group, "/configure", (m, r, c) => m.Configure(r, c));
        Post<ManagedPool>(group, "/pool", (m, r, c) => m.Pool(r, c));
        Post<ScalePool>(group, "/scale", (m, r, c) => m.Scale(r, c));
        Post<WorkflowAction>(group, "/workflow", (m, r, c) => m.Workflow(r, c), operatorAccess: true);
        Post<GitHubGroupAction>(group, "/group", (m, r, c) => m.Group(r, c));
        Post<JsonElement>(group, "/version", (m, _, c) => m.Version(c));
        Post<CancelOperation>(group, "/cancel", (m, r, c) => { m.Cancel(r.Id); c.RequestServices.GetRequiredService<AccessAudit>().Add(c, "management_cancel"); return new { message = "Cancellation requested." }; });
    }
    private sealed record CancelOperation(string Id);
    internal static bool OperatorRunnerAction(string? action) => action is "start" or "stop" or "restart" or "drain";
    private static void Post<T>(RouteGroupBuilder group, string path, Func<RunnerManagement, T, HttpContext, object> action, bool operatorAccess = false)
    {
        group.MapPost(path, async (HttpContext context, RunnerManagement management, RunnerOptions options) =>
        {
            var request = context.Request;
            if (!request.HasJsonContentType() || request.Headers.Origin is { Count: > 0 } origin && origin.ToString() != $"{request.Scheme}://{request.Host}") return Results.StatusCode(403);
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = 32768;
            try
            {
                var input = await request.ReadFromJsonAsync<T>(context.RequestAborted);
                if (input is null) return Results.BadRequest(new { message = "Request body is required." });
                if (input is RunnerAction runnerAction && !AccessSetup.IsAdmin(context, options) && !OperatorRunnerAction(runnerAction.Action))
                {
                    context.RequestServices.GetRequiredService<AccessAudit>().Add(context, "access_denied");
                    return Results.Json(new { message = "Only administrators can register, remove or update runners." }, statusCode: 403);
                }
                return Results.Accepted(value: action(management, input, context));
            }
            catch (ManagementException ex) { return Results.BadRequest(new { message = ex.Message }); }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException or ArgumentException or NullReferenceException)
            { return Results.BadRequest(new { message = "Invalid management request. Check required fields." }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return Results.Json(new { message = "Could not persist the operation. Check state-directory permissions and free space." }, statusCode: 503); }
        }).WithMetadata(operatorAccess ? (object)new OperatorAccess() : new AdminAccess());
    }
}
