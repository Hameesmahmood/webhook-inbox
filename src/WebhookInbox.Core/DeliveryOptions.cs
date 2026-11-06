namespace WebhookInbox.Core;

public sealed class DeliveryOptions
{
    public const string Section = "Delivery";

    public int MaxAttempts { get; set; } = 8;

    public int BatchSize { get; set; } = 10;

    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan Lease { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A lease shorter than the HTTP timeout hands live jobs to a second worker,
    /// so a misconfigured worker should refuse to start.
    /// </summary>
    public void Validate()
    {
        if (Lease < HttpTimeout + TimeSpan.FromSeconds(1))
        {
            throw new InvalidOperationException($"Delivery:Lease ({Lease}) must be at least one second longer than Delivery:HttpTimeout ({HttpTimeout}).");
        }
    }
}
