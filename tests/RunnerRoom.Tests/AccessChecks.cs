using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RunnerRoom;

internal static class AccessChecks
{
    internal static void Run(Action<bool, string> check)
    {
        const string password = "test-only-password-42";
        var hash = AccessAccounts.Hasher.HashPassword("admin", password);
        var options = new AccessOptions { Enabled = true, PublicOrigin = "https://runner.example",
            LocalUsers = [new("admin", hash, "admin"), new("reader", hash, "viewer")],
            GitHubClientId = "test-client", GitHubClientSecretFile = "/unused/unit-test",
            GitHubUsers = [new("42", "viewer")] };
        var accounts = new AccessAccounts(options);
        var admin = accounts.Login("ADMIN", password);
        check(admin is not null && admin.IsInRole("admin") && accounts.ValidSession(admin), "Valid local credentials create a role-bound session with case-insensitive username matching.");
        check(accounts.Login("reader", password)?.IsInRole("viewer") == true, "Viewer credentials never create an admin session.");
        check(accounts.Login("admin", "wrong") is null && accounts.Login("unknown", password) is null &&
            accounts.Login("invalid username", password) is null, "Invalid local sign-ins are rejected.");
        check(!JsonSerializer.Serialize(admin!.Claims.Select(c => c.Value)).Contains(hash), "Session claims do not contain password hashes.");
        var changed = new AccessAccounts(new() { Enabled = true, PublicOrigin = "https://runner.example", LocalUsers = [
            new("admin", AccessAccounts.Hasher.HashPassword("admin", "changed-test-password"), "admin")] });
        check(!changed.ValidSession(admin), "Changing a password invalidates existing sessions after restart.");
        var changedRole = new AccessAccounts(new() { Enabled = true, PublicOrigin = "https://runner.example", LocalUsers = [
            new("admin", hash, "viewer"), new("other", hash, "admin")] });
        check(!changedRole.ValidSession(admin), "Role changes invalidate existing sessions.");
        check(!accounts.ValidSession(new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "local:removed")], "Session"))), "Removed and incomplete identities cannot reuse a session.");
        using var allowed = JsonDocument.Parse("""{"id":42,"login":"renamed-user"}""");
        var github = AccessSetup.GitHubIdentity(accounts, allowed.RootElement);
        check(github?.IsInRole("viewer") == true && github.FindFirstValue(ClaimTypes.NameIdentifier) == "github:42", "GitHub access binds to the immutable numeric ID.");
        using var impersonator = JsonDocument.Parse("""{"id":999,"login":"admin"}""");
        check(AccessSetup.GitHubIdentity(accounts, impersonator.RootElement) is null, "A matching login name cannot bypass the GitHub ID allowlist.");
        foreach (var bad in new[] { "[]", """{"id":"42","login":"admin"}""", """{"id":42}""", """{"id":-1,"login":"admin"}""" })
        {
            using var json = JsonDocument.Parse(bad);
            check(AccessSetup.GitHubIdentity(accounts, json.RootElement) is null, "Malformed GitHub identity responses are rejected.");
        }
        bool Invalid(AccessOptions input)
        {
            try { _ = new AccessAccounts(input); return false; }
            catch (InvalidOperationException) { return true; }
        }
        check(Invalid(new() { Enabled = true, PublicOrigin = "https://runner.example" }), "Enabled access without an administrator fails closed.");
        check(Invalid(new() { Enabled = true, PublicOrigin = "https://runner.example", LocalUsers = [new("admin", "not-a-hash", "admin")] }), "Placeholder or malformed password hashes fail startup.");
        check(Invalid(new() { Enabled = true, PublicOrigin = "http://runner.example", LocalUsers = [new("admin", hash, "admin")] }), "HTTPS is required by default.");
        check(Invalid(new() { Enabled = true, PublicOrigin = "https://runner.example/path", LocalUsers = [new("admin", hash, "admin")] }), "Canonical origin cannot contain a path.");
        check(Invalid(new() { Enabled = true, PublicOrigin = "https://runner.example", LocalUsers = [new("admin", hash, "admin"), new("ADMIN", hash, "viewer")] }), "Duplicate case-insensitive local identities fail closed.");
        check(Invalid(new() { Enabled = true, PublicOrigin = "https://runner.example", GitHubUsers = [new("42", "admin")] }), "GitHub-only access requires OAuth configuration.");
        var context = new DefaultHttpContext { User = accounts.Login("reader", password)! };
        check(!AccessSetup.IsAdmin(context, new() { Access = options }) && AccessSetup.IsAdmin(context, new()), "Server permissions distinguish authenticated viewer mode from explicitly open LAN mode.");
        var root = Path.Combine(Path.GetTempPath(), "runner-room-access-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new RunnerOptions { Monitoring = new() { StateDirectory = root } };
            var audit = new AccessAudit(config);
            audit.Add(context, "login_succeeded");
            check(new AccessAudit(config).Recent() is [{ Event: "login_succeeded", Actor: "local:reader" }], "Access activity persists without credentials.");
            check(!File.ReadAllText(Path.Combine(root, "access-audit.json")).Contains(password), "Audit persistence never includes passwords.");
            var demo = new AccessAudit(new() { Demo = true, Monitoring = new() { StateDirectory = root } });
            check(demo.Recent().Length == 0, "Demo access audit does not load live history.");
            File.WriteAllText(Path.Combine(root, "access-audit.json"), "broken");
            var broken = new AccessAudit(config);
            broken.Add(context, "logout");
            check(broken.Warning is not null && File.ReadAllText(Path.Combine(root, "access-audit.json")) == "broken", "Unreadable audit files are preserved.");
        }
        finally { Directory.Delete(root, true); }
    }
}
