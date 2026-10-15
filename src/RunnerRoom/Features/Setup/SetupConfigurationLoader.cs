using System.Text.Json;
using RunnerRoom.Configuration;
using RunnerRoom.Features.Access;
using RunnerRoom.Features.Management;
using RunnerRoom.Features.Runners;

namespace RunnerRoom.Features.Setup;

internal static class SetupConfigurationLoader
{
    internal static RunnerOptions Load(IConfiguration configuration, string[] args)
    {
        var options = configuration.Get<RunnerOptions>() ?? new();
        var path = SetupService.ConfigurationPath(options);
        if (options.Demo || !File.Exists(path)) return options;
        // Configuration providers merge arrays by index. Replace complete owned sections first,
        // so removed accounts and allowed scopes cannot leak back in from an older settings file.
        var text = LocalRunnerReader.ReadSmall(path, 131072) ?? throw new InvalidOperationException("The saved setup configuration could not be read. Restore its permissions or a known-good backup.");
        try
        {
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("Management", out var management)) throw new JsonException();
            options.Management = management.Deserialize<ManagementOptions>() ?? throw new JsonException();
            if (json.RootElement.TryGetProperty("Access", out var access)) options.Access = access.Deserialize<AccessOptions>() ?? throw new JsonException();
        }
        catch (JsonException) { throw new InvalidOperationException("The saved setup configuration is invalid. Restore a known-good backup before restarting."); }
        // Explicit deployment changes after setup still take precedence.
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(options));
        var effective = new ConfigurationBuilder().AddJsonStream(stream).AddEnvironmentVariables().AddCommandLine(args).Build();
        return effective.Get<RunnerOptions>() ?? throw new InvalidOperationException("Setup configuration could not be applied.");
    }
}
