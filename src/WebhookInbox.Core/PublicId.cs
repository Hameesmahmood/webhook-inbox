namespace WebhookInbox.Core;

public static class PublicId
{
    private const string EventPrefix = "evt_";
    private const string DeliveryPrefix = "dlv_";

    /// <summary>The public form of an event ID. It's also the value consumers deduplicate on.</summary>
    public static string ForEvent(long id) => EventPrefix + id;

    public static string ForDelivery(long id) => DeliveryPrefix + id;

    public static bool TryParseEvent(string value, out long id) => TryParse(value, EventPrefix, out id);

    public static bool TryParseDelivery(string value, out long id) => TryParse(value, DeliveryPrefix, out id);

    private static bool TryParse(string value, string prefix, out long id)
    {
        id = 0;
        return value.StartsWith(prefix, StringComparison.Ordinal)
            && long.TryParse(value.AsSpan(prefix.Length), System.Globalization.NumberStyles.None, null, out id)
            && id > 0;
    }
}
