using FiscalDocuments.Infrastructure.Messaging;
using FiscalDocuments.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FiscalDocuments.Infrastructure;

public static class LocalDependencyChecks
{
    /// <summary>
    /// Verifica as conexões com SQL Server e RabbitMQ, para identificar problemas antes de executar as migrations e iniciar o sistema.
    /// </summary>
    /// <param name="services"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static async Task CheckLocalDependenciesAsync(this IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        try
        {
            var configured = scope.ServiceProvider.GetRequiredService<FiscalDbContext>().Database.GetConnectionString();
            var connectionString = new SqlConnectionStringBuilder(configured)
            { 
                InitialCatalog = "master", 
                ConnectTimeout = 10 
            };
            await using var sql = new SqlConnection(connectionString.ConnectionString);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await sql.OpenAsync(deadline.Token);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("SQL Server connection failed. Install SQL Server LocalDB (MSSQLLocalDB) or pass -SqlConnectionString to Initialize-Local.ps1. Verify that the configured SQL account can connect to master and create the application database. Failure type: " + exception.GetType().Name);
        }
        Console.WriteLine("SQL Server connection: OK");
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await services.GetRequiredService<RabbitBroker>().ConnectionAsync(deadline.Token);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("RabbitMQ connection failed. Install RabbitMQ with a compatible Erlang version and start its Windows service. Defaults are localhost:5672, guest/guest, virtual host /. For another installation, pass -RabbitHost, -RabbitPort, -RabbitUsername, -RabbitPassword and -RabbitVirtualHost to Initialize-Local.ps1. Verify credentials and virtual-host permissions. Failure type: " + exception.GetType().Name);
        }
        Console.WriteLine("RabbitMQ connection: OK");
    }
}
