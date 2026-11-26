using FiscalDocuments.Application;
using FiscalDocuments.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace FiscalDocuments.Api;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken ct)
    {
        var (status, code) = exception switch
        {
            AppException app => (app.StatusCode, app.Code),
            DomainException domain => (409, domain.Code),
            BadHttpRequestException bad => (bad.StatusCode, "invalid_request"),
            InvalidDataException => (400, "invalid_multipart"),
            _ => (500, "internal_error")
        };
        if (status >= 500)
        {
            logger.LogError("Request failed with code {ErrorCode}; trace {TraceId}; type {ExceptionType}; stack {Stack}",
            code, httpContext.TraceIdentifier, exception.GetType().Name, exception.StackTrace);
        }

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status, Title = code, Type = $"urn:fiscal-documents:error:{code}",
            Extensions = { ["traceId"] = httpContext.TraceIdentifier }
        }, cancellationToken: ct);
        return true;
    }
}
