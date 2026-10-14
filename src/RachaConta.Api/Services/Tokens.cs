using System.Security.Cryptography;

namespace RachaConta.Api.Services;

public static class Tokens
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>8-char URL-safe (alphanumeric) public slug.</summary>
    public static string NewSlug() => RandomNumberGenerator.GetString(Alphabet, 8);

    /// <summary>32-char secret admin token (~190 bits).</summary>
    public static string NewAdminToken() => RandomNumberGenerator.GetString(Alphabet, 32);

    public static bool FixedTimeEquals(string? a, string b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
}
