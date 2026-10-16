using System.Text.Json;
using RunnerRoom.Features.Access;
using RunnerRoom.Features.Management;

namespace RunnerRoom.Features.Maintenance;

internal static class AppUpdateEndpoints
{
    internal static void Configure(WebApplicationBuilder builder) => builder.Services.AddSingleton<AppInstallationService>();

    internal static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/updates").WithMetadata(new AdminAccess());
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapGet("/installation", (AppInstallationService installation) => Results.Ok(installation.Snapshot()));
        group.MapPost("/check", (HttpContext context, AppUpdateService updates) => Post(context, async () =>
        {
            await updates.CheckNowAsync(context.RequestAborted);
            return Results.Ok(updates.Current);
        }));
        group.MapPost("/install", (HttpContext context, AppInstallationService installation, AccessAudit audit) => Post(context, async () =>
        {
            var result = await installation.InstallAsync(context.RequestAborted);
            audit.Add(context, result.Demo ? "application_update_demo" : "application_update_requested");
            return Results.Accepted(value: result);
        }));
    }

    private static async Task<IResult> Post(HttpContext context, Func<Task<IResult>> action)
    {
        var request = context.Request;
        if (!request.HasJsonContentType() || request.Headers.Origin.Count > 0 &&
            !string.Equals(request.Headers.Origin.ToString(), $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase))
            return Results.Json(new { message = "Use the update controls on this dashboard." }, statusCode: 403);
        if (request.ContentLength > 1024) return Results.StatusCode(413);
        try
        {
            var bytes = new byte[1025];
            var length = await request.Body.ReadAtLeastAsync(bytes, bytes.Length, false, context.RequestAborted);
            if (length == bytes.Length) return Results.StatusCode(413);
            using var body = JsonDocument.Parse(bytes.AsMemory(0, length));
            if (body.RootElement.ValueKind != JsonValueKind.Object || body.RootElement.EnumerateObject().Any())
                return Results.BadRequest(new { message = "Update requests do not accept settings, commands, versions or download URLs." });
            return await action();
        }
        catch (Exception error) when (error is AppInstallationException or ManagementException)
        { return Results.Json(new { message = error.Message }, statusCode: 409); }
        catch (Exception error) when (error is JsonException or BadHttpRequestException)
        { return Results.BadRequest(new { message = "Submit an empty JSON object to use this update action." }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Results.Json(new { message = "The update request could not be saved. Check the server's update service permissions or wait for its current operation." }, statusCode: 503); }
    }
}
