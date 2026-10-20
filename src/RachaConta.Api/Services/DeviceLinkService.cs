using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RachaConta.Api.Contracts;
using RachaConta.Api.Controllers;
using RachaConta.Api.Data;
using RachaConta.Api.Domain;

namespace RachaConta.Api.Services;

/// <summary>Lets a member use the same identity on several devices, each with its own revocable token.</summary>
public sealed class DeviceLinkService(AppDbContext db, TimeProvider clock)
{
    public const int CodeLength = 8;
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    private const int MaxLabelLength = 80;

    // No 0/O/1/I so the code survives being read aloud or typed by hand.
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public async Task<LinkCodeDto> CreateCodeAsync(GroupActor actor, CancellationToken ct)
    {
        var memberId = RequireMember(actor);
        var now = clock.GetUtcNow().UtcDateTime;

        // A new code replaces any pending one.
        await db.MemberLinkCodes.Where(c => c.MemberId == memberId && (c.UsedAt != null || c.ExpiresAt <= now))
            .ExecuteDeleteAsync(ct);
        await db.MemberLinkCodes.Where(c => c.MemberId == memberId && c.UsedAt == null).ExecuteDeleteAsync(ct);

        var code = RandomNumberGenerator.GetString(CodeAlphabet, CodeLength);
        var expiresAt = now + CodeLifetime;
        db.MemberLinkCodes.Add(new MemberLinkCode
        {
            Id = Guid.NewGuid(), MemberId = memberId, CodeHash = Tokens.Hash(code), ExpiresAt = expiresAt,
        });
        await db.SaveChangesAsync(ct);
        return new LinkCodeDto(code, expiresAt);
    }

    public async Task<ClaimResultDto> RedeemAsync(Guid groupId, RedeemLinkCodeRequest req, CancellationToken ct)
    {
        var code = NormalizeCode(req.Code);
        if (code.Length != CodeLength) throw InvalidCode();

        var now = clock.GetUtcNow().UtcDateTime;
        var hash = Tokens.Hash(code);
        var found = await db.MemberLinkCodes
            .Where(c => c.CodeHash == hash && c.UsedAt == null && c.ExpiresAt > now)
            .Join(db.Members.Where(m => m.GroupId == groupId), c => c.MemberId, m => m.Id, (c, m) => new { c.Id, Member = m })
            .FirstOrDefaultAsync(ct);
        if (found is null) throw InvalidCode();

        // Single use: of two simultaneous redeems only one flips UsedAt.
        var rows = await db.MemberLinkCodes
            .Where(c => c.Id == found.Id && c.UsedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.UsedAt, now), ct);
        if (rows == 0) throw InvalidCode();

        var member = found.Member;
        var token = Tokens.NewAdminToken();
        db.MemberDevices.Add(new MemberDevice
        {
            Id = Guid.NewGuid(), MemberId = member.Id, TokenHash = Tokens.Hash(token),
            Label = CleanLabel(req.Label), CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        return new ClaimResultDto(token, GroupService.ToDto(member));
    }

    public async Task<IReadOnlyList<DeviceDto>> ListAsync(GroupActor actor, CancellationToken ct)
    {
        var memberId = RequireMember(actor);
        var list = await db.MemberDevices.AsNoTracking()
            .Where(d => d.MemberId == memberId)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(ct);
        return list.Select(d => new DeviceDto(d.Id, d.Label, d.CreatedAt, d.LastUsedAt, d.Id == actor.DeviceId)).ToList();
    }

    public async Task RevokeAsync(GroupActor actor, Guid deviceId, CancellationToken ct)
    {
        var memberId = RequireMember(actor);
        var devices = await db.MemberDevices.Where(d => d.MemberId == memberId).Select(d => d.Id).ToListAsync(ct);
        if (!devices.Contains(deviceId)) throw new NotFoundException($"Device '{deviceId}' not found.");
        if (devices.Count == 1) throw new ConflictException("You cannot disconnect your only device.");

        await db.MemberDevices.Where(d => d.Id == deviceId).ExecuteDeleteAsync(ct);
    }

    public static string? CleanLabel(string? label)
    {
        var trimmed = label?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > MaxLabelLength ? trimmed[..MaxLabelLength] : trimmed;
    }

    private static Guid RequireMember(GroupActor actor) =>
        actor.MemberId ?? throw new ForbiddenException("Pick who you are in this group first.");

    private static string NormalizeCode(string? raw) =>
        new((raw ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    // One generic answer for wrong, expired and used codes, so nothing can be probed.
    private static NotFoundException InvalidCode() => new("Invalid or expired code.");
}
