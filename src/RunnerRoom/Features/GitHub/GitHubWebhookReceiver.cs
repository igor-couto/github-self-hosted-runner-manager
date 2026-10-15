using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RunnerRoom.Configuration;
using RunnerRoom.Features.Runners;

namespace RunnerRoom.Features.GitHub;

public sealed class GitHubWebhookReceiver(RunnerOptions options, GitHubSyncService sync)
{
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> deliveries = new();
    internal static bool ValidSignature(byte[] body, string secret, string signature)
    {
        if (!signature.StartsWith("sha256=", StringComparison.Ordinal) || signature.Length != 71) return false;
        try { return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body), Convert.FromHexString(signature[7..])); }
        catch (FormatException) { return false; }
    }
    public async Task<IResult> Receive(HttpContext context)
    {
        var secret = options.GitHub.WebhookSecretFile is { } path ? LocalRunnerReader.ReadSmall(path, 8192)?.Trim() : null;
        if (options.Demo || !options.GitHub.Sync.Enabled || string.IsNullOrWhiteSpace(secret)) return Results.NotFound();
        if (context.Request.ContentLength > 1048576) return Results.StatusCode(413);
        var buffer = new byte[1048577];
        var length = await context.Request.Body.ReadAtLeastAsync(buffer, buffer.Length, false, context.RequestAborted);
        if (length == buffer.Length) return Results.StatusCode(413);
        var body = buffer[..length];
        if (!ValidSignature(body, secret, context.Request.Headers["X-Hub-Signature-256"].ToString())) return Results.StatusCode(403);
        var delivery = context.Request.Headers["X-GitHub-Delivery"].ToString();
        if (!Guid.TryParse(delivery, out _)) return Results.BadRequest();
        lock (gate)
        {
            foreach (var key in deliveries.Where(p => p.Value < DateTimeOffset.UtcNow.AddHours(-1)).Select(p => p.Key).ToArray()) deliveries.Remove(key);
            if (deliveries.ContainsKey(delivery)) return Results.Ok(new { message = "Delivery already received." });
            if (deliveries.Count >= 1000) return Results.StatusCode(429);
            deliveries[delivery] = DateTimeOffset.UtcNow;
        }
        var kind = context.Request.Headers["X-GitHub-Event"].ToString();
        if (kind is not ("workflow_run" or "workflow_job")) return Results.Ok(new { message = "Event acknowledged." });
        try
        {
            using var json = JsonDocument.Parse(body, new() { MaxDepth = 48 });
            var root = json.RootElement;
            var repo = GitHubSyncClient.Repository(GitHubSyncClient.Text(root.GetProperty("repository"), "full_name") ?? "");
            if (!sync.Snapshot().Repositories.Any(r => r.Selected && r.FullName == repo)) return Results.Ok(new { message = "Repository is not followed." });
            // Treat delivery as an invalidation only. Periodic sync repairs events received during an active import.
            var queued = sync.Queue(repo);
            return Results.Accepted(value: new { queued });
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or GitHubSyncException) { return Results.BadRequest(); }
    }
}
