namespace WebhookInbox.Core;

public sealed class InboxOptions
{
    public const string Section = "Inbox";

    /// <summary>Webhook sources, by the name used in the ingest URL.</summary>
    public Dictionary<string, SourceOptions> Sources { get; set; } = [];

    public int MaxBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>Bearer key for the admin API. When it isn't set, the admin API refuses every request.</summary>
    public string? AdminKey { get; set; }
}

public sealed class SourceOptions
{
    /// <summary>The header holding the provider's event ID. Without one, events can't be deduplicated.</summary>
    public string? IdempotencyHeader { get; set; }

    public List<string> Destinations { get; set; } = [];
}
