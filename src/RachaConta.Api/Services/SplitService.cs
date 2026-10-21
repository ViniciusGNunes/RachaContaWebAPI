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
        req.Validate(errors);
        errors.ThrowIfAny();

        var total = req.TotalAmount!.Value;
        var splitMode = req.SplitMode!.Value;
        var participants = req.Participants ?? [];
        decimal[] amounts = splitMode == SplitMode.Equal
            ? ShareCalculator.Equal(total, participants.Count)
            : participants.Select(p => p!.Amount!.Value).ToArray();

        PixKeyNormalizer.TryNormalize(req.PixKeyType!.Value, req.PixKey, out var pixKey, out _);
        var now = clock.GetUtcNow().UtcDateTime;
        var split = new Split
        {
            Id = Guid.NewGuid(),
            Slug = await NewUniqueSlugAsync(ct),
            AdminToken = Tokens.NewAdminToken(),
            Title = req.Title!.Trim(),
            TotalAmount = total,
            PixKey = pixKey,
            PixKeyType = req.PixKeyType!.Value,
            ReceiverName = req.ReceiverName!.Trim(),
            ReceiverCity = req.ReceiverCity!.Trim(),
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
        var p = split.FindParticipant(participantId);
        if (p.IsOwner)
            throw new NotFoundException("The owner receives the payment and has no Pix code.");

        var code = PixPayload.Build(split.PixKey, split.ReceiverName, split.ReceiverCity, p.Amount);
        return new SplitPixDto(p.Id, p.Name, p.Amount, split.PixKey,
            PixText.Sanitize(split.ReceiverName, PixPayload.MaxNameLength), code);
    }

    public async Task<SplitPublicDto> MarkPaidAsync(string slug, Guid participantId, CancellationToken ct)
    {
        var split = await LoadAsync(slug, ct);
        var p = split.FindParticipant(participantId);
        p.MarkPendingPayment(clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return ToPublicDto(split);
    }

    public async Task<SplitAdminDto> GetAdminAsync(string slug, string? token, CancellationToken ct) =>
        ToAdminDto(await LoadAdminAsync(slug, token, ct), includeToken: false);

    public async Task<SplitAdminDto> TogglePaidAsync(string slug, Guid participantId, string? token, CancellationToken ct)
    {
        var split = await LoadAdminAsync(slug, token, ct);
        var p = split.FindParticipant(participantId);
        p.TogglePaid(clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return ToAdminDto(split, includeToken: false);
    }

    public async Task<SplitAdminDto> RejectClaimAsync(string slug, Guid participantId, string? token, CancellationToken ct)
    {
        var split = await LoadAdminAsync(slug, token, ct);
        var p = split.FindParticipant(participantId);
        p.RejectClaim();
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
        s.PaidCount, s.PaidAmount, s.PendingCount);

    private static SplitAdminDto ToAdminDto(Split s, bool includeToken) => new(
        s.Slug, s.Title, s.TotalAmount, s.ReceiverName, s.CreatedAt,
        s.Participants.Select(p => new SplitAdminParticipantDto(
            p.Id, p.Name, p.Amount, p.IsOwner, p.Paid, p.PaidAt, p.PaymentPending, p.ClaimedAt)).ToList(),
        s.PaidCount, s.PaidAmount, s.PendingCount,
        s.PixKey, s.PixKeyType, s.ReceiverCity, s.SplitMode,
        includeToken ? s.AdminToken : null);
}
