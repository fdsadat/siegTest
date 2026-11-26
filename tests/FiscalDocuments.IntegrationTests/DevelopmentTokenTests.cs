using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FiscalDocuments.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace FiscalDocuments.IntegrationTests;

[TestFixture, Category("Http")]
public sealed class DevelopmentTokenTests
{
    private const string SigningKey = "test-only-development-token-key-0123456789abcdef";
    private static WebApplicationFactory<Program> CreateFactory(string environment = "Development", string? authority = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SqlServer"] = "Server=127.0.0.1,1;Database=Unused;User Id=test;Password=test-only;TrustServerCertificate=true",
                ["Privacy:HmacKey"] = Convert.ToBase64String(new byte[32]),
                ["Jwt:SigningKey"] = SigningKey,
                ["Jwt:Issuer"] = "test-issuer",
                ["Jwt:Audience"] = "test-audience",
                ["Jwt:Authority"] = authority,
                ["Database:ApplyMigrations"] = "false"
            }));
        });
    }

    [Test]
    public async Task Anonymous_request_issues_a_signed_token_accepted_by_the_api()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/v1/auth/token", null);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = (await response.Content.ReadFromJsonAsync<DevelopmentTokenResponse>())!;
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(body.AccessToken, new TokenValidationParameters
        {
            ValidIssuer = "test-issuer", ValidAudience = "test-audience",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
            ValidateIssuer = true, ValidateAudience = true, ValidateIssuerSigningKey = true,
            ValidateLifetime = true, ClockSkew = TimeSpan.Zero
        }, out var validated);
        Assert.Multiple(() =>
        {
            Assert.That(body.TokenType, Is.EqualTo("Bearer"));
            Assert.That(body.ExpiresIn, Is.EqualTo(7200));
            Assert.That(validated.ValidTo - validated.ValidFrom, Is.EqualTo(TimeSpan.FromHours(2)));
            Assert.That(principal.FindFirst("sub")?.Value, Is.EqualTo("demo-developer"));
            Assert.That(principal.FindFirst("scope")?.Value.Split(' '),
                Is.EquivalentTo(new[] { "documents.read", "documents.write", "operations.replay" }));
            Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
        });

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(body.TokenType, body.AccessToken);
        // A validação ocorre antes do SQL: o status 428 comprova que o token passou pela autenticação e pela autorização documents.write.
        using var mutation = await client.DeleteAsync($"/api/v1/documents/{Guid.NewGuid()}");
        Assert.That((int)mutation.StatusCode, Is.EqualTo(428));
    }

    [TestCase("Production")]
    [TestCase("Staging")]
    public async Task Token_endpoint_is_not_registered_outside_development(string environment)
    {
        await using var factory = CreateFactory(environment);
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/v1/auth/token", null);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Token_endpoint_is_not_registered_when_an_external_authority_is_configured()
    {
        await using var factory = CreateFactory(authority: "https://identity.example.test");
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/v1/auth/token", null);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Swagger_describes_the_test_only_endpoint_without_requiring_a_bearer_token()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        using var swagger = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var operation = swagger.RootElement.GetProperty("paths").GetProperty("/api/v1/auth/token").GetProperty("post");
        Assert.Multiple(() =>
        {
            Assert.That(operation.GetProperty("description").GetString(), Does.Contain("não deveria existir").And.Contain("produção"));
            Assert.That(operation.GetProperty("security").EnumerateArray().Any(requirement => !requirement.EnumerateObject().Any()), Is.True);
            Assert.That(operation.TryGetProperty("requestBody", out _), Is.False);
            Assert.That(operation.GetProperty("responses").TryGetProperty("200", out _), Is.True);
            Assert.That(swagger.RootElement.GetProperty("security")[0].TryGetProperty("Bearer", out _), Is.True);
        });
    }
}
