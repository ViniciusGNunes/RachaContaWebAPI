using RachaPix.Api.Contracts;
using RachaPix.Api.Domain;

namespace RachaPix.Api.Services;

/// <summary>DTO and entity mapping for expenses and settlements (Module 2).</summary>
public sealed class ExpenseMapper
{
    private static string NameOf(IReadOnlyDictionary<Guid, Member> members, Guid id) =>
        members.TryGetValue(id, out var m) ? m.Name : "";

    public ExpenseDto ToDto(Expense e, IReadOnlyDictionary<Guid, Member> members) => new(
        e.Id, e.GroupId, e.Description, e.Amount, e.Date, e.Category, e.PaymentMethod,
        e.PaidByMemberId, NameOf(members, e.PaidByMemberId), e.ShareMode,
        e.Shares.OrderBy(s => s.Position)
            .Select(s => new ExpenseShareDto(s.MemberId, NameOf(members, s.MemberId), s.Amount, s.Percentage)).ToList(),
        e.Notes, e.CreatedAt,
        e.CreatedByMemberId, e.CreatedByMemberId is { } cb ? NameOf(members, cb) : null,
        e.UpdatedByMemberId is { } ub ? NameOf(members, ub) : null, e.UpdatedAt,
        e.Confirmations.OrderBy(c => c.CreatedAt)
            .Select(c => new ExpenseConfirmationDto(c.MemberId, NameOf(members, c.MemberId), c.Status, c.Comment, c.CreatedAt)).ToList());

    public SettlementDto ToDto(Settlement s, IReadOnlyDictionary<Guid, Member> members) => new(
        s.Id, s.GroupId, s.FromMemberId, NameOf(members, s.FromMemberId), s.ToMemberId, NameOf(members, s.ToMemberId),
        s.Amount, s.Date, s.PaymentMethod, s.Notes, s.CreatedAt,
        s.CreatedByMemberId, s.CreatedByMemberId is { } cb ? NameOf(members, cb) : null, s.Status, s.ConfirmedAt);

    public void Apply(Expense expense, ExpenseRequest req, IReadOnlyDictionary<Guid, Member> members)
    {
        var errors = new ValidationErrors();
        req.Validate(errors);
        if (req.PaidByMemberId is { } paidBy && !members.ContainsKey(paidBy))
            errors.Add("paidByMemberId", "Paying member does not belong to this group.");

        var shares = req.Shares ?? [];
        for (var i = 0; i < shares.Count; i++)
        {
            var s = shares[i];
            if (s?.MemberId is { } memberId && !members.ContainsKey(memberId))
                errors.Add($"shares[{i}].memberId", "Member does not belong to this group.");
        }
        errors.ThrowIfAny();

        var amount = req.Amount!.Value;
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
                break;
            default:
                var pcts = shares.Select(s => s!.Percentage!.Value).ToArray();
                amounts = ShareCalculator.Percentage(amount, pcts);
                percentages = pcts.Select(p => (decimal?)p).ToArray();
                break;
        }

        expense.Description = req.Description!.Trim();
        expense.Amount = amount;
        expense.Date = req.Date!.Value;
        expense.Category = req.Category ?? ExpenseCategory.Uncategorized;
        expense.PaymentMethod = req.PaymentMethod ?? PaymentMethod.Other;
        expense.PaidByMemberId = req.PaidByMemberId!.Value;
        expense.ShareMode = mode;
        expense.Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim();
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
}
