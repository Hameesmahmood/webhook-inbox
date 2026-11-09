using Npgsql;
using WebhookInbox.Api;
using WebhookInbox.Core;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Inbox")
    ?? throw new InvalidOperationException("ConnectionStrings:Inbox is required.");
builder.Services.AddInboxStorage(connectionString);
builder.Services.Configure<InboxOptions>(builder.Configuration.GetSection(InboxOptions.Section));
builder.Services.AddProblemDetails();

var app = builder.Build();

await Migrator.MigrateAsync(app.Services.GetRequiredService<NpgsqlDataSource>());

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet("/readyz", async (NpgsqlDataSource dataSource, CancellationToken ct) =>
{
    try
    {
        await using var command = dataSource.CreateCommand("SELECT 1");
        await command.ExecuteScalarAsync(ct);
        return Results.Ok(new { status = "ready" });
    }
    catch (NpgsqlException)
    {
        return Results.Problem("The database is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapIngest();
app.MapAdmin();

app.Run();

public partial class Program;
