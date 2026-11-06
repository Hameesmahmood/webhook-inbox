using Microsoft.Extensions.Options;
using WebhookInbox.Core;
using WebhookInbox.Worker;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Inbox")
    ?? throw new InvalidOperationException("ConnectionStrings:Inbox is required.");

builder.Services.AddInboxStorage(connectionString);
builder.Services.Configure<DeliveryOptions>(builder.Configuration.GetSection(DeliveryOptions.Section));
builder.Services
    .AddHttpClient<DeliveryProcessor>(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        // A redirect isn't a delivery. It's treated as a failure instead of being followed.
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    });
builder.Services.AddHostedService<DeliveryWorker>();

var delivery = builder.Configuration.GetSection(DeliveryOptions.Section).Get<DeliveryOptions>() ?? new DeliveryOptions();
delivery.Validate();

// Shutdown has to outlast the longest send, or jobs in progress get abandoned to their leases.
builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = delivery.HttpTimeout + TimeSpan.FromSeconds(5));

builder.Build().Run();
