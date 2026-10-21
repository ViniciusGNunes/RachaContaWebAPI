using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using RachaPix.Api.Services;

namespace RachaPix.Api.Infrastructure;

/// <summary>Maps <see cref="AppException"/>s to RFC 7807 ProblemDetails responses.</summary>
public sealed class AppExceptionHandler(IProblemDetailsService problemDetails, ILogger<AppExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var correlationId = http.Items[CorrelationIdMiddleware.ItemKey] as string
            ?? (http.Response.Headers.TryGetValue(CorrelationIdMiddleware.HeaderName, out var respHeader) ? respHeader.ToString() : null)
            ?? (http.Request.Headers.TryGetValue(CorrelationIdMiddleware.HeaderName, out var reqHeader) ? reqHeader.ToString() : null)
            ?? http.TraceIdentifier;

        if (exception is not AppException app)
        {
            logger.LogError(exception, "Unhandled unexpected exception (CorrelationId: {CorrelationId}): {Message}", correlationId, exception.Message);
            return false;
        }

        ProblemDetails problem = app is ValidationException v
            ? new ValidationProblemDetails(v.Errors.ToDictionary(e => e.Key, e => e.Value)) { Detail = v.Message }
            : new ProblemDetails { Detail = app.Message };
        problem.Status = app.Status;
        problem.Title = app.Title;

        http.Response.StatusCode = app.Status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}

/// <summary>
/// Converts expected domain errors into ProblemDetails inside the MVC pipeline, so they are not
/// logged by the exception-handler middleware as "unhandled exceptions". Anything else still propagates.
/// </summary>
public sealed class AppExceptionFilter(ILogger<AppExceptionFilter> logger) : Microsoft.AspNetCore.Mvc.Filters.IExceptionFilter
{
    public void OnException(Microsoft.AspNetCore.Mvc.Filters.ExceptionContext context)
    {
        if (context.Exception is not AppException app) return;

        ProblemDetails problem = app is ValidationException v
            ? new ValidationProblemDetails(v.Errors.ToDictionary(e => e.Key, e => e.Value)) { Detail = v.Message }
            : new ProblemDetails { Detail = app.Message };
        problem.Status = app.Status;
        problem.Title = app.Title;
        problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;

        logger.LogInformation("{Status} {Title}: {Detail}", app.Status, app.Title, app.Message);
        context.Result = new ObjectResult(problem) { StatusCode = app.Status, ContentTypes = { "application/problem+json" } };
        context.ExceptionHandled = true;
    }
}
