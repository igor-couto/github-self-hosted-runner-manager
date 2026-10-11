namespace RunnerRoom.Features.Runners;

public sealed class DiscoveryOptions
{
    public bool Recursive { get; set; }
    public int MaxDepth { get; set; } = 4;
}

public sealed class RunnerOverride
{
    public string Path { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Group { get; set; }
}
