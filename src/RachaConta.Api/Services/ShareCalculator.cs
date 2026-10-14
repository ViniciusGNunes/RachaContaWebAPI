namespace RachaConta.Api.Services;

/// <summary>Pure money-splitting math. All amounts are BRL with 2 decimal places.</summary>
public static class ShareCalculator
{
    public static bool HasAtMostTwoDecimals(decimal value) => decimal.Round(value, 2) == value;

    /// <summary>
    /// total / n rounded down to cents; leftover cents go to the first entries one by one,
    /// so the sum is exactly <paramref name="total"/>.
    /// </summary>
    public static decimal[] Equal(decimal total, int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (total < 0 || !HasAtMostTwoDecimals(total)) throw new ArgumentOutOfRangeException(nameof(total));

        var totalCents = (long)(total * 100);
        var baseCents = totalCents / count;
        var leftover = totalCents - baseCents * count;

        var result = new decimal[count];
        for (var i = 0; i < count; i++)
            result[i] = (baseCents + (i < leftover ? 1 : 0)) / 100m;
        return result;
    }

    /// <summary>
    /// Amount per percentage, rounded down to cents; leftover cents go to the first entries
    /// one by one so the sum is exactly <paramref name="total"/>.
    /// Percentages must sum to 100 (±0.01) — validate with <see cref="PercentagesSumTo100"/>.
    /// </summary>
    public static decimal[] Percentage(decimal total, IReadOnlyList<decimal> percentages)
    {
        if (percentages.Count == 0) throw new ArgumentException("No percentages.", nameof(percentages));
        if (total < 0 || !HasAtMostTwoDecimals(total)) throw new ArgumentOutOfRangeException(nameof(total));

        var totalCents = (long)(total * 100);
        var sumPct = percentages.Sum();
        // Normalize by the actual sum so a 99.99/100.01 tolerance can't over/under-allocate.
        var cents = percentages.Select(p => (long)decimal.Floor(totalCents * p / sumPct)).ToArray();
        var leftover = totalCents - cents.Sum();
        for (var i = 0; leftover > 0; i = (i + 1) % cents.Length, leftover--)
            cents[i]++;
        return cents.Select(c => c / 100m).ToArray();
    }

    public static bool PercentagesSumTo100(IEnumerable<decimal> percentages) =>
        Math.Abs(percentages.Sum() - 100m) <= 0.01m;

    public static bool CustomSumMatches(decimal total, IEnumerable<decimal> amounts) =>
        amounts.Sum() == total;
}
