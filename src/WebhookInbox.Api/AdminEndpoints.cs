using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using WebhookInbox.Core;

namespace WebhookInbox.Api;

public static class AdminEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;
    private static readonly string[] Statuses = ["pending", "leased", "delivered", "dead"];

    public static void MapAdmin(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/v1").AddEndpointFilter(RequireAdminKey);

        admin.MapGet("/events", ListEvents);
        admin.MapGet("/events/{id}", GetEvent);
        admin.MapGet("/deliveries", ListDeliveries);
        admin.MapPost("/deliveries/{id}/replay", Replay);
        admin.MapPost("/deliveries/replay", ReplayDead);
        admin.MapGet("/stats", async (InboxStore store, CancellationToken ct) => Results.Ok(await store.GetStatsAsync(ct)));
    }

    private static async ValueTask<object?> RequireAdminKey(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var expected = http.RequestServices.GetRequiredService<IOptions<InboxOptions>>().Value.AdminKey;
        if (string.IsNullOrEmpty(expected))
        {
            return Results.Problem("The admin API is turned off. Set Inbox:AdminKey to turn it on.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var header = http.Request.Headers.Authorization.ToString();
        var given = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : "";

        // A fixed-time comparison doesn't reveal how much of the key was right.
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected)))
        {
            return Results.Problem("Send the admin key as 'Authorization: Bearer <key>'.", statusCode: StatusCodes.Status401Unauthorized);
        }

        return await next(context);
    }

    private static async Task<IResult> ListEvents(string? source, string? cursor, int? limit, InboxStore store, CancellationToken ct)
    {
        if (!TryReadPage(cursor, limit, PublicId.TryParseEvent, out var before, out var take, out var problem))
        {
            return problem!;
        }

        var rows = await store.ListEventsAsync(source, before, take + 1, ct);
        var page = rows.Take(take).ToList();
        return Results.Ok(new
        {
            data = page.Select(e => new
            {
                id = PublicId.ForEvent(e.Id),
                e.Source,
                e.IdempotencyKey,
                e.ReceivedAt,
                e.SizeBytes,
            }),
            nextCursor = rows.Count > take ? PublicId.ForEvent(page[^1].Id) : null,
        });
    }

    private static async Task<IResult> GetEvent(string id, InboxStore store, CancellationToken ct)
    {
        if (!PublicId.TryParseEvent(id, out var eventId) || await store.GetEventAsync(eventId, ct) is not { } found)
        {
            return Results.Problem($"No event has the ID '{id}'.", statusCode: StatusCodes.Status404NotFound);
        }

        var (e, deliveries) = found;
        return Results.Ok(new
        {
            id = PublicId.ForEvent(e.Id),
            e.Source,
            e.IdempotencyKey,
            e.ReceivedAt,
            body = Encoding.UTF8.GetString(e.Payload),
            headers = System.Text.Json.JsonDocument.Parse(e.Headers).RootElement,
            deliveries = deliveries.Select(ToResponse),
        });
    }

    private static async Task<IResult> ListDeliveries(string? status, string? source, string? cursor, int? limit, InboxStore store, CancellationToken ct)
    {
        if (status is not null && !Statuses.Contains(status))
        {
            return Results.Problem($"status must be one of: {string.Join(", ", Statuses)}.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!TryReadPage(cursor, limit, PublicId.TryParseDelivery, out var before, out var take, out var problem))
        {
            return problem!;
        }

        var rows = await store.ListDeliveriesAsync(status, source, before, take + 1, ct);
        var page = rows.Take(take).ToList();
        return Results.Ok(new
        {
            data = page.Select(ToResponse),
            nextCursor = rows.Count > take ? PublicId.ForDelivery(page[^1].Id) : null,
        });
    }

    private static async Task<IResult> Replay(string id, InboxStore store, CancellationToken ct)
    {
        var outcome = PublicId.TryParseDelivery(id, out var deliveryId)
            ? await store.ReplayAsync(deliveryId, ct)
            : ReplayOutcome.NotFound;

        return outcome switch
        {
            ReplayOutcome.Replayed => Results.Accepted(value: new { id, status = "pending" }),
            ReplayOutcome.InProgress => Results.Problem($"{id} is still pending or being delivered. Only finished deliveries can be replayed.", statusCode: StatusCodes.Status409Conflict),
            _ => Results.Problem($"No delivery has the ID '{id}'.", statusCode: StatusCodes.Status404NotFound),
        };
    }

    private static async Task<IResult> ReplayDead(string? source, InboxStore store, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return Results.Problem("Name the source to replay, for example ?source=stripe.", statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.Accepted(value: new { source, replayed = await store.ReplayDeadAsync(source, ct) });
    }

    private static object ToResponse(DeliveryRecord d) => new
    {
        id = PublicId.ForDelivery(d.Id),
        eventId = PublicId.ForEvent(d.EventId),
        d.Source,
        d.Destination,
        d.Status,
        d.Attempts,
        d.NextAttemptAt,
        d.LastStatusCode,
        d.LastError,
        d.DeliveredAt,
    };

    private delegate bool CursorParser(string value, out long id);

    private static bool TryReadPage(string? cursor, int? limit, CursorParser parse, out long? before, out int take, out IResult? problem)
    {
        before = null;
        take = limit ?? DefaultLimit;
        problem = null;

        if (take is < 1 or > MaxLimit)
        {
            problem = Results.Problem($"limit must be between 1 and {MaxLimit}.", statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        if (cursor is not null)
        {
            if (!parse(cursor, out var id))
            {
                problem = Results.Problem("cursor isn't valid. Use the nextCursor value from the previous page.", statusCode: StatusCodes.Status400BadRequest);
                return false;
            }

            before = id;
        }

        return true;
    }
}
