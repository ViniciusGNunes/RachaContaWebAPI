using System.ComponentModel.DataAnnotations;
using RachaConta.Api.Domain;
using RachaConta.Api.Services;

namespace RachaConta.Api.Contracts;

// ---------- Requests ----------

public sealed record CreateGroupRequest(
    [StringLength(80, MinimumLength = 1, ErrorMessage = "Name must have 1 to 80 characters.")]
    string? Name,
    [StringLength(500, ErrorMessage = "Description must have at most 500 characters.")]
    string? Description,
    List<MemberRequest?>? Members) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var errors = new ValidationErrors();
        Validate(errors);
        return errors.ToValidationResults();
    }

    public void Validate(ValidationErrors errors)
    {
        var name = Name?.Trim() ?? "";
        if (name.Length is < 1 or > 80) errors.Add("name", "Name must have 1 to 80 characters.");

        var description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
        if (description?.Length > 500) errors.Add("description", "Description must have at most 500 characters.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var members = Members ?? [];
        for (var i = 0; i < members.Count; i++)
        {
            var m = members[i];
            if (m is null)
            {
                errors.Add($"members[{i}]", "Member is required.");
                continue;
            }
            m.Validate(errors, $"members[{i}].");
            var mName = m.Name?.Trim() ?? "";
            if (mName.Length >= 1 && mName.Length <= 60 && !seen.Add(mName))
                errors.Add($"members[{i}].name", $"Duplicate member name '{mName}'.");
        }
    }
}

public sealed record UpdateGroupRequest(
    [StringLength(80, MinimumLength = 1, ErrorMessage = "Name must have 1 to 80 characters.")]
    string? Name,
    [StringLength(500, ErrorMessage = "Description must have at most 500 characters.")]
    string? Description) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var errors = new ValidationErrors();
        Validate(errors);
        return errors.ToValidationResults();
    }

    public void Validate(ValidationErrors errors)
    {
        var name = Name?.Trim() ?? "";
        if (name.Length is < 1 or > 80) errors.Add("name", "Name must have 1 to 80 characters.");

        var description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
        if (description?.Length > 500) errors.Add("description", "Description must have at most 500 characters.");
    }
}

public sealed record MemberRequest(
    [StringLength(60, MinimumLength = 1, ErrorMessage = "Name must have 1 to 60 characters.")]
    string? Name,
    string? PixKey,
    PixKeyType? PixKeyType) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var errors = new ValidationErrors();
        Validate(errors);
        return errors.ToValidationResults();
    }

    public void Validate(ValidationErrors errors, string prefix = "")
    {
        var name = Name?.Trim() ?? "";
        if (name.Length is < 1 or > 60)
            errors.Add(prefix + "name", "Name must have 1 to 60 characters.");

        if (!string.IsNullOrWhiteSpace(PixKey))
        {
            if (PixKeyType is not { } type)
            {
                errors.Add(prefix + "pixKeyType", "Pix key type is required when a Pix key is given.");
            }
            else if (!PixKeyNormalizer.TryNormalize(type, PixKey, out _, out var error))
            {
                errors.Add(prefix + "pixKey", error);
            }
        }
    }
}

