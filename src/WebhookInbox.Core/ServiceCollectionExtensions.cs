using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace WebhookInbox.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInboxStorage(this IServiceCollection services, string connectionString) =>
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
}
