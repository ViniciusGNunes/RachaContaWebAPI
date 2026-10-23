using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace RachaPix.Api.Infrastructure;

/// <summary>Answers rate-limited requests with an RFC 7807 body and a Retry-After header.</summary>
public static class RateLimitRejection
{
    public static async ValueTask WriteAsync(OnRejectedContext context, CancellationToken ct)
    {
        var http = context.HttpContext;
        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        var problemDetails = http.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too Many Requests",
                Detail = "Too many attempts. Try again in a minute.",
            },
        });
    }
}