public sealed record ExpenseRequest(
    string? Description,
    decimal? Amount,
    DateOnly? Date,
    ExpenseCategory? Category,
    PaymentMethod? PaymentMethod,
    Guid? PaidByMemberId,
    ShareMode? ShareMode,
    List<ExpenseShareRequest?>? Shares,
    string? Notes) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var errors = new ValidationErrors();
        Validate(errors);
        return errors.ToValidationResults();
    }

    public void Validate(ValidationErrors errors)
    {
        var description = Description?.Trim() ?? "";
        if (description.Length is < 1 or > 120) errors.Add("description", "Description must have 1 to 120 characters.");

        var amount = Amount ?? 0;
        if (Amount is null) errors.Add("amount", "Amount is required.");
        else if (amount <= 0 || amount > SplitService.MaxTotal) errors.Add("amount", "Amount must be greater than 0 and at most 1,000,000.");
        else if (!ShareCalculator.HasAtMostTwoDecimals(amount)) errors.Add("amount", "Amount must have at most 2 decimal places.");

        if (Date is null) errors.Add("date", "Date is required (yyyy-MM-dd).");
        if (PaidByMemberId is null) errors.Add("paidByMemberId", "Paying member is required.");

        var notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim();
        if (notes?.Length > 500) errors.Add("notes", "Notes must have at most 500 characters.");

        if (ShareMode is null) errors.Add("shareMode", "Share mode is required.");
        var shares = Shares ?? [];
        if (shares.Count == 0) errors.Add("shares", "At least one member must share the expense.");

        var seen = new HashSet<Guid>();
        for (var i = 0; i < shares.Count; i++)
        {
            var s = shares[i];
            if (s?.MemberId is not { } memberId) { errors.Add($"shares[{i}].memberId", "Member is required."); continue; }
            if (!seen.Add(memberId)) errors.Add($"shares[{i}].memberId", "Member is listed more than once.");

            switch (ShareMode)
            {
                case Domain.ShareMode.Custom:
                    if (s.Amount is not { } a) errors.Add($"shares[{i}].amount", "Amount is required in Custom mode.");
                    else if (a <= 0) errors.Add($"shares[{i}].amount", "Amount must be greater than 0.");
                    else if (!ShareCalculator.HasAtMostTwoDecimals(a)) errors.Add($"shares[{i}].amount", "Amount must have at most 2 decimal places.");
                    break;
                case Domain.ShareMode.Percentage:
                    if (s.Percentage is not { } p) errors.Add($"shares[{i}].percentage", "Percentage is required in Percentage mode.");
                    else if (p <= 0 || p > 100) errors.Add($"shares[{i}].percentage", "Percentage must be greater than 0 and at most 100.");
                    else if (decimal.Round(p, 4) != p) errors.Add($"shares[{i}].percentage", "Percentage must have at most 4 decimal places.");
                    break;
            }
        }

        if (!errors.Any && Amount is { } validAmount && shares.Count > 0)
        {
            switch (ShareMode)
            {
                case Domain.ShareMode.Custom:
                    var amounts = shares.Select(s => s!.Amount!.Value).ToArray();
                    if (!ShareCalculator.CustomSumMatches(validAmount, amounts))
                        errors.Add("shares", $"Share amounts sum to {amounts.Sum():0.00} but the expense amount is {validAmount:0.00}.");
                    break;
                case Domain.ShareMode.Percentage:
                    var pcts = shares.Select(s => s!.Percentage!.Value).ToArray();
                    if (!ShareCalculator.PercentagesSumTo100(pcts))
                        errors.Add("shares", $"Percentages sum to {pcts.Sum():0.####} but must sum to 100.");
                    break;
            }
        }
    }
}

public sealed record ExpenseShareRequest(Guid? MemberId, decimal? Amount, decimal? Percentage);

public sealed record CreateSettlementRequest(
    Guid? FromMemberId,
    Guid? ToMemberId,
    decimal? Amount,
    DateOnly? Date,
    PaymentMethod? PaymentMethod = null,
    string? Notes = null) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var errors = new ValidationErrors();
        Validate(errors);
        return errors.ToValidationResults();
    }

    public void Validate(ValidationErrors errors)
    {
        if (FromMemberId is null) errors.Add("fromMemberId", "Paying member is required.");
        if (ToMemberId is null) errors.Add("toMemberId", "Receiving member is required.");
        if (FromMemberId is not null && FromMemberId == ToMemberId)
            errors.Add("toMemberId", "A member cannot pay themselves.");

        if (Amount is not { } amount) errors.Add("amount", "Amount is required.");
        else if (amount <= 0 || amount > SplitService.MaxTotal) errors.Add("amount", "Amount must be greater than 0 and at most 1,000,000.");
        else if (!ShareCalculator.HasAtMostTwoDecimals(amount)) errors.Add("amount", "Amount must have at most 2 decimal places.");

        if (Date is null) errors.Add("date", "Date is required (yyyy-MM-dd).");
        var notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim();
        if (notes?.Length > 500) errors.Add("notes", "Notes must have at most 500 characters.");
    }
}

/// <summary>Pick who you are: an existing unclaimed member, or a new one by name.</summary>
public sealed record ClaimRequest(Guid? MemberId, string? NewName, string? Label = null) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var errors = new ValidationErrors();
        Validate(errors);
        return errors.ToValidationResults();
    }

    public void Validate(ValidationErrors errors)
    {
        if (MemberId is null)
        {
            var name = NewName?.Trim() ?? "";
            if (name.Length is < 1 or > 60)
                errors.Add("newName", "Name must have 1 to 60 characters.");
        }
    }
}

/// <summary>Redeems a link code on a new device; the label is a free-text device name.</summary>
public sealed record RedeemLinkCodeRequest(string? Code, string? Label = null);

