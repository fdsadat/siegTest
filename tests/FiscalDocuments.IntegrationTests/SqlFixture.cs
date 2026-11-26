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

namespace FiscalDocuments.IntegrationTests;

public abstract class SqlFixture
{
    protected const string JwtKey = "test-only-signing-key-not-for-production-0123456789";
    protected WebApplicationFactory<Program> Factory = null!;
    protected HttpClient Client = null!;
    private string connection = "";

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        var external = Environment.GetEnvironmentVariable("FISCAL_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(external))
        {
            external = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true";
        }

        try
        {
            await using var check = new SqlConnection(new SqlConnectionStringBuilder(external)
            { InitialCatalog = "master", ConnectTimeout = 10 }.ConnectionString);
            await check.OpenAsync();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("SQL test prerequisite unavailable. Install/start SQL Server LocalDB, or set FISCAL_TEST_SQL_CONNECTION to a SQL Server account allowed to create and drop test databases. No Docker fallback is used. Failure type: " + exception.GetType().Name);
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
                ["Jwt:Authority"] = null,
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
