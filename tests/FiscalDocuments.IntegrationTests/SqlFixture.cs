using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using FiscalDocuments.Application;
using FiscalDocuments.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;
using Testcontainers.MsSql;

namespace FiscalDocuments.IntegrationTests;

public abstract class SqlFixture
{
    protected const string JwtKey = "test-only-signing-key-not-for-production-0123456789";
    protected WebApplicationFactory<Program> Factory = null!;
    protected HttpClient Client = null!;
    private MsSqlContainer? sql;
    private string connection = "";

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        var external = Environment.GetEnvironmentVariable("FISCAL_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(external))
        {
            sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await sql.StartAsync();
            external = sql.GetConnectionString();
        }
        connection = new SqlConnectionStringBuilder(external)
        {
            InitialCatalog = "FiscalTests_" + Guid.NewGuid().ToString("N"), TrustServerCertificate = true
        }.ConnectionString;
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SqlServer"] = connection,
                ["Privacy:HmacKey"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
                ["Jwt:SigningKey"] = JwtKey,
                ["Jwt:Issuer"] = "fiscal-documents", ["Jwt:Audience"] = "fiscal-api",
                ["Database:ApplyMigrations"] = "false"
            }));
        });
        Client = Factory.CreateClient();
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<FiscalDbContext>().Database.MigrateAsync();
    }

    [SetUp]
    public async Task CleanAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<FiscalDbContext>().Database.ExecuteSqlRawAsync("""
            DELETE FROM InboxMessages; DELETE FROM OutboxMessages; DELETE FROM IdempotentOperations;
            DELETE FROM ProcessingFailures; DELETE FROM DocumentSummaries; DELETE FROM UploadReceipts;
            DELETE FROM DocumentProcessing; DELETE FROM Documents;
            """);
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        Client?.Dispose();
        if (Factory is not null)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<FiscalDbContext>().Database.EnsureDeletedAsync();
            await Factory.DisposeAsync();
        }
        if (sql is not null) await sql.DisposeAsync();
    }

    protected static byte[] Sample(string name = "nfe.xml") => File.ReadAllBytes(Path.Combine(TestContext.CurrentContext.TestDirectory, "Samples", name));
    protected static string Token(string scopes = "documents.read documents.write operations.replay") => new JwtSecurityTokenHandler().WriteToken(
        new JwtSecurityToken("fiscal-documents", "fiscal-api", [new Claim("sub", "integration-user"), new Claim("scope", scopes)],
            expires: DateTime.UtcNow.AddHours(1), signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey)), SecurityAlgorithms.HmacSha256)));

    protected async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }
}
