using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace WebhookInbox.Tests;

public static class TestApi
{
    /// <summary>"shop" has an idempotency header and two destinations. "plain" has one destination and no key.</summary>
    public static WebApplicationFactory<Program> Create(string connectionString) => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(host => host
            .UseSetting("ConnectionStrings:Inbox", connectionString)
            .UseSetting("Inbox:MaxBodyBytes", "1000")
            .UseSetting("Inbox:Sources:shop:IdempotencyHeader", "X-Event-Id")
            .UseSetting("Inbox:Sources:shop:Destinations:0", "http://one.test/hook")
            .UseSetting("Inbox:Sources:shop:Destinations:1", "http://two.test/hook")
            .UseSetting("Inbox:Sources:plain:Destinations:0", "http://one.test/hook"));
}
