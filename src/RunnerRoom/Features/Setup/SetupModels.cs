namespace RunnerRoom.Features.Setup;

internal sealed record SetupSaveRequest(string? Token, string[]? Scopes);
internal sealed record SetupDiscoverRequest(string? Token);
internal sealed record SetupRepository(string FullName, bool IsPrivate);
internal sealed record SetupDiscovery(SetupRepository[] Repositories, bool Partial, string Message);
internal sealed record SetupConfiguration(bool Demo, bool AccessEnabled, bool ManagementEnabled, bool PendingRestart, string Origin, string RootDirectory, string[] Scopes, string[] Issues);
internal sealed record SetupResult(bool Saved, bool RestartRequired, string Message);
internal sealed class SetupException(string message) : Exception(message);
