using System.ComponentModel.DataAnnotations;
using RachaConta.Api.Domain;
using RachaConta.Api.Services;

namespace RachaConta.Api.Contracts;

// Requests use nullable members so missing fields produce field-level 400s instead of defaults.

public sealed record CreateSplitRequest(
    string? Title,
    decimal? TotalAmount,
    string? PixKey,
    PixKeyType? PixKeyType,
    string? ReceiverName,
    string? ReceiverCity,
    SplitMode? SplitMode,
    bool? IncludeOwner,
    List<CreateSplitParticipantRequest?>? Participants) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var errors = new ValidationErrors();
        Validate(errors);
        return errors.ToValidationResults();
    }

    public void Validate(ValidationErrors errors)
    {
        var title = Title?.Trim() ?? "";
        if (title.Length is < 1 or > 80) errors.Add("title", "Title must have 1 to 80 characters.");

        if (TotalAmount is not { } total)
        {
            errors.Add("totalAmount", "Total amount is required.");
        }
        else if (total <= 0 || total > SplitService.MaxTotal)
            errors.Add("totalAmount", "Total amount must be greater than 0 and at most 1,000,000.");
        else if (!ShareCalculator.HasAtMostTwoDecimals(total))
            errors.Add("totalAmount", "Total amount must have at most 2 decimal places.");

        if (PixKeyType is not { } pixKeyType)
        {
            errors.Add("pixKeyType", "Pix key type is required.");
        }
        else if (!PixKeyNormalizer.TryNormalize(pixKeyType, PixKey, out _, out var keyError))
            errors.Add("pixKey", keyError);

        var receiverName = ReceiverName?.Trim() ?? "";
        var sanitizedName = PixText.Sanitize(receiverName, int.MaxValue);
        if (sanitizedName.Length is < 1 or > PixPayload.MaxNameLength)
            errors.Add("receiverName", "Receiver name must have 1 to 25 characters (letters/digits, accents removed).");

        var receiverCity = PixText.Sanitize(ReceiverCity, int.MaxValue);
        if (receiverCity.Length is < 1 or > PixPayload.MaxCityLength)
            errors.Add("receiverCity", "Receiver city must have 1 to 15 characters (letters/digits, accents removed).");

        if (SplitMode is null)
        {
            errors.Add("splitMode", "Split mode is required.");
        }

        var participants = Participants ?? [];
        if (participants.Count is < 2 or > 50)
            errors.Add("participants", "A split needs 2 to 50 participants.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < participants.Count; i++)
        {
            var p = participants[i];
            var name = p?.Name?.Trim() ?? "";
            if (name.Length is < 1 or > 60) errors.Add($"participants[{i}].name", "Name must have 1 to 60 characters.");
            else if (!seen.Add(name)) errors.Add($"participants[{i}].name", $"Duplicate participant name '{name}'.");

            if (SplitMode == Domain.SplitMode.Custom)
            {
                if (p?.Amount is not { } a) errors.Add($"participants[{i}].amount", "Amount is required in Custom mode.");
                else if (a <= 0) errors.Add($"participants[{i}].amount", "Amount must be greater than 0.");
                else if (!ShareCalculator.HasAtMostTwoDecimals(a)) errors.Add($"participants[{i}].amount", "Amount must have at most 2 decimal places.");
            }
        }

        var ownerCount = participants.Count(p => p?.IsOwner == true);
        if (ownerCount > 1) errors.Add("participants", "Only one participant can be the owner.");
        if (ownerCount > 0 && IncludeOwner == false)
            errors.Add("includeOwner", "includeOwner is false but a participant is marked as owner.");

        if (SplitMode == Domain.SplitMode.Custom && TotalAmount is { } validTotal && !errors.Any)
        {
            var amounts = participants.Select(p => p!.Amount!.Value).ToArray();
            if (!ShareCalculator.CustomSumMatches(validTotal, amounts))
                errors.Add("participants",
                    $"Participant amounts sum to {amounts.Sum():0.00} but the total is {validTotal:0.00}.");
        }
    }
}

public sealed record CreateSplitParticipantRequest(
    [StringLength(60, MinimumLength = 1, ErrorMessage = "Name must have 1 to 60 characters.")]
    string? Name,
    decimal? Amount,
    bool? IsOwner);

public sealed record SplitParticipantDto(Guid Id, string Name, decimal Amount, bool IsOwner, bool Paid, bool PaymentPending);

public sealed record SplitPublicDto(
    string Slug,
    string Title,
    decimal TotalAmount,
    string ReceiverName,
    DateTime CreatedAt,
    IReadOnlyList<SplitParticipantDto> Participants,
    int PaidCount,
    decimal PaidAmount,
    int PendingCount);

public sealed record SplitAdminParticipantDto(
    Guid Id, string Name, decimal Amount, bool IsOwner, bool Paid, DateTime? PaidAt, bool PaymentPending, DateTime? ClaimedAt);

public sealed record SplitAdminDto(
    string Slug,
    string Title,
    decimal TotalAmount,
    string ReceiverName,
    DateTime CreatedAt,
    IReadOnlyList<SplitAdminParticipantDto> Participants,
    int PaidCount,
    decimal PaidAmount,
    int PendingCount,
    string PixKey,
    PixKeyType PixKeyType,
    string ReceiverCity,
    SplitMode SplitMode,
    string? AdminToken);

public sealed record SplitPixDto(Guid ParticipantId, string Name, decimal Amount, string PixKey, string ReceiverName, string PixCode);
