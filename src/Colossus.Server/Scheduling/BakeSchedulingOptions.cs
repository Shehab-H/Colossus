namespace Colossus.Server.Scheduling;

public sealed class BakeSchedulingOptions
{
    public const string Section = "Scheduling";
    public bool Enabled { get; set; } = true;
    public int WorkerCount { get; set; } = 2;
}
