using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RachaPix.Api.Domain;

namespace RachaPix.Api.Services;

/// <summary>CRC16-CCITT-FALSE (poly 0x1021, init 0xFFFF, no reflection, no final xor).</summary>
public static class Crc16
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    public static string ComputeHex(string text) => Compute(Encoding.UTF8.GetBytes(text)).ToString("X4");
}

public static class PixText
{
    /// <summary>Uppercase, strip accents, keep only A-Z, 0-9 and single spaces, truncate.</summary>
    public static string Sanitize(string? input, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var sb = new StringBuilder(input.Length);
        foreach (var c in input.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            var u = char.ToUpperInvariant(c);
            if (u is >= 'A' and <= 'Z' or >= '0' and <= '9') sb.Append(u);
            else if (char.IsWhiteSpace(u) || u is '-' or '.' or '_' or '\'') sb.Append(' ');
        }
        var collapsed = Regex.Replace(sb.ToString(), " {2,}", " ").Trim();
        return collapsed.Length <= maxLength ? collapsed : collapsed[..maxLength].TrimEnd();
    }

    /// <summary>Alphanumeric only, max 25 chars; "***" when empty.</summary>
    public static string TxId(string? input)
    {
        var alnum = new string((input ?? "").Where(char.IsAsciiLetterOrDigit).ToArray());
        if (alnum.Length > 25) alnum = alnum[..25];
        return alnum.Length == 0 ? "***" : alnum;
    }
}

public static partial class PixKeyNormalizer
{
    /// <summary>Normalizes a Pix key. Returns false (with an error message) when invalid.</summary>
    public static bool TryNormalize(PixKeyType type, string? raw, out string normalized, out string error)
    {
        normalized = "";
        error = "";
        var value = (raw ?? "").Trim();
        if (value.Length == 0)
        {
            error = "Pix key is required.";
            return false;
        }

        switch (type)
        {
            case PixKeyType.Cpf:
            {
                var digits = Digits(value);
                if (digits.Length != 11 || !IsValidCpf(digits)) { error = "Invalid CPF."; return false; }
                normalized = digits;
                return true;
            }
            case PixKeyType.Cnpj:
            {
                var digits = Digits(value);
                if (digits.Length != 14 || !IsValidCnpj(digits)) { error = "Invalid CNPJ."; return false; }
                normalized = digits;
                return true;
            }
            case PixKeyType.Email:
            {
                var email = value.ToLowerInvariant();
                if (email.Length > 77 || !EmailRegex().IsMatch(email)) { error = "Invalid email."; return false; }
                normalized = email;
                return true;
            }
            case PixKeyType.Phone:
            {
                var digits = Digits(value);
                // Accept national (DDD + 8/9 digits) or international with 55 prefix.
                if (digits.Length is 12 or 13 && digits.StartsWith("55")) digits = digits[2..];
                if (digits.Length is not (10 or 11) || digits[0] == '0')
                {
                    error = "Invalid phone. Use DDD + number, e.g. +5511987654321.";
                    return false;
                }
                normalized = "+55" + digits;
                return true;
            }
            case PixKeyType.Random:
            {
                if (!Guid.TryParse(value, out var guid)) { error = "Invalid random key (EVP UUID)."; return false; }
                normalized = guid.ToString("D");
                return true;
            }
            default:
                error = "Unknown Pix key type.";
                return false;
        }
    }

    private static string Digits(string s) => new(s.Where(char.IsAsciiDigit).ToArray());

    public static bool IsValidCpf(string d)
    {
        if (d.Length != 11 || d.Distinct().Count() == 1) return false;
        return CheckDigit(d[..9], [10, 9, 8, 7, 6, 5, 4, 3, 2]) == d[9] - '0'
            && CheckDigit(d[..10], [11, 10, 9, 8, 7, 6, 5, 4, 3, 2]) == d[10] - '0';
    }

    public static bool IsValidCnpj(string d)
    {
        if (d.Length != 14 || d.Distinct().Count() == 1) return false;
        return CheckDigit(d[..12], [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]) == d[12] - '0'
            && CheckDigit(d[..13], [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]) == d[13] - '0';
    }

    private static int CheckDigit(string digits, int[] weights)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++) sum += (digits[i] - '0') * weights[i];
        var r = sum % 11;
        return r < 2 ? 0 : 11 - r;
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();
}

/// <summary>Builds a static Pix BR Code (EMV MPM) payload.</summary>
public static class PixPayload
{
    public const int MaxNameLength = 25;
    public const int MaxCityLength = 15;

    /// <param name="pixKey">Already-normalized Pix key.</param>
    /// <param name="receiverName">Raw name; sanitized to 25 uppercase chars without accents.</param>
    /// <param name="receiverCity">Raw city; sanitized to 15 uppercase chars without accents.</param>
    /// <param name="amount">Optional amount (omitted when null or 0).</param>
    /// <param name="txId">Optional txid; sanitized to 25 alphanumerics, "***" when empty.</param>
    /// <param name="description">Optional description (field 26.02), dropped if it does not fit.</param>
    public static string Build(string pixKey, string receiverName, string receiverCity, decimal? amount,
        string? txId = null, string? description = null)
    {
        var name = PixText.Sanitize(receiverName, MaxNameLength);
        var city = PixText.Sanitize(receiverCity, MaxCityLength);
        if (name.Length == 0) throw new ArgumentException("Receiver name is empty after sanitization.", nameof(receiverName));
        if (city.Length == 0) throw new ArgumentException("Receiver city is empty after sanitization.", nameof(receiverCity));

        var merchantAccount = Field("00", "br.gov.bcb.pix") + Field("01", pixKey);
        var desc = PixText.Sanitize(description, 72);
        if (desc.Length > 0)
        {
            var room = 99 - merchantAccount.Length - 4;
            if (room > 0) merchantAccount += Field("02", desc.Length <= room ? desc : desc[..room].TrimEnd());
        }

        var sb = new StringBuilder();
        sb.Append(Field("00", "01"));
        sb.Append(Field("26", merchantAccount));
        sb.Append(Field("52", "0000"));
        sb.Append(Field("53", "986"));
        if (amount is > 0) sb.Append(Field("54", amount.Value.ToString("0.00", CultureInfo.InvariantCulture)));
        sb.Append(Field("58", "BR"));
        sb.Append(Field("59", name));
        sb.Append(Field("60", city));
        sb.Append(Field("62", Field("05", PixText.TxId(txId))));
        sb.Append("6304");
        sb.Append(Crc16.ComputeHex(sb.ToString()));
        return sb.ToString();
    }

    public static string Field(string id, string value)
    {
        if (value.Length > 99) throw new ArgumentException($"EMV field {id} exceeds 99 characters.", nameof(value));
        return id + value.Length.ToString("00", CultureInfo.InvariantCulture) + value;
    }
}
