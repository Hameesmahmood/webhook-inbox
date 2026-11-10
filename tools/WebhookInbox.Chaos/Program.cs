using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

// The chaos run. It sends events to a running stack while killing workers with SIGKILL, waits for
// the queue to drain, then compares what the inbox acknowledged with what the consumer accepted.
var options = ChaosOptions.Parse(args);
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var admin = new AuthenticationHeaderValue("Bearer", options.AdminKey);
var runId = Guid.NewGuid().ToString("N")[..8];

await WaitUntilReady();
await http.PostAsync($"{options.Sink}/reset", null);
var workers = await Docker($"{options.Compose} ps -q worker");
var workerIds = workers.Split('\n', StringSplitOptions.RemoveEmptyEntries);
Console.WriteLine($"Run {runId}: {options.Events} events, {workerIds.Length} workers, one SIGKILL every {options.KillEvery.TotalSeconds}s");

// One clock for the whole run, so samples and kills share a time axis.
var clock = Stopwatch.StartNew();
using var stopKilling = new CancellationTokenSource();
var kills = 0;
var killTimes = new ConcurrentBag<double>();
var killer = Task.Run(async () =>
{
    while (!stopKilling.IsCancellationRequested)
    {
        await Task.Delay(options.KillEvery).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (stopKilling.IsCancellationRequested)
        {
            break;
        }

        var victim = workerIds[Random.Shared.Next(workerIds.Length)];
        await Docker($"kill --signal KILL {victim}");
        Interlocked.Increment(ref kills);
        killTimes.Add(Math.Round(clock.Elapsed.TotalSeconds, 1));
        await Task.Delay(TimeSpan.FromSeconds(1));
        await Docker($"start {victim}");
    }
});

// Send every event, resending on failure as a real provider would. The idempotency key makes resends safe.
// Record the queue once a second, for the chart in docs/RESULTS.md.
using var stopSampling = new CancellationTokenSource();
var samples = new ConcurrentQueue<Sample>();
var sampler = Task.Run(async () =>
{
    while (!stopSampling.IsCancellationRequested)
    {
        await TakeSample();
        await Task.Delay(1000).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }
});

var acked = new ConcurrentDictionary<string, bool>();
var rejected = 0;
using (var limit = new SemaphoreSlim(options.Concurrency))
{
    await Task.WhenAll(Enumerable.Range(0, options.Events).Select(async i =>
    {
        await limit.WaitAsync();
        try
        {
            var id = await SendUntilAcknowledged(i);
            if (id is null)
            {
                Interlocked.Increment(ref rejected);
            }
            else
            {
                acked[id] = true;
            }
        }
        finally
        {
            limit.Release();
        }
    }));
}

var ingestSeconds = clock.Elapsed.TotalSeconds;
Console.WriteLine($"Sent {options.Events} events in {ingestSeconds:F1}s. Waiting for the queue to drain...");

var drained = await WaitForDrain();
var drainSeconds = clock.Elapsed.TotalSeconds;
stopKilling.Cancel();
await killer;
stopSampling.Cancel();
await sampler;
await TakeSample();
await Docker($"{options.Compose} up --detach --no-recreate worker");

var sink = await http.GetFromJsonAsync<SinkReport>($"{options.Sink}/received") ?? throw new InvalidOperationException("The sink sent no report.");
var dead = await DeadEventIds();

var delivered = acked.Keys.Count(sink.Accepted.ContainsKey);
var lost = acked.Keys.Count(id => !sink.Accepted.ContainsKey(id) && !dead.Contains(id));
var duplicates = acked.Keys.Where(sink.Accepted.ContainsKey).Sum(id => sink.Accepted[id] - 1);
var unexpected = sink.Accepted.Keys.Count(id => !acked.ContainsKey(id));

var result = new
{
    runId,
    finishedAt = DateTimeOffset.UtcNow,
    events = options.Events,
    acknowledged = acked.Count,
    rejected,
    delivered,
    deadLettered = acked.Keys.Count(dead.Contains),
    lost,
    duplicates,
    unexpected,
    workerKills = kills,
    workers = workerIds.Length,
    consumerRequests = sink.Requests,
    drained,
    ingestSeconds = Math.Round(ingestSeconds, 1),
    totalSeconds = Math.Round(drainSeconds, 1),
};

var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(json);
if (options.Out is not null)
{
    var folder = Path.GetDirectoryName(Path.GetFullPath(options.Out));
    if (!string.IsNullOrEmpty(folder))
    {
        Directory.CreateDirectory(folder);
    }

    await File.WriteAllTextAsync(options.Out, json + "\n");

    var timeline = new { runId, samples = samples.OrderBy(s => s.T), kills = killTimes.Order() };
    var timelinePath = Path.Combine(folder ?? "", Path.GetFileNameWithoutExtension(options.Out) + "-timeline.json");
    await File.WriteAllTextAsync(timelinePath, JsonSerializer.Serialize(timeline, JsonSerializerOptions.Web) + "\n");
}

