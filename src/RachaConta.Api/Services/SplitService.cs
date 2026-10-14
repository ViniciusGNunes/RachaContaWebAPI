using Microsoft.EntityFrameworkCore;
using RachaConta.Api.Contracts;
using RachaConta.Api.Data;
using RachaConta.Api.Domain;

namespace RachaConta.Api.Services;

public sealed class SplitService(AppDbContext db, TimeProvider clock)
{
    public const decimal MaxTotal = 1_000_000m;

    public async Task<SplitAdminDto> CreateAsync(CreateSplitRequest req, CancellationToken ct)
    {
        var errors = new ValidationErrors();

        var title = req.Title?.Trim() ?? "";
        if (title.Length is < 1 or > 80) errors.Add("title", "Title must have 1 to 80 characters.");

        if (req.TotalAmount is not { } total)
        {
            errors.Add("totalAmount", "Total amount is required.");
            total = 0;
        }
        else if (total <= 0 || total > MaxTotal)
            errors.Add("totalAmount", "Total amount must be greater than 0 and at most 1,000,000.");
        else if (!ShareCalculator.HasAtMostTwoDecimals(total))
            errors.Add("totalAmount", "Total amount must have at most 2 decimal places.");

        var pixKey = "";
        if (req.PixKeyType is not { } pixKeyType)
        {
            errors.Add("pixKeyType", "Pix key type is required.");
            pixKeyType = default;
        }
        else if (!PixKeyNormalizer.TryNormalize(pixKeyType, req.PixKey, out pixKey, out var keyError))
            errors.Add("pixKey", keyError);

        var receiverName = req.ReceiverName?.Trim() ?? "";
        var sanitizedName = PixText.Sanitize(receiverName, int.MaxValue);
        if (sanitizedName.Length is < 1 or > PixPayload.MaxNameLength)
            errors.Add("receiverName", "Receiver name must have 1 to 25 characters (letters/digits, accents removed).");

        var receiverCity = PixText.Sanitize(req.ReceiverCity, int.MaxValue);
        if (receiverCity.Length is < 1 or > PixPayload.MaxCityLength)
            errors.Add("receiverCity", "Receiver city must have 1 to 15 characters (letters/digits, accents removed).");

        if (req.SplitMode is not { } splitMode)
        {
            errors.Add("splitMode", "Split mode is required.");
            splitMode = default;
        }

        var participants = req.Participants ?? [];
        if (participants.Count is < 2 or > 50)
            errors.Add("participants", "A split needs 2 to 50 participants.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < participants.Count; i++)
        {
            var p = participants[i];
            var name = p?.Name?.Trim() ?? "";
            if (name.Length is < 1 or > 60) errors.Add($"participants[{i}].name", "Name must have 1 to 60 characters.");
            else if (!seen.Add(name)) errors.Add($"participants[{i}].name", $"Duplicate participant name '{name}'.");

            if (splitMode == SplitMode.Custom && req.SplitMode is not null)
            {
                if (p?.Amount is not { } a) errors.Add($"participants[{i}].amount", "Amount is required in Custom mode.");
                else if (a <= 0) errors.Add($"participants[{i}].amount", "Amount must be greater than 0.");
                else if (!ShareCalculator.HasAtMostTwoDecimals(a)) errors.Add($"participants[{i}].amount", "Amount must have at most 2 decimal places.");
            }
        }

        var ownerCount = participants.Count(p => p?.IsOwner == true);
        if (ownerCount > 1) errors.Add("participants", "Only one participant can be the owner.");
        if (ownerCount > 0 && req.IncludeOwner == false)
            errors.Add("includeOwner", "includeOwner is false but a participant is marked as owner.");

        errors.ThrowIfAny();

        decimal[] amounts;
        if (splitMode == SplitMode.Equal)
            amounts = ShareCalculator.Equal(total, participants.Count);
        else
        {
            amounts = participants.Select(p => p!.Amount!.Value).ToArray();
            if (!ShareCalculator.CustomSumMatches(total, amounts))
                throw new ValidationException("participants",
                    $"Participant amounts sum to {amounts.Sum():0.00} but the total is {total:0.00}.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var split = new Split
        {
            Id = Guid.NewGuid(),
            Slug = await NewUniqueSlugAsync(ct),
            AdminToken = Tokens.NewAdminToken(),
            Title = title,
            TotalAmount = total,
            PixKey = pixKey,
            PixKeyType = pixKeyType,
            ReceiverName = receiverName,
            ReceiverCity = receiverCity,
            SplitMode = splitMode,
            CreatedAt = now,
            Participants = participants.Select((p, i) => new SplitParticipant
            {
                Id = Guid.NewGuid(),
                Position = i,
                Name = p!.Name!.Trim(),
                Amount = amounts[i],
                IsOwner = p.IsOwner == true,
                Paid = p.IsOwner == true,
                PaidAt = p.IsOwner == true ? now : null,
            }).ToList(),
        };

        db.Splits.Add(split);
        await db.SaveChangesAsync(ct);
        return ToAdminDto(split, includeToken: true);
    }

    public async Task<SplitPublicDto> GetPublicAsync(string slug, CancellationToken ct) =>
        ToPublicDto(await LoadAsync(slug, ct));

    public async Task<SplitPixDto> GetPixAsync(string slug, Guid participantId, CancellationToken ct)
    {
        var split = await LoadAsync(slug, ct);
        var p = FindParticipant(split, participantId);
        if (p.IsOwner)
            throw new NotFoundException("The owner receives the payment and has no Pix code.");

        var code = PixPayload.Build(split.PixKey, split.ReceiverName, split.ReceiverCity, p.Amount);
        return new SplitPixDto(p.Id, p.Name, p.Amount, split.PixKey,
            PixText.Sanitize(split.ReceiverName, PixPayload.MaxNameLength), code);
    }

    public async Task<SplitPublicDto> MarkPaidAsync(string slug, Guid participantId, CancellationToken ct)
    {
        var split = await LoadAsync(slug, ct);
        var p = FindParticipant(split, participantId);
        if (!p.Paid && !p.PaymentPending)
        {
            p.PaymentPending = true;
            p.ClaimedAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
        }
        return ToPublicDto(split);
    }

    public async Task<SplitAdminDto> GetAdminAsync(string slug, string? token, CancellationToken ct) =>
        ToAdminDto(await LoadAdminAsync(slug, token, ct), includeToken: false);

    public async Task<SplitAdminDto> TogglePaidAsync(string slug, Guid participantId, string? token, CancellationToken ct)
    {
        var split = await LoadAdminAsync(slug, token, ct);
        var p = FindParticipant(split, participantId);
        if (p.IsOwner)
            throw new ValidationException("participantId", "The owner is always marked as paid.");

        p.Paid = !p.Paid;
        p.PaidAt = p.Paid ? clock.GetUtcNow().UtcDateTime : null;
        p.PaymentPending = false;
        p.ClaimedAt = null;
        await db.SaveChangesAsync(ct);
        return ToAdminDto(split, includeToken: false);
    }

    public async Task<SplitAdminDto> RejectClaimAsync(string slug, Guid participantId, string? token, CancellationToken ct)
    {
        var split = await LoadAdminAsync(slug, token, ct);
        var p = FindParticipant(split, participantId);
        p.PaymentPending = false;
        p.ClaimedAt = null;
        await db.SaveChangesAsync(ct);
        return ToAdminDto(split, includeToken: false);
    }

    public async Task DeleteAsync(string slug, string? token, CancellationToken ct)
    {
        var split = await LoadAdminAsync(slug, token, ct);
        db.Splits.Remove(split);
        await db.SaveChangesAsync(ct);
    }

    private async Task<Split> LoadAsync(string slug, CancellationToken ct)
    {
        var split = await db.Splits.Include(s => s.Participants).FirstOrDefaultAsync(s => s.Slug == slug, ct)
                    ?? throw new NotFoundException($"Split '{slug}' not found.");
        split.Participants = split.Participants.OrderBy(p => p.Position).ToList();
        return split;
    }

    private async Task<Split> LoadAdminAsync(string slug, string? token, CancellationToken ct)
    {
        var split = await LoadAsync(slug, ct);
        if (!Tokens.FixedTimeEquals(token, split.AdminToken))
            throw new ForbiddenException("Invalid admin token.");
        return split;
    }

    private static SplitParticipant FindParticipant(Split split, Guid participantId) =>
        split.Participants.FirstOrDefault(p => p.Id == participantId)
        ?? throw new NotFoundException($"Participant '{participantId}' not found in this split.");

    private async Task<string> NewUniqueSlugAsync(CancellationToken ct)
    {
        while (true)
        {
            var slug = Tokens.NewSlug();
            if (!await db.Splits.AnyAsync(s => s.Slug == slug, ct)) return slug;
        }
    }

    private static SplitPublicDto ToPublicDto(Split s) => new(
        s.Slug, s.Title, s.TotalAmount, s.ReceiverName, s.CreatedAt,
        s.Participants.Select(p => new SplitParticipantDto(p.Id, p.Name, p.Amount, p.IsOwner, p.Paid, p.PaymentPending)).ToList(),
        s.Participants.Count(p => p.Paid),
        s.Participants.Where(p => p.Paid).Sum(p => p.Amount),
        s.Participants.Count(p => p.PaymentPending));

    private static SplitAdminDto ToAdminDto(Split s, bool includeToken) => new(
        s.Slug, s.Title, s.TotalAmount, s.ReceiverName, s.CreatedAt,
        s.Participants.Select(p => new SplitAdminParticipantDto(
            p.Id, p.Name, p.Amount, p.IsOwner, p.Paid, p.PaidAt, p.PaymentPending, p.ClaimedAt)).ToList(),
        s.Participants.Count(p => p.Paid),
        s.Participants.Where(p => p.Paid).Sum(p => p.Amount),
        s.Participants.Count(p => p.PaymentPending),
        s.PixKey, s.PixKeyType, s.ReceiverCity, s.SplitMode,
        includeToken ? s.AdminToken : null);
}
