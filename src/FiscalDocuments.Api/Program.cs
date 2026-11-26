using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FiscalDocuments.Api;
using FiscalDocuments.Application;
using FiscalDocuments.Infrastructure;
using FiscalDocuments.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args.Where(argument => argument is not "--migrate" and not "--check-dependencies").ToArray());
builder.Services.AddApplication().AddInfrastructure(builder.Configuration);
builder.Services.AddControllers().AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
// Swagger**
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo { Title = "Fiscal Documents API", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT"
    });
    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        [new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Reference = new Microsoft.OpenApi.Models.OpenApiReference { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" }
        }] = Array.Empty<string>()
    });
    options.OperationFilter<DocumentOperationFilter>();
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.Configure<ApiBehaviorOptions>(options => options.InvalidModelStateResponseFactory = _ =>
    new BadRequestObjectResult(new ProblemDetails { Status = 400, Title = "invalid_request_parameters" }));

// Authentication** Valida o token JWT.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    var issuer = builder.Configuration["Jwt:Issuer"] ?? "fiscal-documents";
    var audience = builder.Configuration["Jwt:Audience"] ?? "fiscal-api";
    var authority = builder.Configuration["Jwt:Authority"];
    options.MapInboundClaims = false;
    options.IncludeErrorDetails = false;
    if (!string.IsNullOrWhiteSpace(authority))
    {
        options.Authority = authority;
        options.Audience = audience;
        options.RequireHttpsMetadata = true;
    }
    else
    {
        var key = builder.Configuration["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
        {
            throw new InvalidOperationException("Jwt:SigningKey must contain at least 32 bytes, or configure Jwt:Authority.");
        }

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = issuer, ValidateAudience = true, ValidAudience = audience,
            ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30), NameClaimType = "sub"
        };
    }
});
// Authorization** Define as claims.
builder.Services.AddAuthorization(options =>
{
    foreach (var permission in new[] { "documents.read", "documents.write", "operations.replay" })
    {
        options.AddPolicy(permission, policy => policy.RequireAuthenticatedUser().RequireClaim("sub")
            .RequireAssertion(context => context.User.FindAll("scope").Any(claim => claim.Value.Split(' ').Contains(permission, StringComparer.Ordinal))));
    }
});
// Rate Limiting** Limita a quantidade de requisições.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.User.FindFirstValue("sub") ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddConcurrencyLimiter("uploads", options => { options.PermitLimit = 4; options.QueueLimit = 0; });
});
// Kestrel Configuration** Configura o limite máximo do corpo da requisição.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = RequestValidation.MaxRequestBytes);

var app = builder.Build();
// Se o argumento --check-dependencies for passado, verifica se o SQL Server e o RabbitMQ estão disponíveis.
if (args.Contains("--check-dependencies", StringComparer.Ordinal))
{
    try 
    { 
        await app.Services.CheckLocalDependenciesAsync(); 
    }
    catch (InvalidOperationException exception)
    {
        Console.Error.WriteLine(exception.Message);
        Environment.ExitCode = 1;
    }
    await app.DisposeAsync();
    return;
}
// Se o argumento --migrate for passado, aplica as migrações pendentes.
if (args.Contains("--migrate", StringComparer.Ordinal))
{
    await app.Services.MigrateDatabaseAsync();
    await DatabaseProvisioning.ProvisionApplicationUserAsync(app.Services, builder.Configuration);
    return;
}
// Garante que a chave HMAC esteja configurada corretamente.
_ = app.Services.GetRequiredService<IFingerprints>();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
    await next(context);
});
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.MapControllers();
app.MapDevelopmentTokenEndpoint();
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapGet("/health/ready", async (FiscalDbContext db, CancellationToken ct) =>
{
    try
    {
        if (!await db.Database.CanConnectAsync(ct) || (await db.Database.GetPendingMigrationsAsync(ct)).Any())
        {
            return Results.StatusCode(503);
        }

        return Results.Ok(new { status = "ready" });
    }
    catch { return Results.StatusCode(503); }
});
if (builder.Configuration.GetValue<bool>("Database:ApplyMigrations"))
{
    await app.Services.MigrateDatabaseAsync();
}

await app.RunAsync();

public partial class Program;
