using Microsoft.EntityFrameworkCore;
using RachaPix.Api.Contracts;
using RachaPix.Api.Data;
using RachaPix.Api.Domain;

namespace RachaPix.Api.Services;

/// <summary>Query operations for expenses, settlements, balances and stats (Module 2).</summary>
public sealed class ExpenseQueries(AppDbContext db, ExpenseMapper mapper)
{
    public ExpenseQueries(AppDbContext db) : this(db, new ExpenseMapper()) { }

    public async Task EnsureGroupAsync(Guid groupId, CancellationToken ct)
    {
        if (!await db.Groups.AnyAsync(g => g.Id == groupId, ct))
            throw new NotFoundException($"Group '{groupId}' not found.");
    }

    /// <summary>Members of the group keyed by id (throws 404 if the group doesn't exist).</summary>
    public async Task<Dictionary<Guid, Member>> LoadMembersAsync(Guid groupId, CancellationToken ct)
    {
        await EnsureGroupAsync(groupId, ct);
        var members = await db.Members.AsNoTracking().Where(m => m.GroupId == groupId).ToListAsync(ct);
        return members.ToDictionary(m => m.Id);
    }

    public static List<BalanceMember> OrderedMembers(Dictionary<Guid, Member> members) =>
        members.Values.OrderBy(m => m.CreatedAt).Select(m => new BalanceMember(m.Id, m.Name)).ToList();

    public async Task<Expense> LoadExpenseAsync(Guid groupId, Guid expenseId, CancellationToken ct, bool tracking = false)
    {
        var query = db.Expenses.Include(e => e.Shares).Include(e => e.Confirmations).Where(e => e.Id == expenseId && e.GroupId == groupId);
        if (!tracking) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(ct)
               ?? throw new NotFoundException($"Expense '{expenseId}' not found in this group.");
    }

    public async Task<IReadOnlyList<ExpenseDto>> ListExpensesAsync(Guid groupId, ExpenseFilter filter, CancellationToken ct)
    {
        var members = await LoadMembersAsync(groupId, ct);
        var errors = new ValidationErrors();
        filter.Validate(errors);
        errors.ThrowIfAny();

        var query = db.Expenses.AsNoTracking().Include(e => e.Shares).Include(e => e.Confirmations).Where(e => e.GroupId == groupId);
        if (filter.From is { } from) query = query.Where(e => e.Date >= from);
        if (filter.To is { } to) query = query.Where(e => e.Date <= to);
        if (filter.Category is { } category) query = query.Where(e => e.Category == category);
        if (filter.MemberId is { } memberId)
            query = query.Where(e => e.PaidByMemberId == memberId || e.Shares.Any(s => s.MemberId == memberId));

        var expenses = await query.ToListAsync(ct);
        return expenses
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.CreatedAt)
            .Select(e => mapper.ToDto(e, members))
            .ToList();
    }

    public async Task<IReadOnlyList<SettlementDto>> ListSettlementsAsync(Guid groupId, CancellationToken ct)
    {
        var members = await LoadMembersAsync(groupId, ct);
        var settlements = await db.Settlements.AsNoTracking().Where(s => s.GroupId == groupId).ToListAsync(ct);
        return settlements
            .OrderByDescending(s => s.Date).ThenByDescending(s => s.CreatedAt)
            .Select(s => mapper.ToDto(s, members))
            .ToList();
    }

    public async Task<BalanceDto> GetBalancesAsync(Guid groupId, CancellationToken ct)
    {
        var members = await LoadMembersAsync(groupId, ct);
        var expenses = await db.Expenses.AsNoTracking().Include(e => e.Shares).Where(e => e.GroupId == groupId).ToListAsync(ct);
        var settlements = await db.Settlements.AsNoTracking()
            .Where(s => s.GroupId == groupId && s.Status == SettlementStatus.Confirmed).ToListAsync(ct);

        var balances = BalanceCalculator.Compute(
            OrderedMembers(members),
            expenses.Select(e => new BalanceExpense(e.PaidByMemberId, e.Amount, e.Shares.Select(s => (s.MemberId, s.Amount)).ToList())),
            settlements.Select(s => new BalanceSettlement(s.FromMemberId, s.ToMemberId, s.Amount)));
        var transfers = BalanceCalculator.SuggestTransfers(balances);

        return new BalanceDto(groupId,
            balances.Select(b => new MemberBalanceDto(b.MemberId, b.Name, b.Paid, b.Owed, b.SettlementsSent, b.SettlementsReceived, b.Net)).ToList(),
            transfers.Select(t => new TransferDto(t.FromMemberId, t.FromName, t.ToMemberId, t.ToName, t.Amount)).ToList());
    }

    public async Task<StatsDto> GetStatsAsync(Guid groupId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var members = await LoadMembersAsync(groupId, ct);
        var filter = new ExpenseFilter(from, to, null, null);
        var errors = new ValidationErrors();
        filter.Validate(errors);
        errors.ThrowIfAny();

        var query = db.Expenses.AsNoTracking().Include(e => e.Shares).Where(e => e.GroupId == groupId);
        if (from is { } fromDate) query = query.Where(e => e.Date >= fromDate);
        if (to is { } toDate) query = query.Where(e => e.Date <= toDate);
        var expenses = await query.ToListAsync(ct);

        var balances = BalanceCalculator.Compute(OrderedMembers(members),
            expenses.Select(e => new BalanceExpense(e.PaidByMemberId, e.Amount, e.Shares.Select(s => (s.MemberId, s.Amount)).ToList())),
            []);

        return new StatsDto(
            groupId,
            expenses.Sum(e => e.Amount),
            expenses.GroupBy(e => e.Category)
                .Select(g => new CategoryTotalDto(g.Key, g.Sum(e => e.Amount)))
                .OrderByDescending(x => x.Total).ThenBy(x => x.Category).ToList(),
            balances.Select(b => new MemberStatsDto(b.MemberId, b.Name, b.Paid, b.Owed)).ToList(),
            expenses.GroupBy(e => e.Date.ToString("yyyy-MM"))
                .Select(g => new MonthTotalDto(g.Key, g.Sum(e => e.Amount)))
                .OrderBy(x => x.Month, StringComparer.Ordinal).ToList());
    }
}
