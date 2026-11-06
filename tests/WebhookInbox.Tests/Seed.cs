using System.Security.Cryptography;
using System.Text;
using WebhookInbox.Core;

namespace WebhookInbox.Tests;

public static class Seed
{
    public static async Task<long> EventAsync(InboxStore store, string body = "{}", params string[] destinations)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var targets = destinations.Length > 0 ? destinations : ["http://one.test/hook"];
        var result = await store.IngestAsync(
            new NewEvent("shop", null, bytes, SHA256.HashData(bytes), """{"Content-Type":"application/json"}"""),
            targets);
        return result.EventId;
    }
}
