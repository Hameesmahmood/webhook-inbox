using WebhookInbox.Core;
using WebhookInbox.Worker;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Inbox")
    ?? throw new InvalidOperationException("ConnectionStrings:Inbox is required.");
builder.Services.AddInboxStorage(connectionString);
builder.Services.AddHostedService<DeliveryWorker>();

builder.Build().Run();
