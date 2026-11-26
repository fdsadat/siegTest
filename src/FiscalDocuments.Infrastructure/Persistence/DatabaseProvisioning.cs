using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FiscalDocuments.Infrastructure.Persistence;

public static class DatabaseProvisioning
{
    public static async Task ProvisionApplicationUserAsync(IServiceProvider services, IConfiguration configuration)
    {
        var password = configuration["Database:ApplicationPassword"];
        if (string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        if (password.Length < 16 || password.Length > 128)
        {
            throw new InvalidOperationException("Database application password requires 16 to 128 characters.");
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FiscalDbContext>();
        // Identificador fixo e escape do segredo parametrizado no SQL; a senha não aparece no texto do comando.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            IF SUSER_ID('fiscal_app') IS NULL
            BEGIN
                DECLARE @secret nvarchar(128) = {password};
                DECLARE @statement nvarchar(max) = N'CREATE LOGIN [fiscal_app] WITH PASSWORD = ' + QUOTENAME(@secret, '''') + N', CHECK_POLICY = ON;';
                EXEC sys.sp_executesql @statement;
            END;
            IF DATABASE_PRINCIPAL_ID('fiscal_app') IS NULL CREATE USER [fiscal_app] FOR LOGIN [fiscal_app];
            ALTER ROLE db_datareader ADD MEMBER [fiscal_app];
            ALTER ROLE db_datawriter ADD MEMBER [fiscal_app];
            """);
    }
}
