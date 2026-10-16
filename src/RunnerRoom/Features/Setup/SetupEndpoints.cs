using System.Text.Json;
using RunnerRoom.Configuration;
using RunnerRoom.Features.Access;

namespace RunnerRoom.Features.Setup;

internal static class SetupEndpoints
{
    internal static void Map(WebApplication app, RunnerOptions options)
    {
        var group = app.MapGroup("/api/setup");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapGet("/status", (SetupService setup) => Results.Ok(setup.Status()));
        group.MapGet("/config", (HttpContext context, SetupService setup) =>
            setup.Authorized(context) ? Results.Ok(setup.Configuration(Origin(context))) : Forbidden());
        group.MapPost("/discover", (HttpContext context, SetupService setup) =>
            Post<SetupDiscoverRequest>(context, setup, async input => await setup.DiscoverAsync(input.Token, context.RequestAborted)));
        group.MapPost("/save", (HttpContext context, SetupService setup) =>
            Post<SetupSaveRequest>(context, setup, async input =>
            {
                var result = await setup.SaveAsync(input, Origin(context), context.RequestAborted);
                context.RequestServices.GetRequiredService<AccessAudit>().Add(context, options.Demo ? "setup_demo_saved" : "management_setup_saved");
                return result;
            }));
    }
    private static string Origin(HttpContext context) => $"{context.Request.Scheme}://{context.Request.Host}";
    private static IResult Forbidden() => Results.Json(new { message = "Sign in as an administrator to configure runner management." }, statusCode: 403);
    private static async Task<IResult> Post<T>(HttpContext context, SetupService setup, Func<T, Task<object>> action)
    {
        var request = context.Request;
        if (!setup.Authorized(context)) return Forbidden();
        if (!request.HasJsonContentType() || request.Headers.Origin.Count > 0 && !string.Equals(request.Headers.Origin.ToString(), Origin(context), StringComparison.OrdinalIgnoreCase))
            return Results.Json(new { message = "Open setup from this dashboard and submit JSON requests." }, statusCode: 403);
        if (request.ContentLength > 32768) return Results.StatusCode(413);
        try
        {
            var bytes = new byte[32769];
            var length = await request.Body.ReadAtLeastAsync(bytes, bytes.Length, false, context.RequestAborted);
            if (length == bytes.Length) return Results.StatusCode(413);
            var input = JsonSerializer.Deserialize<T>(bytes.AsSpan(0, length), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (input is null) return Results.BadRequest(new { message = "Setup settings are required." });
            return Results.Ok(await action(input));
        }
        catch (SetupException error) { return Results.BadRequest(new { message = error.Message }); }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException) { return Results.BadRequest(new { message = "Check the setup fields and try again." }); }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        { return Results.Json(new { message = "GitHub verification took too long. No settings were saved. Try fewer scopes or retry later." }, statusCode: 504); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Management.ManagementException)
        { return Results.Json(new { message = "Setup could not save server settings. Check that the Runner Room account can write its state directory and that sufficient disk space is available." }, statusCode: 503); }
    }
}
