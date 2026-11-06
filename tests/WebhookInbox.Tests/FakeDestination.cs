using System.Collections.Concurrent;
using System.Net;

namespace WebhookInbox.Tests;

/// <summary>Stands in for the consumer's app and records every request it gets.</summary>
public sealed class FakeDestination(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public ConcurrentQueue<(HttpRequestMessage Request, byte[] Body)> Received { get; } = new();

    public static FakeDestination Returning(HttpStatusCode status) => new((_, _) => Task.FromResult(new HttpResponseMessage(status)));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Received.Enqueue((request, await request.Content!.ReadAsByteArrayAsync(ct)));
        return await respond(request, ct);
    }
}