var passed = drained && rejected == 0 && lost == 0 && unexpected == 0;
Console.WriteLine(passed ? "PASS: nothing acknowledged was lost." : "FAIL: see the counts above.");
return passed ? 0 : 1;

async Task<string?> SendUntilAcknowledged(int i)
{
    var body = Encoding.UTF8.GetBytes($$"""{"n":{{i}}}""");
    for (var attempt = 0; attempt < 30; attempt++)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.Api}/v1/hooks/demo") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-Event-Id", $"{runId}-{i}");
        try
        {
            using var response = await http.SendAsync(request);
            if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK)
            {
                return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
            }

            if ((int)response.StatusCode < 500)
            {
                return null;
            }
        }
        catch (HttpRequestException)
        {
        }

        await Task.Delay(200);
    }

    return null;
}

async Task TakeSample()
{
    try
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{options.Api}/v1/stats");
        request.Headers.Authorization = admin;
        var stats = await (await http.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();
        samples.Enqueue(new Sample(
            Math.Round(clock.Elapsed.TotalSeconds, 1),
            stats.GetProperty("pending").GetInt64(),
            stats.GetProperty("leased").GetInt64(),
            stats.GetProperty("delivered").GetInt64(),
            stats.GetProperty("dead").GetInt64()));
    }
    catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
    {
        // A missed sample leaves a gap in the chart; it never fails the run.
    }
}

async Task<bool> WaitForDrain()
{
    var deadline = DateTime.UtcNow + options.DrainTimeout;
    var quietChecks = 0;
    while (DateTime.UtcNow < deadline)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{options.Api}/v1/stats");
        request.Headers.Authorization = admin;
        var stats = await (await http.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();
        var open = stats.GetProperty("pending").GetInt64() + stats.GetProperty("leased").GetInt64();
        quietChecks = open == 0 ? quietChecks + 1 : 0;
        if (quietChecks >= 2)
        {
            return true;
        }

        await Task.Delay(1000);
    }

    return false;
}

async Task<HashSet<string>> DeadEventIds()
{
    var ids = new HashSet<string>();
    string? cursor = null;
    do
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{options.Api}/v1/deliveries?status=dead&limit=200{(cursor is null ? "" : $"&cursor={cursor}")}");
        request.Headers.Authorization = admin;
        var page = await (await http.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();
        foreach (var delivery in page.GetProperty("data").EnumerateArray())
        {
            ids.Add(delivery.GetProperty("eventId").GetString()!);
        }

        cursor = page.GetProperty("nextCursor").GetString();
    }
    while (cursor is not null);

    return ids;
}

async Task WaitUntilReady()
{
    for (var i = 0; i < 60; i++)
    {
        try
        {
            if ((await http.GetAsync($"{options.Api}/readyz")).IsSuccessStatusCode && (await http.GetAsync($"{options.Sink}/healthz")).IsSuccessStatusCode)
            {
                return;
            }
        }
        catch (HttpRequestException)
        {
        }

        await Task.Delay(1000);
    }

    throw new InvalidOperationException("The stack never became ready. Is `docker compose up` running?");
}

static async Task<string> Docker(string arguments)
{
    using var process = Process.Start(new ProcessStartInfo("docker", arguments) { RedirectStandardOutput = true, RedirectStandardError = true })!;
    var output = await process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();
    return output.Trim();
}

sealed record Sample(double T, long Pending, long Leased, long Delivered, long Dead);

sealed record SinkReport(long Requests, Dictionary<string, int> Accepted);

sealed record ChaosOptions(
    string Api,
    string Sink,
    string AdminKey,
    int Events,
    int Concurrency,
    TimeSpan KillEvery,
    TimeSpan DrainTimeout,
    string Compose,
    string? Out)
{
    public static ChaosOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>();
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            values[args[i].TrimStart('-')] = args[i + 1];
        }

        string Get(string key, string fallback) => values.GetValueOrDefault(key, fallback);
        var files = Get("compose-files", "compose.yaml,compose.chaos.yaml").Split(',').Select(f => $"-f {f}");

        return new ChaosOptions(
            Get("api", "http://localhost:8080"),
            Get("sink", "http://localhost:8081"),
            Get("admin-key", Environment.GetEnvironmentVariable("INBOX_ADMIN_KEY") ?? "local-admin-key"),
            int.Parse(Get("events", "2000")),
            int.Parse(Get("concurrency", "16")),
            TimeSpan.FromSeconds(double.Parse(Get("kill-every", "2"))),
            TimeSpan.FromSeconds(double.Parse(Get("drain-timeout", "300"))),
            $"compose {string.Join(' ', files)}",
            values.GetValueOrDefault("out"));
    }
}
