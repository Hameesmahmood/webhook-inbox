using System.Collections.Concurrent;

// A stand-in for the consumer's app, used by the chaos run. It fails a share of requests on
// purpose and records every Webhook-Id it accepted, so the run can count losses and duplicates.
var builder = WebApplication.CreateBuilder(args);
var failRate = builder.Configuration.GetValue("Sink:FailRate", 0.0);
var maxLatencyMs = builder.Configuration.GetValue("Sink:MaxLatencyMs", 0);

var app = builder.Build();
var accepted = new ConcurrentDictionary<string, int>();
long requests = 0;

app.MapPost("/hooks", async (HttpRequest request) =>
{
    Interlocked.Increment(ref requests);
    if (maxLatencyMs > 0)
    {
        await Task.Delay(Random.Shared.Next(maxLatencyMs));
    }

    if (Random.Shared.NextDouble() < failRate)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var id = request.Headers["Webhook-Id"].ToString();
    if (id.Length == 0)
    {
        return Results.BadRequest();
    }

    // Recorded before replying, like a real consumer committing its work. If the worker dies
    // before it hears this reply, the inbox sends again and this count goes to 2.
    accepted.AddOrUpdate(id, 1, (_, count) => count + 1);
    return Results.Ok();
});

app.MapGet("/received", () => Results.Ok(new { requests = Interlocked.Read(ref requests), accepted }));

app.MapPost("/reset", () =>
{
    accepted.Clear();
    Interlocked.Exchange(ref requests, 0);
    return Results.NoContent();
});

app.MapGet("/healthz", () => Results.Ok());

app.Run();
