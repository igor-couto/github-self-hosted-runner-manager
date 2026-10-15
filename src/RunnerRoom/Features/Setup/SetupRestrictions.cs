using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace RunnerRoom.Features.Setup;

internal static class SetupRestrictions
{
    internal static string? Find(IConfigurationRoot configuration, bool accessEnabled)
    {
        var ownedKeys = configuration.AsEnumerable().Where(entry => entry.Value is not null &&
            (entry.Key.StartsWith("Management:", StringComparison.OrdinalIgnoreCase) ||
             !accessEnabled && entry.Key.StartsWith("Access:", StringComparison.OrdinalIgnoreCase) &&
             !entry.Key.StartsWith("Access:TrustedProxies:", StringComparison.OrdinalIgnoreCase))).Select(entry => entry.Key);
        foreach (var key in ownedKeys)
        {
            // Ask the last applicable provider, just as the configuration binder does.
            foreach (var provider in configuration.Providers.Reverse())
            {
                if (!provider.TryGet(key, out var value)) continue;
                if (provider is EnvironmentVariablesConfigurationProvider or CommandLineConfigurationProvider)
                    return $"This deployment controls {key.Split(':')[0]} through environment variables or startup arguments. Remove those overrides before using guided setup, or continue configuring that section through your deployment.";
                break;
            }
        }
        return null;
    }
}
