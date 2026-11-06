namespace WebhookInbox.Core;

public static class PublicId
{
    /// <summary>The public form of an event ID. It's also the value consumers deduplicate on.</summary>
    public static string ForEvent(long id) => $"evt_{id}";
}
