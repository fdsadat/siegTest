using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace FiscalDocuments.Api;

public static class DevelopmentTokenEndpoint
{
    /// <summary>
    /// Endpoint criado apenas para facilitar os testes. Injetado diretamente apenas se estiver em modo DEV.
    /// </summary>
    /// <param name="app"></param>
    /// <exception cref="InvalidOperationException"></exception>
    public static void MapDevelopmentTokenEndpoint(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment() || !string.IsNullOrWhiteSpace(app.Configuration["Jwt:Authority"]))
        {
            return;
        }

        app.MapPost("/api/v1/auth/token", (IConfiguration configuration, TimeProvider clock) =>
        {
            const int expiresIn = 7200;
            var now = clock.GetUtcNow();
            var signingKey = configuration["Jwt:SigningKey"];
            if (string.IsNullOrWhiteSpace(signingKey) || Encoding.UTF8.GetByteCount(signingKey) < 32)
            {
                throw new InvalidOperationException("Jwt:SigningKey must contain at least 32 bytes.");
            }

            var token = new JwtSecurityToken(
                issuer: configuration["Jwt:Issuer"] ?? "fiscal-documents",
                audience: configuration["Jwt:Audience"] ?? "fiscal-api",
                claims:
                [
                    new Claim(JwtRegisteredClaimNames.Sub, "demo-developer"),
                    new Claim("scope", "documents.read documents.write operations.replay")
                ],
                notBefore: now.UtcDateTime,
                expires: now.AddSeconds(expiresIn).UtcDateTime,
                signingCredentials: new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256));

            return Results.Ok(new DevelopmentTokenResponse(new JwtSecurityTokenHandler().WriteToken(token), "Bearer", expiresIn));
        })
        .AllowAnonymous()
        .WithTags("Autenticação (testes)")
        .WithSummary("Gerar token Bearer para testes locais")
        .WithDescription("Este endpoint não deveria existir em uma aplicação de produção. Está disponível apenas em Development, sem autenticação, para facilitar os testes deste projeto. Gera um token válido por duas horas com as permissões documents.read, documents.write e operations.replay. Não está disponível quando Jwt:Authority está configurado. Copie accessToken e cole no botão Authorize do Swagger.")
        .Produces<DevelopmentTokenResponse>();
    }
}
