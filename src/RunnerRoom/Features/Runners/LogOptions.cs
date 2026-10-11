namespace RunnerRoom.Features.Runners;

public sealed class LogOptions
{
    public bool Enabled { get; set; } = true;
    public string[] RedactValues { get; set; } = [];
}
