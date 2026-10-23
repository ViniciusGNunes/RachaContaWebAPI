using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RachaPix.Api.Data;
using RachaPix.Api.Infrastructure;
using RachaPix.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers(o => o.Filters.Add<AppExceptionFilter>()).AddJsonOptions(o => JsonSetup.Configure(o.JsonSerializerOptions));
builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var errors = new Dictionary<string, string[]>();
        foreach (var (key, value) in context.ModelState)
        {
            if (value.Errors.Count == 0) continue;
            var parts = key.Split('.');
            var camelKey = string.Join(".", parts.Select(p =>
            {
                var bracketIdx = p.IndexOf('[');
                if (bracketIdx == 0) return p;
                if (bracketIdx > 0)
                    return char.ToLowerInvariant(p[0]) + p[1..bracketIdx] + p[bracketIdx..];
                return p.Length > 0 ? char.ToLowerInvariant(p[0]) + p[1..] : p;
            }));
            errors[camelKey] = value.Errors.Select(e => e.ErrorMessage).ToArray();
        }
        var detail = string.Join(" ", errors.SelectMany(e => e.Value));
        var problem = new ValidationProblemDetails(errors)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more validation errors occurred.",
            Detail = detail,
        };
        problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
        return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
    };
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AppExceptionHandler>();

var connectionString = builder.WebHost.GetSetting("ConnectionStrings:Default")
    ?? builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");
var provider = builder.WebHost.GetSetting("Database:Provider") ?? builder.Configuration["Database:Provider"] ?? "Postgres";
builder.Services.AddDbContext<AppDbContext>(options =>
{
    switch (provider.ToLowerInvariant())
    {
        case "sqlite":
            options.UseSqlite(connectionString);
            break;
        case "postgres":
        case "postgresql":
            options.UseNpgsql(connectionString);
            break;
        default:
            throw new InvalidOperationException($"Unsupported Database:Provider '{provider}'. Use 'Sqlite' or 'Postgres'.");
    }
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<PixOptions>(builder.Configuration.GetSection("Pix"));
builder.Services.AddScoped<SplitService>();
builder.Services.AddSingleton<GroupMapper>();
builder.Services.AddScoped<GroupQueries>();
builder.Services.AddScoped<GroupService>();
builder.Services.AddSingleton<ExpenseMapper>();
builder.Services.AddScoped<ExpenseQueries>();
builder.Services.AddScoped<DeviceLinkService>();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = RateLimitRejection.WriteAsync;
    // Link codes are short, so cap guesses per client and group.
    o.AddPolicy("link-redeem", http => RateLimitPartition.GetFixedWindowLimiter(
        $"{http.Connection.RemoteIpAddress}|{http.Request.RouteValues["id"]}",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.AddScoped<ExpenseService>();

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? ["http://localhost:3000"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseRateLimiter();
app.MapControllers();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.Run();

public partial class Program;
