using Microsoft.EntityFrameworkCore;
using RachaConta.Api.Contracts;
using RachaConta.Api.Controllers;
using RachaConta.Api.Data;
using RachaConta.Api.Domain;

namespace RachaConta.Api.Services;

/// <summary>Expenses, settlements, balances and stats of a group (Module 2).</summary>
public sealed class ExpenseService(AppDbContext db, TimeProvider clock)
{
    // ---------- Expenses ----------

    public async Task<IReadOnlyList<ExpenseDto>> ListAsync(Guid groupId, ExpenseFilter filter, CancellationToken ct)
    {
        var members = await LoadMembersAsync(groupId, ct);
        if (filter.From is { } f && filter.To is { } t && f > t)
            throw new ValidationException("from", "'from' must be on or before 'to'.");

        var query = db.Expenses.AsNoTracking().Include(e => e.Shares).Include(e => e.Confirmations).Where(e => e.GroupId == groupId);
        if (filter.From is { } from) query = query.Where(e => e.Date >= from);
        if (filter.To is { } to) query = query.Where(e => e.Date <= to);
        if (filter.Category is { } category) query = query.Where(e => e.Category == category);
        if (filter.MemberId is { } memberId)
            query = query.Where(e => e.PaidByMemberId == memberId || e.Shares.Any(s => s.MemberId == memberId));

        var expenses = await query.ToListAsync(ct);
        return expenses
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.CreatedAt)
            .Select(e => ToDto(e, members))
            .ToList();
    }

    public async Task<ExpenseDto> GetAsync(Guid groupId, Guid expenseId, CancellationToken ct)
    {
        var members = await LoadMembersAsync(groupId, ct);
        var expense = await LoadExpenseAsync(groupId, expenseId, ct);
        return ToDto(expense, members);
    }

    public async Task<ExpenseDto> CreateAsync(Guid groupId, ExpenseRequest req, GroupActor actor, CancellationToken ct)
    {
        var members = await LoadMembersAsync(groupId, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var expense = new Expense
        {
            Id = Guid.NewGuid(), GroupId = groupId, CreatedAt = now, CreatedByMemberId = actor.MemberId,
        };
        Apply(expense, req, members);
        expense.Confirmations = AutoConfirmations(expense, actor.MemberId, now);
        db.Expenses.Add(expense);
        await db.SaveChangesAsync(ct);
        return ToDto(expense, members);
    }

    public async Task<ExpenseDto> UpdateAsync(Guid groupId, Guid expenseId, ExpenseRequest req, GroupActor actor, CancellationToken ct)
    {
        var members = await LoadMembersAsync(groupId, ct);
        var expense = await LoadExpenseAsync(groupId, expenseId, ct, tracking: true);
        if (!actor.CanModify(expense.CreatedByMemberId))
            throw new ForbiddenException("Only the person who added this expense, or the admin, can edit it.");

        var now = clock.GetUtcNow().UtcDateTime;
        var oldShares = expense.Shares.ToList();
        var oldConfirmations = expense.Confirmations.ToList();
        Apply(expense, req, members);
        expense.UpdatedByMemberId = actor.MemberId;
        expense.UpdatedAt = now;
        // The numbers changed, so earlier confirmations no longer apply.
        expense.Confirmations = AutoConfirmations(expense, actor.MemberId, now);
        db.ExpenseConfirmations.RemoveRange(oldConfirmations);
        db.ExpenseShares.RemoveRange(oldShares);
        db.ExpenseShares.AddRange(expense.Shares);
        db.ExpenseConfirmations.AddRange(expense.Confirmations);
        await db.SaveChangesAsync(ct);
        return ToDto(expense, members);
    }

    public async Task DeleteAsync(Guid groupId, Guid expenseId, GroupActor actor, CancellationToken ct)
    {
        await EnsureGroupAsync(groupId, ct);
        var expense = await LoadExpenseAsync(groupId, expenseId, ct, tracking: true);
        if (!actor.CanModify(expense.CreatedByMemberId))
            throw new ForbiddenException("Only the person who added this expense, or the admin, can delete it.");
        db.Expenses.Remove(expense);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A participant confirms or contests an expense that was split with them.</summary>
    public async Task<ExpenseDto> ConfirmAsync(Guid groupId, Guid expenseId, ConfirmationRequest req, GroupActor actor, CancellationToken ct)
    {
        var members = await LoadMembersAsync(groupId, ct);
        var expense = await LoadExpenseAsync(groupId, expenseId, ct, tracking: true);

        if (actor.MemberId is not { } me || !RequiredConfirmers(expense).Contains(me))
            throw new ForbiddenException("Only the people this expense was split with can confirm or contest it.");
        if (req.Status is not { } status)
            throw new ValidationException("status", "Status is required (Confirmed or Contested).");
        var comment = string.IsNullOrWhiteSpace(req.Comment) ? null : req.Comment.Trim();
        if (comment?.Length > 200) throw new ValidationException("comment", "Comment must have at most 200 characters.");

        var now = clock.GetUtcNow().UtcDateTime;
        var existing = expense.Confirmations.FirstOrDefault(c => c.MemberId == me);
        if (existing is null)
        {
            var created = new ExpenseConfirmation
            {
                Id = Guid.NewGuid(), ExpenseId = expense.Id, MemberId = me, Status = status, Comment = comment, CreatedAt = now,
            };
            db.ExpenseConfirmations.Add(created); // Added explicitly; EF links it to the tracked expense
        }
        else
        {
            existing.Status = status;
            existing.Comment = comment;
            existing.CreatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return ToDto(expense, members);
    }

    /// <summary>Members who must confirm: everyone sharing the expense except whoever paid it.</summary>
    public static HashSet<Guid> RequiredConfirmers(Expense expense) =>
        expense.Shares.Select(s => s.MemberId).Where(id => id != expense.PaidByMemberId).ToHashSet();

    /// <summary>The person who adds or edits an expense already agrees with it, if they take part in it.</summary>
    private static List<ExpenseConfirmation> AutoConfirmations(Expense expense, Guid? actorMemberId, DateTime now) =>
        actorMemberId is { } me && RequiredConfirmers(expense).Contains(me)
            ? [new ExpenseConfirmation { Id = Guid.NewGuid(), ExpenseId = expense.Id, MemberId = me, Status = ConfirmationStatus.Confirmed, CreatedAt = now }]
            : [];

    /// <summary>Validates the request and writes fields + computed shares into <paramref name="expense"/>.</summary>
    public static void Apply(Expense expense, ExpenseRequest req, IReadOnlyDictionary<Guid, Member> members)
    {
        var errors = new ValidationErrors();

        var description = req.Description?.Trim() ?? "";
        if (description.Length is < 1 or > 120) errors.Add("description", "Description must have 1 to 120 characters.");

        var amount = req.Amount ?? 0;
        if (req.Amount is null) errors.Add("amount", "Amount is required.");
        else if (amount <= 0 || amount > SplitService.MaxTotal) errors.Add("amount", "Amount must be greater than 0 and at most 1,000,000.");
        else if (!ShareCalculator.HasAtMostTwoDecimals(amount)) errors.Add("amount", "Amount must have at most 2 decimal places.");

        if (req.Date is null) errors.Add("date", "Date is required (yyyy-MM-dd).");
        if (req.PaidByMemberId is not { } paidBy) errors.Add("paidByMemberId", "Paying member is required.");
        else if (!members.ContainsKey(paidBy)) errors.Add("paidByMemberId", "Paying member does not belong to this group.");

        var notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim();
        if (notes?.Length > 500) errors.Add("notes", "Notes must have at most 500 characters.");

        if (req.ShareMode is null) errors.Add("shareMode", "Share mode is required.");
        var shares = req.Shares ?? [];
        if (shares.Count == 0) errors.Add("shares", "At least one member must share the expense.");

        var seen = new HashSet<Guid>();
        for (var i = 0; i < shares.Count; i++)
        {
            var s = shares[i];
            if (s?.MemberId is not { } memberId) { errors.Add($"shares[{i}].memberId", "Member is required."); continue; }
            if (!members.ContainsKey(memberId)) errors.Add($"shares[{i}].memberId", "Member does not belong to this group.");
            else if (!seen.Add(memberId)) errors.Add($"shares[{i}].memberId", "Member is listed more than once.");

            switch (req.ShareMode)
            {
                case ShareMode.Custom:
                    if (s.Amount is not { } a) errors.Add($"shares[{i}].amount", "Amount is required in Custom mode.");
                    else if (a <= 0) errors.Add($"shares[{i}].amount", "Amount must be greater than 0.");
                    else if (!ShareCalculator.HasAtMostTwoDecimals(a)) errors.Add($"shares[{i}].amount", "Amount must have at most 2 decimal places.");
                    break;
                case ShareMode.Percentage:
                    if (s.Percentage is not { } p) errors.Add($"shares[{i}].percentage", "Percentage is required in Percentage mode.");
                    else if (p <= 0 || p > 100) errors.Add($"shares[{i}].percentage", "Percentage must be greater than 0 and at most 100.");
                    else if (decimal.Round(p, 4) != p) errors.Add($"shares[{i}].percentage", "Percentage must have at most 4 decimal places.");
                    break;
            }
        }
        errors.ThrowIfAny();

        var mode = req.ShareMode!.Value;
        var memberIds = shares.Select(s => s!.MemberId!.Value).ToList();
        decimal[] amounts;
        decimal?[] percentages = new decimal?[shares.Count];
        switch (mode)
        {
            case ShareMode.Equal:
                amounts = ShareCalculator.Equal(amount, shares.Count);
                break;
            case ShareMode.Custom:
                amounts = shares.Select(s => s!.Amount!.Value).ToArray();
                if (!ShareCalculator.CustomSumMatches(amount, amounts))
                    throw new ValidationException("shares", $"Share amounts sum to {amounts.Sum():0.00} but the expense amount is {amount:0.00}.");
                break;
            default:
                var pcts = shares.Select(s => s!.Percentage!.Value).ToArray();
                if (!ShareCalculator.PercentagesSumTo100(pcts))
                    throw new ValidationException("shares", $"Percentages sum to {pcts.Sum():0.####} but must sum to 100.");
                amounts = ShareCalculator.Percentage(amount, pcts);
                percentages = pcts.Select(p => (decimal?)p).ToArray();
                break;
        }

        expense.Description = description;
        expense.Amount = amount;
        expense.Date = req.Date!.Value;
        expense.Category = req.Category ?? ExpenseCategory.Uncategorized;
        expense.PaymentMethod = req.PaymentMethod ?? PaymentMethod.Other;
        expense.PaidByMemberId = req.PaidByMemberId!.Value;
        expense.ShareMode = mode;
        expense.Notes = notes;
        expense.Shares = memberIds.Select((id, i) => new ExpenseShare
        {
            Id = Guid.NewGuid(),
            ExpenseId = expense.Id,
            Position = i,
            MemberId = id,
            Amount = amounts[i],
            Percentage = percentages[i],
        }).ToList();
    }

    // ---------- Settlements ----------

    public async Task<IReadOnlyList<SettlementDto>> ListSettlementsAsync(Guid groupId, CancellationToken ct)
    {
        var members = await LoadMembersAsync(groupId, ct);
        var settlements = await db.Settlements.AsNoTracking().Where(s => s.GroupId == groupId).ToListAsync(ct);
        return settlements
            .OrderByDescending(s => s.Date).ThenByDescending(s => s.CreatedAt)
            .Select(s => ToDto(s, members))
            .ToList();
    }

    public async Task<SettlementDto> CreateSettlementAsync(Guid groupId, CreateSettlementRequest req, GroupActor actor, CancellationToken ct)
    {
        var members = await LoadMembersAsync(groupId, ct);
        var errors = new ValidationErrors();

        if (req.FromMemberId is not { } from) errors.Add("fromMemberId", "Paying member is required.");
        else if (!members.ContainsKey(from)) errors.Add("fromMemberId", "Member does not belong to this group.");
        if (req.ToMemberId is not { } to) errors.Add("toMemberId", "Receiving member is required.");
        else if (!members.ContainsKey(to)) errors.Add("toMemberId", "Member does not belong to this group.");
        if (req.FromMemberId is not null && req.FromMemberId == req.ToMemberId)
            errors.Add("toMemberId", "A member cannot pay themselves.");

        if (req.Amount is not { } amount) errors.Add("amount", "Amount is required.");
        else if (amount <= 0 || amount > SplitService.MaxTotal) errors.Add("amount", "Amount must be greater than 0 and at most 1,000,000.");
        else if (!ShareCalculator.HasAtMostTwoDecimals(amount)) errors.Add("amount", "Amount must have at most 2 decimal places.");

        if (req.Date is null) errors.Add("date", "Date is required (yyyy-MM-dd).");
        var notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim();
        if (notes?.Length > 500) errors.Add("notes", "Notes must have at most 500 characters.");
        errors.ThrowIfAny();

        if (!actor.IsAdmin && actor.MemberId != req.FromMemberId && actor.MemberId != req.ToMemberId)
            throw new ForbiddenException("You can only register payments you made or received.");

        // Money received counts only after the receiver confirms. The receiver's own entry, or an admin
        // acting for others, is confirmed right away.
        var now = clock.GetUtcNow().UtcDateTime;
        var confirmedNow = actor.MemberId == req.ToMemberId || (actor.IsAdmin && actor.MemberId != req.FromMemberId);

        var settlement = new Settlement
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            FromMemberId = req.FromMemberId!.Value,
            ToMemberId = req.ToMemberId!.Value,
            Amount = req.Amount!.Value,
            Date = req.Date!.Value,
            PaymentMethod = req.PaymentMethod ?? PaymentMethod.Other,
            Notes = notes,
            CreatedAt = now,
            CreatedByMemberId = actor.MemberId,
            Status = confirmedNow ? SettlementStatus.Confirmed : SettlementStatus.Pending,
            ConfirmedAt = confirmedNow ? now : null,
        };
        db.Settlements.Add(settlement);
        await db.SaveChangesAsync(ct);
        return ToDto(settlement, members);
    }

    public async Task<SettlementDto> ConfirmSettlementAsync(Guid groupId, Guid settlementId, GroupActor actor, CancellationToken ct)
    {
        var members = await LoadMembersAsync(groupId, ct);
        var settlement = await db.Settlements.FirstOrDefaultAsync(s => s.Id == settlementId && s.GroupId == groupId, ct)
                         ?? throw new NotFoundException($"Settlement '{settlementId}' not found in this group.");
        if (!actor.IsAdmin && actor.MemberId != settlement.ToMemberId)
            throw new ForbiddenException("Only the person who received the money can confirm it.");

        if (settlement.Status != SettlementStatus.Confirmed)
        {
            settlement.Status = SettlementStatus.Confirmed;
            settlement.ConfirmedAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
        }
        return ToDto(settlement, members);
    }

    public async Task DeleteSettlementAsync(Guid groupId, Guid settlementId, GroupActor actor, CancellationToken ct)
    {
        await EnsureGroupAsync(groupId, ct);
        var settlement = await db.Settlements.FirstOrDefaultAsync(s => s.Id == settlementId && s.GroupId == groupId, ct)
                         ?? throw new NotFoundException($"Settlement '{settlementId}' not found in this group.");
        // The receiver may also refuse a payment that is still waiting for confirmation.
        var refusal = settlement.Status == SettlementStatus.Pending && actor.MemberId == settlement.ToMemberId;
        if (!actor.CanModify(settlement.CreatedByMemberId) && !refusal)
            throw new ForbiddenException("Only the person who registered this payment, or the admin, can remove it.");
        db.Settlements.Remove(settlement);
        await db.SaveChangesAsync(ct);
    }

    // ---------- Balances & stats ----------

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
        if (from is { } f && to is { } t && f > t)
            throw new ValidationException("from", "'from' must be on or before 'to'.");

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

    // ---------- Helpers ----------

    private async Task EnsureGroupAsync(Guid groupId, CancellationToken ct)
    {
        if (!await db.Groups.AnyAsync(g => g.Id == groupId, ct))
            throw new NotFoundException($"Group '{groupId}' not found.");
    }

    /// <summary>Members of the group keyed by id (throws 404 if the group doesn't exist).</summary>
    private async Task<Dictionary<Guid, Member>> LoadMembersAsync(Guid groupId, CancellationToken ct)
    {
        await EnsureGroupAsync(groupId, ct);
        var members = await db.Members.AsNoTracking().Where(m => m.GroupId == groupId).ToListAsync(ct);
        return members.ToDictionary(m => m.Id);
    }

    private static List<BalanceMember> OrderedMembers(Dictionary<Guid, Member> members) =>
        members.Values.OrderBy(m => m.CreatedAt).Select(m => new BalanceMember(m.Id, m.Name)).ToList();

    private async Task<Expense> LoadExpenseAsync(Guid groupId, Guid expenseId, CancellationToken ct, bool tracking = false)
    {
        var query = db.Expenses.Include(e => e.Shares).Include(e => e.Confirmations).Where(e => e.Id == expenseId && e.GroupId == groupId);
        if (!tracking) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(ct)
               ?? throw new NotFoundException($"Expense '{expenseId}' not found in this group.");
    }

    private static string NameOf(Dictionary<Guid, Member> members, Guid id) =>
        members.TryGetValue(id, out var m) ? m.Name : "";

    private static ExpenseDto ToDto(Expense e, Dictionary<Guid, Member> members) => new(
        e.Id, e.GroupId, e.Description, e.Amount, e.Date, e.Category, e.PaymentMethod,
        e.PaidByMemberId, NameOf(members, e.PaidByMemberId), e.ShareMode,
        e.Shares.OrderBy(s => s.Position)
            .Select(s => new ExpenseShareDto(s.MemberId, NameOf(members, s.MemberId), s.Amount, s.Percentage)).ToList(),
        e.Notes, e.CreatedAt,
        e.CreatedByMemberId, e.CreatedByMemberId is { } cb ? NameOf(members, cb) : null,
        e.UpdatedByMemberId is { } ub ? NameOf(members, ub) : null, e.UpdatedAt,
        e.Confirmations.OrderBy(c => c.CreatedAt)
            .Select(c => new ExpenseConfirmationDto(c.MemberId, NameOf(members, c.MemberId), c.Status, c.Comment, c.CreatedAt)).ToList());

    private static SettlementDto ToDto(Settlement s, Dictionary<Guid, Member> members) => new(
        s.Id, s.GroupId, s.FromMemberId, NameOf(members, s.FromMemberId), s.ToMemberId, NameOf(members, s.ToMemberId),
        s.Amount, s.Date, s.PaymentMethod, s.Notes, s.CreatedAt,
        s.CreatedByMemberId, s.CreatedByMemberId is { } cb ? NameOf(members, cb) : null, s.Status, s.ConfirmedAt);
}
