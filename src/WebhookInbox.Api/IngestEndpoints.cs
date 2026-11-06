using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using WebhookInbox.Core;

namespace WebhookInbox.Api;

public static class IngestEndpoints
{
    private const int MaxIdempotencyKeyLength = 255;

    // Credentials must never sit in the database.
    private static readonly HashSet<string> HeadersNotStored = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Cookie",
        "Proxy-Authorization",
    };

    public static void MapIngest(this IEndpointRouteBuilder app) => app.MapPost("/v1/hooks/{source}", Receive);

    private static async Task<IResult> Receive(
        string source,
        HttpContext context,
        InboxStore store,
        IOptions<InboxOptions> options,
        CancellationToken ct)
    {
        if (!options.Value.Sources.TryGetValue(source, out var sourceOptions))
        {
            return Results.Problem($"No source is configured with the name '{source}'.", statusCode: StatusCodes.Status404NotFound);
        }

        var body = await ReadBodyAsync(context.Request, options.Value.MaxBodyBytes, ct);
        if (body is null)
        {
            return Results.Problem($"The body is larger than the {options.Value.MaxBodyBytes} byte limit.", statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var key = ReadIdempotencyKey(context.Request, sourceOptions);
        if (key?.Length > MaxIdempotencyKeyLength)
        {
            return Results.Problem($"The idempotency key is longer than {MaxIdempotencyKeyLength} characters.", statusCode: StatusCodes.Status400BadRequest);
        }

        var newEvent = new NewEvent(source, key, body, SHA256.HashData(body), SerializeHeaders(context.Request.Headers));

        IngestResult result;
        try
        {
            result = await store.IngestAsync(newEvent, sourceOptions.Destinations, ct);
        }
        catch (NpgsqlException)
        {
            // Never acknowledge what wasn't saved. A 503 makes the provider send it again.
            context.Response.Headers.RetryAfter = "5";
            return Results.Problem("The event could not be stored. Send it again.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var id = PublicId.ForEvent(result.EventId);
        return result.Outcome switch
        {
            IngestOutcome.Stored => Results.Accepted(value: new { id }),
            IngestOutcome.Duplicate => Results.Ok(new { id, duplicate = true }),
            _ => Results.Problem($"This idempotency key was already used by {id} with a different body.", statusCode: StatusCodes.Status409Conflict),
        };
    }

    /// <summary>Returns null when the body goes over the limit, without reading the rest of it.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int maxBytes, CancellationToken ct)
    {
        if (request.ContentLength > maxBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string? ReadIdempotencyKey(HttpRequest request, SourceOptions source)
    {
        if (source.IdempotencyHeader is null)
        {
            return null;
        }

        var value = request.Headers[source.IdempotencyHeader].ToString().Trim();
        return value.Length == 0 ? null : value;
    }

    private static string SerializeHeaders(IHeaderDictionary headers) => JsonSerializer.Serialize(
        headers
            .Where(header => !HeadersNotStored.Contains(header.Key))
            .ToDictionary(header => header.Key, header => header.Value.ToString()));
}
