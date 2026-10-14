using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RachaConta.Api.Infrastructure;

/// <summary>Writes decimals with at least 2 decimal places (money: 300 → 300.00) while keeping extra precision (percentages).</summary>
public sealed class MoneyDecimalConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? decimal.Parse(reader.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture)
            : reader.GetDecimal();

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
        writer.WriteRawValue(value.ToString("0.00##########", CultureInfo.InvariantCulture), skipInputValidation: true);
}

/// <summary>Writes DateTime as ISO-8601 UTC with a trailing Z.</summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTime().ToUniversalTime();

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(DateTime.SpecifyKind(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value, DateTimeKind.Utc)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
}

public static class JsonSetup
{
    public static void Configure(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        o.Converters.Add(new MoneyDecimalConverter());
        o.Converters.Add(new UtcDateTimeConverter());
        o.NumberHandling = JsonNumberHandling.Strict;
    }
}
