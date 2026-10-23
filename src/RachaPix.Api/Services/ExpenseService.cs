using Microsoft.EntityFrameworkCore;
using RachaPix.Api.Contracts;
using RachaPix.Api.Controllers;
using RachaPix.Api.Data;
using RachaPix.Api.Domain;

namespace RachaPix.Api.Services;

/// <summary>Expenses, settlements, balances and stats orchestration (Module 2).</summary>
public sealed class ExpenseService(AppDbContext db, TimeProvider clock, ExpenseQueries queries, ExpenseMapper mapper)
{
    public ExpenseService(AppDbContext db, TimeProvider clock) : this(db, clock, new ExpenseQueries(db), new ExpenseMapper()) { }

    // ---------- Expenses ----------

    public Task<IReadOnlyList<ExpenseDto>> ListAsync(Guid groupId, ExpenseFilter filter, CancellationToken ct) =>
        queries.ListExpensesAsync(groupId, filter, ct);

    public async Task<ExpenseDto> GetAsync(Guid groupId, Guid expenseId, CancellationToken ct)
    {
        var members = await queries.LoadMembersAsync(groupId, ct);
        var expense = await queries.LoadExpenseAsync(groupId, expenseId, ct);
        return mapper.ToDto(expense, members);
    }

    public async Task<ExpenseDto> CreateAsync(Guid groupId, ExpenseRequest req, GroupActor actor, CancellationToken ct)
    {
        var members = await queries.LoadMembersAsync(groupId, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var expense = new Expense
        {
            Id = Guid.NewGuid(), GroupId = groupId, CreatedAt = now, CreatedByMemberId = actor.MemberId,
        };
        mapper.Apply(expense, req, members);
        expense.Confirmations = expense.GenerateAutoConfirmations(actor.MemberId, now);
        db.Expenses.Add(expense);
        await db.SaveChangesAsync(ct);
        return mapper.ToDto(expense, members);
    }

    public async Task<ExpenseDto> UpdateAsync(Guid groupId, Guid expenseId, ExpenseRequest req, GroupActor actor, CancellationToken ct)
    {
        var members = await queries.LoadMembersAsync(groupId, ct);
        var expense = await queries.LoadExpenseAsync(groupId, expenseId, ct, tracking: true);
        if (!actor.CanModify(expense.CreatedByMemberId))
            throw new ForbiddenException("Only the person who added this expense, or the admin, can edit it.");

        var now = clock.GetUtcNow().UtcDateTime;
        var oldShares = expense.Shares.ToList();
        var oldConfirmations = expense.Confirmations.ToList();
        mapper.Apply(expense, req, members);
        expense.UpdatedByMemberId = actor.MemberId;
        expense.UpdatedAt = now;
        expense.Confirmations = expense.GenerateAutoConfirmations(actor.MemberId, now);
        db.ExpenseConfirmations.RemoveRange(oldConfirmations);
        db.ExpenseShares.RemoveRange(oldShares);
        db.ExpenseShares.AddRange(expense.Shares);
        db.ExpenseConfirmations.AddRange(expense.Confirmations);
        await db.SaveChangesAsync(ct);
        return mapper.ToDto(expense, members);
    }

    public async Task DeleteAsync(Guid groupId, Guid expenseId, GroupActor actor, CancellationToken ct)
    {
        await queries.EnsureGroupAsync(groupId, ct);
        var expense = await queries.LoadExpenseAsync(groupId, expenseId, ct, tracking: true);
        if (!actor.CanModify(expense.CreatedByMemberId))
            throw new ForbiddenException("Only the person who added this expense, or the admin, can delete it.");
        db.Expenses.Remove(expense);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ExpenseDto> ConfirmAsync(Guid groupId, Guid expenseId, ConfirmationRequest req, GroupActor actor, CancellationToken ct)
    {
        var members = await queries.LoadMembersAsync(groupId, ct);
        var expense = await queries.LoadExpenseAsync(groupId, expenseId, ct);

        if (actor.MemberId is not { } me || !expense.RequiredConfirmers().Contains(me))
            throw new ForbiddenException("Only the people this expense was split with can confirm or contest it.");

        var errors = new ValidationErrors();
        req.Validate(errors);
        errors.ThrowIfAny();

        var comment = string.IsNullOrWhiteSpace(req.Comment) ? null : req.Comment.Trim();
        var now = clock.GetUtcNow().UtcDateTime;
        var existing = await db.ExpenseConfirmations.FirstOrDefaultAsync(c => c.ExpenseId == expenseId && c.MemberId == me, ct);
        if (existing is null)
        {
            db.ExpenseConfirmations.Add(new ExpenseConfirmation
            {
                Id = Guid.NewGuid(), ExpenseId = expenseId, MemberId = me, Status = req.Status!.Value, Comment = comment, CreatedAt = now,
            });
        }
        else
        {
            existing.Status = req.Status!.Value;
            existing.Comment = comment;
            existing.CreatedAt = now;
        }
        await db.SaveChangesAsync(ct);

        var refreshed = await queries.LoadExpenseAsync(groupId, expenseId, ct);
        return mapper.ToDto(refreshed, members);
    }

    // ---------- Settlements ----------

    public Task<IReadOnlyList<SettlementDto>> ListSettlementsAsync(Guid groupId, CancellationToken ct) =>
        queries.ListSettlementsAsync(groupId, ct);

    public async Task<SettlementDto> CreateSettlementAsync(Guid groupId, CreateSettlementRequest req, GroupActor actor, CancellationToken ct)
    {
        var members = await queries.LoadMembersAsync(groupId, ct);
        var errors = new ValidationErrors();
        req.Validate(errors);
        if (req.FromMemberId is { } from && !members.ContainsKey(from)) errors.Add("fromMemberId", "Member does not belong to this group.");
        if (req.ToMemberId is { } to && !members.ContainsKey(to)) errors.Add("toMemberId", "Member does not belong to this group.");
        errors.ThrowIfAny();

        if (!actor.IsAdmin && actor.MemberId != req.FromMemberId && actor.MemberId != req.ToMemberId)
            throw new ForbiddenException("You can only register payments you made or received.");

        var now = clock.GetUtcNow().UtcDateTime;
        var confirmedNow = actor.MemberId == req.ToMemberId || (actor.IsAdmin && actor.MemberId != req.FromMemberId);
        var settlement = new Settlement
        {
            Id = Guid.NewGuid(), GroupId = groupId,
            FromMemberId = req.FromMemberId!.Value, ToMemberId = req.ToMemberId!.Value,
            Amount = req.Amount!.Value, Date = req.Date!.Value,
            PaymentMethod = req.PaymentMethod ?? PaymentMethod.Other,
            Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim(),
            CreatedAt = now, CreatedByMemberId = actor.MemberId,
            Status = confirmedNow ? SettlementStatus.Confirmed : SettlementStatus.Pending,
            ConfirmedAt = confirmedNow ? now : null,
        };
        db.Settlements.Add(settlement);
        await db.SaveChangesAsync(ct);
        return mapper.ToDto(settlement, members);
    }

    public async Task<SettlementDto> ConfirmSettlementAsync(Guid groupId, Guid settlementId, GroupActor actor, CancellationToken ct)
    {
        var members = await queries.LoadMembersAsync(groupId, ct);
        var settlement = await db.Settlements.FirstOrDefaultAsync(s => s.Id == settlementId && s.GroupId == groupId, ct)
                         ?? throw new NotFoundException($"Settlement '{settlementId}' not found in this group.");
        if (!actor.IsAdmin && actor.MemberId != settlement.ToMemberId)
            throw new ForbiddenException("Only the person who received the money can confirm it.");

        settlement.Confirm(clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return mapper.ToDto(settlement, members);
    }

    public async Task DeleteSettlementAsync(Guid groupId, Guid settlementId, GroupActor actor, CancellationToken ct)
    {
        await queries.EnsureGroupAsync(groupId, ct);
        var settlement = await db.Settlements.FirstOrDefaultAsync(s => s.Id == settlementId && s.GroupId == groupId, ct)
                         ?? throw new NotFoundException($"Settlement '{settlementId}' not found in this group.");
        var refusal = settlement.Status == SettlementStatus.Pending && actor.MemberId == settlement.ToMemberId;
        if (!actor.CanModify(settlement.CreatedByMemberId) && !refusal)
            throw new ForbiddenException("Only the person who registered this payment, or the admin, can remove it.");
        db.Settlements.Remove(settlement);
        await db.SaveChangesAsync(ct);
    }

    // ---------- Balances & stats ----------

    public Task<BalanceDto> GetBalancesAsync(Guid groupId, CancellationToken ct) => queries.GetBalancesAsync(groupId, ct);

    public Task<StatsDto> GetStatsAsync(Guid groupId, DateOnly? from, DateOnly? to, CancellationToken ct) =>
        queries.GetStatsAsync(groupId, from, to, ct);

    // ---------- Static helpers & backwards compatibility ----------

    public static HashSet<Guid> RequiredConfirmers(Expense expense) => expense.RequiredConfirmers();

    public static void Apply(Expense expense, ExpenseRequest req, IReadOnlyDictionary<Guid, Member> members) =>
        new ExpenseMapper().Apply(expense, req, members);
}
