using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using RachaConta.Api.Data;
using RachaConta.Api.Infrastructure;
using RachaConta.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers(o => o.Filters.Add<AppExceptionFilter>()).AddJsonOptions(o => JsonSetup.Configure(o.JsonSerializerOptions));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AppExceptionHandler>();

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");
var provider = builder.Configuration["Database:Provider"] ?? "Postgres";
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
builder.Services.AddScoped<GroupService>();
builder.Services.AddScoped<DeviceLinkService>();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
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

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseRateLimiter();
app.MapControllers();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.Run();

public partial class Program;
