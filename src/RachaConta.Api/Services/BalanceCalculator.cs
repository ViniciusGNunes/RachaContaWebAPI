namespace RachaConta.Api.Services;

public sealed record BalanceMember(Guid MemberId, string Name);
public sealed record BalanceExpense(Guid PaidByMemberId, decimal Amount, IReadOnlyList<(Guid MemberId, decimal Amount)> Shares);
public sealed record BalanceSettlement(Guid FromMemberId, Guid ToMemberId, decimal Amount);

public sealed record MemberBalance(Guid MemberId, string Name, decimal Paid, decimal Owed,
    decimal SettlementsSent, decimal SettlementsReceived, decimal Net);

public sealed record Transfer(Guid FromMemberId, string FromName, Guid ToMemberId, string ToName, decimal Amount);

public static class BalanceCalculator
{
    public static IReadOnlyList<MemberBalance> Compute(IReadOnlyList<BalanceMember> members,
        IEnumerable<BalanceExpense> expenses, IEnumerable<BalanceSettlement> settlements)
    {
        var paid = members.ToDictionary(m => m.MemberId, _ => 0m);
        var owed = members.ToDictionary(m => m.MemberId, _ => 0m);
        var sent = members.ToDictionary(m => m.MemberId, _ => 0m);
        var received = members.ToDictionary(m => m.MemberId, _ => 0m);

        foreach (var e in expenses)
        {
            Add(paid, e.PaidByMemberId, e.Amount);
            foreach (var (memberId, amount) in e.Shares) Add(owed, memberId, amount);
        }
        foreach (var s in settlements)
        {
            Add(sent, s.FromMemberId, s.Amount);
            Add(received, s.ToMemberId, s.Amount);
        }

        return members.Select(m => new MemberBalance(m.MemberId, m.Name, paid[m.MemberId], owed[m.MemberId],
                sent[m.MemberId], received[m.MemberId],
                paid[m.MemberId] - owed[m.MemberId] + sent[m.MemberId] - received[m.MemberId]))
            .ToList();
    }

    /// <summary>
    /// Greedy min-cash-flow: repeatedly the largest debtor pays the largest creditor
    /// min(|debt|, credit). Produces at most n-1 transfers.
    /// </summary>
    public static IReadOnlyList<Transfer> SuggestTransfers(IEnumerable<MemberBalance> balances)
    {
        var list = balances.ToList();
        var names = list.ToDictionary(b => b.MemberId, b => b.Name);
        // Stable tie-break by original order keeps output deterministic.
        var order = list.Select((b, i) => (b.MemberId, i)).ToDictionary(x => x.MemberId, x => x.i);
        var creditors = list.Where(b => b.Net > 0).Select(b => (Id: b.MemberId, Amount: b.Net)).ToList();
        var debtors = list.Where(b => b.Net < 0).Select(b => (Id: b.MemberId, Amount: -b.Net)).ToList();

        var transfers = new List<Transfer>();
        while (creditors.Count > 0 && debtors.Count > 0)
        {
            var c = creditors.OrderByDescending(x => x.Amount).ThenBy(x => order[x.Id]).First();
            var d = debtors.OrderByDescending(x => x.Amount).ThenBy(x => order[x.Id]).First();
            var amount = Math.Min(c.Amount, d.Amount);
            if (amount > 0)
                transfers.Add(new Transfer(d.Id, names[d.Id], c.Id, names[c.Id], amount));

            creditors.Remove(c);
            debtors.Remove(d);
            if (c.Amount - amount > 0) creditors.Add((c.Id, c.Amount - amount));
            if (d.Amount - amount > 0) debtors.Add((d.Id, d.Amount - amount));
        }
        return transfers;
    }

    private static void Add(Dictionary<Guid, decimal> map, Guid id, decimal amount)
    {
        if (map.ContainsKey(id)) map[id] += amount;
    }
}
