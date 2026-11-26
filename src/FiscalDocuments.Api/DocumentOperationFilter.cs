using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace FiscalDocuments.Api;

/// <summary>
/// Classe de configuração para o Swagger.
/// </summary>
public sealed class DocumentOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        // Quando o endpoint possui AllowAnonymous, não precisa de token.
        if (context.ApiDescription.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
        {
            operation.Security = [new OpenApiSecurityRequirement()];
        }
        var method = context.ApiDescription.HttpMethod;
        var path = context.ApiDescription.RelativePath;

        // Documentação adicional para o endpoint de upload de XMLs.
        if (method == "POST" && path == "api/v1/documents")
        {
            operation.Description = "Até 10 XMLs de 2 MiB. Transação e idempotência por arquivo. A resposta 200 contém os resultados individuais; reenvie o lote após interrupção.";
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["multipart/form-data"] = new()
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = "object", Required = new HashSet<string> { "files" },
                            Properties = new Dictionary<string, OpenApiSchema>
                            {
                                ["files"] = new() { Type = "array", MaxItems = 10, Items = new OpenApiSchema { Type = "string", Format = "binary" } }
                            }
                        }
                    }
                }
            };
        }
        // Documentação adicional para o endpoint de substituição de XMLs.
        if (method == "PUT")
        {
            operation.Description = "Substitui o XML somente após conclusão ou falha definitiva; mantém tipo e identidade. Conteúdo anterior não é preservado.";
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true, Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/xml"] = new() { Schema = new OpenApiSchema { Type = "string", Format = "binary" } }
                }
            };
        }
        // Documentação adicional para os endpoints de PUT e DELETE, incluindo /reprocess.
        if (method == "PUT" || method == "DELETE" || path?.EndsWith("/reprocess", StringComparison.Ordinal) == true)
        {
            operation.Parameters.Add(new OpenApiParameter { Name = "Idempotency-Key", In = ParameterLocation.Header,
                Required = true, Description = "8 a 128 caracteres ASCII. Retenção de 24 horas por usuário, operação e documento.", Schema = new OpenApiSchema { Type = "string" } });
            operation.Responses.TryAdd("409", new OpenApiResponse { Description = "Estado, identidade ou chave idempotente incompatível." });
        }
        // Documentação adicional para os endpoints de PUT e DELETE.
        if (method == "PUT" || method == "DELETE")
        {
            operation.Parameters.Add(new OpenApiParameter { Name = "If-Match", In = ParameterLocation.Header,
                Required = true, Description = "ETag retornado pelo GET, incluindo aspas.", Schema = new OpenApiSchema { Type = "string" } });
            operation.Responses.TryAdd("412", new OpenApiResponse { Description = "Documento alterado desde a leitura." });
            operation.Responses.TryAdd("428", new OpenApiResponse { Description = "If-Match obrigatório." });
        }
    }
}