public sealed record ConfirmationRequest(
    ConfirmationStatus? Status,
    [StringLength(200, ErrorMessage = "Comment must have at most 200 characters.")]
    string? Comment) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var errors = new ValidationErrors();
        Validate(errors);
        return errors.ToValidationResults();
    }

    public void Validate(ValidationErrors errors)
    {
        if (Status is null)
            errors.Add("status", "Status is required (Confirmed or Contested).");

        var comment = string.IsNullOrWhiteSpace(Comment) ? null : Comment.Trim();
        if (comment?.Length > 200)
            errors.Add("comment", "Comment must have at most 200 characters.");
    }
}

public sealed record ExpenseFilter(DateOnly? From, DateOnly? To, ExpenseCategory? Category, Guid? MemberId) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var errors = new ValidationErrors();
        Validate(errors);
        return errors.ToValidationResults();
    }

    public void Validate(ValidationErrors errors)
    {
        if (From is { } f && To is { } t && f > t)
            errors.Add("from", "'from' must be on or before 'to'.");
    }
}

// ---------- Responses ----------

public sealed record GroupDto(
    Guid Id,
    string Name,
    string? Description,
    string Currency,
    DateTime CreatedAt,
    IReadOnlyList<MemberDto> Members,
    string? AccessToken = null,
    string? AdminToken = null,
    Guid? YourMemberId = null,
    bool IsAdmin = false);

/// <summary>Returned once when someone claims a member: the personal token is never shown again.</summary>
public sealed record ClaimResultDto(string MemberToken, MemberDto Member);

public sealed record InviteDto(string AccessToken);

/// <summary>Single-use code to connect another device to the caller's member.</summary>
public sealed record LinkCodeDto(string Code, DateTime ExpiresAt);

public sealed record DeviceDto(Guid Id, string? Label, DateTime CreatedAt, DateTime? LastUsedAt, bool IsCurrent);

public sealed record GroupLookupItem(Guid? Id, string? Token);

public sealed record MyGroupDto(
    Guid Id,
    string Name,
    string? Description,
    string Currency,
    int MemberCount,
    decimal TotalSpent,
    DateTime CreatedAt,
    bool IsAdmin);

public sealed record MemberDto(Guid Id, string Name, string? PixKey, PixKeyType? PixKeyType, bool Claimed = false, bool IsAdmin = false);

public sealed record ExpenseConfirmationDto(Guid MemberId, string MemberName, ConfirmationStatus Status, string? Comment, DateTime CreatedAt);

public sealed record ExpenseShareDto(Guid MemberId, string MemberName, decimal Amount, decimal? Percentage);

public sealed record ExpenseDto(
    Guid Id,
    Guid GroupId,
    string Description,
    decimal Amount,
    DateOnly Date,
    ExpenseCategory Category,
    PaymentMethod PaymentMethod,
    Guid PaidByMemberId,
    string PaidByName,
    ShareMode ShareMode,
    IReadOnlyList<ExpenseShareDto> Shares,
    string? Notes,
    DateTime CreatedAt,
    Guid? CreatedByMemberId,
    string? CreatedByName,
    string? UpdatedByName,
    DateTime? UpdatedAt,
    IReadOnlyList<ExpenseConfirmationDto> Confirmations);

public sealed record SettlementDto(
    Guid Id,
    Guid GroupId,
    Guid FromMemberId,
    string FromName,
    Guid ToMemberId,
    string ToName,
    decimal Amount,
    DateOnly Date,
    PaymentMethod PaymentMethod,
    string? Notes,
    DateTime CreatedAt,
    Guid? CreatedByMemberId,
    string? CreatedByName,
    SettlementStatus Status,
    DateTime? ConfirmedAt);

public sealed record MemberBalanceDto(Guid MemberId, string Name, decimal Paid, decimal Owed, decimal SettlementsSent, decimal SettlementsReceived, decimal Net);

public sealed record TransferDto(Guid FromMemberId, string FromName, Guid ToMemberId, string ToName, decimal Amount);

public sealed record BalanceDto(Guid GroupId, IReadOnlyList<MemberBalanceDto> Members, IReadOnlyList<TransferDto> SuggestedTransfers);

public sealed record CategoryTotalDto(ExpenseCategory Category, decimal Total);
public sealed record MemberStatsDto(Guid MemberId, string Name, decimal Paid, decimal Owed);
public sealed record MonthTotalDto(string Month, decimal Total);

public sealed record StatsDto(
    Guid GroupId,
    decimal TotalSpent,
    IReadOnlyList<CategoryTotalDto> ByCategory,
    IReadOnlyList<MemberStatsDto> ByMember,
    IReadOnlyList<MonthTotalDto> ByMonth);

public sealed record MemberPixDto(string PixCode, decimal Amount, string Name, string PixKey);
