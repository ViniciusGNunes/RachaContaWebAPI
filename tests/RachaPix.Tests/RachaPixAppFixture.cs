using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RachaPix.Api.Data;
using RachaPix.Api.Infrastructure;

namespace RachaPix.Tests;

public class RachaPixAppFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath;
    private readonly SqliteConnection _keepAlive;

    public static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions();
        JsonSetup.Configure(options);
        return options;
    }

    public RachaPixAppFactory()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"rachaconta_test_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={_dbPath}";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={_dbPath}");
        builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(Microsoft.EntityFrameworkCore.DbContextOptions<RachaPix.Api.Data.AppDbContext>));
            if (descriptor != null) services.Remove(descriptor);

            services.AddDbContext<RachaPix.Api.Data.AppDbContext>(options =>
            {
                options.UseSqlite($"Data Source={_dbPath}");
                options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _keepAlive.Dispose();
            try
            {
                if (File.Exists(_dbPath)) File.Delete(_dbPath);
            }
            catch
            {
                // Ignore cleanup lock
            }
        }
    }
}

public static class HttpClientTestExtensions
{
    public static void SetGroupToken(this HttpClient client, string? token)
    {
        client.DefaultRequestHeaders.Remove("X-Group-Token");
        if (!string.IsNullOrEmpty(token))
        {
            client.DefaultRequestHeaders.Add("X-Group-Token", token);
        }
    }

    public static Task<HttpResponseMessage> PostAsJsonAsync<T>(this HttpClient client, string uri, T value) =>
        client.PostAsJsonAsync(uri, value, RachaPixAppFactory.JsonOptions);

    public static Task<HttpResponseMessage> PutAsJsonAsync<T>(this HttpClient client, string uri, T value) =>
        client.PutAsJsonAsync(uri, value, RachaPixAppFactory.JsonOptions);

    public static Task<T?> ReadFromJsonAsync<T>(this HttpContent content) =>
        content.ReadFromJsonAsync<T>(RachaPixAppFactory.JsonOptions);
}
