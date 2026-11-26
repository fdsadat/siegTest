using FiscalDocuments.Application;
using FiscalDocuments.Infrastructure.Messaging;
using FiscalDocuments.Infrastructure.Persistence;
using FiscalDocuments.Infrastructure.Xml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FiscalDocuments.Infrastructure;

public static class InfrastructureServices
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<FiscalDbContext>(options => options.UseSqlServer(
            configuration.GetConnectionString("SqlServer") ?? throw new InvalidOperationException("ConnectionStrings:SqlServer is required."),
            sql => sql.EnableRetryOnFailure(3)));
        services.Configure<PrivacyOptions>(configuration.GetSection("Privacy"));
        services.Configure<RabbitOptions>(configuration.GetSection("Rabbit"));
        services.AddSingleton<IFingerprints, Fingerprints>();
        services.AddSingleton<IXmlDocuments, FiscalXmlDocuments>();
        services.AddScoped<IWriteStore, SqlWriteStore>();
        services.AddScoped<IDocumentQueries, SqlDocumentQueries>();
        services.AddSingleton<RabbitBroker>();
        return services;
    }
    public static IServiceCollection AddProcessingWorkers(this IServiceCollection services)
    {
        services.AddHostedService<OutboxDispatcher>();
        services.AddHostedService<RabbitConsumer>();
        return services;
    }
    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<FiscalDbContext>().Database.MigrateAsync(ct);
    }
}
