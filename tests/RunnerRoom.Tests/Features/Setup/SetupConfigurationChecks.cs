using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RunnerRoom.Configuration;
using RunnerRoom.Features.Access;
using RunnerRoom.Features.Management;
using RunnerRoom.Features.Setup;

namespace RunnerRoom.Tests.Features.Setup;

internal static class SetupConfigurationChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "runner-room-setup-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var initial = new RunnerOptions
            {
                Monitoring = new() { StateDirectory = directory },
                Access = new() { LocalUsers = [new("old-admin", "old-hash", "admin"), new("other-admin", "old-hash", "admin")], GitHubUsers = [new("1234", "admin")] },
                Management = new() { AllowedScopes = ["repo:old/repo", "repo:old/other"] }
            };
            var overlay = new
            {
                Access = new AccessOptions { Enabled = true, LocalUsers = [new("new-admin", "new-hash", "admin")] },
                Management = new ManagementOptions { Enabled = true, AllowedScopes = ["repo:new/repo"] }
            };
            File.WriteAllText(SetupService.ConfigurationPath(initial), JsonSerializer.Serialize(overlay));
            using var source = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(initial));
            var config = new ConfigurationBuilder().AddJsonStream(source).Build();
            var loaded = SetupConfigurationLoader.Load(config, []);
            check(loaded.Access.LocalUsers.Length == 1 && loaded.Access.LocalUsers[0].Username == "new-admin" && loaded.Access.GitHubUsers.Length == 0,
                "Setup replaces account arrays without retaining previously disabled accounts.");
            check(loaded.Management.AllowedScopes.SequenceEqual(new[] { "repo:new/repo" }), "Setup replaces scopes without retaining old scopes.");
            check(loaded.Monitoring.StateDirectory == directory, "Setup preserves unrelated options.");
            var overridden = SetupConfigurationLoader.Load(config, ["--Management:Enabled", "false"]);
            check(!overridden.Management.Enabled, "Explicit startup overrides remain authoritative after setup.");
            var restricted = new ConfigurationBuilder().AddCommandLine(["--Management:TokenFile", "/private/token"]).Build();
            check(SetupRestrictions.Find(restricted) is { } reason && !reason.Contains("/private/token"), "Externally owned management settings block setup without leaking values.");
            var accessRestricted = new ConfigurationBuilder().AddCommandLine(["--Access:LocalUsers:0:Username", "existing"]).Build();
            check(SetupRestrictions.Find(accessRestricted) is null,
                "Access overrides remain compatible because management setup preserves authentication settings.");
            var proxyConfig = new ConfigurationBuilder().AddCommandLine(["--Access:TrustedProxies:0", "127.0.0.1"]).Build();
            check(SetupRestrictions.Find(proxyConfig) is null, "Trusted proxy configuration remains compatible with initial setup.");
            File.WriteAllText(SetupService.ConfigurationPath(initial), "invalid-json");
            try { SetupConfigurationLoader.Load(config, []); check(false, "Invalid setup must fail closed."); }
            catch (InvalidOperationException) { check(true, "Invalid setup fails closed."); }
        }
        finally { Directory.Delete(directory, true); }
    }
}
