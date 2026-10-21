using RachaPix.Api.Contracts;
using RachaPix.Api.Controllers;
using RachaPix.Api.Domain;

namespace RachaPix.Api.Services;

/// <summary>DTO mapping for groups and members (Module 2).</summary>
public sealed class GroupMapper
{
    public GroupDto ToDto(Group g, GroupActor? actor = null) => new(
        g.Id,
        g.Name,
        g.Description,
        g.Currency,
        g.CreatedAt,
        g.Members.OrderBy(m => m.CreatedAt).Select(m => ToDto(m, hidePix: actor?.IsInvite == true)).ToList());

    /// <summary>Pix keys are private to identified members: the bare invite link cannot see them.</summary>
    public MemberDto ToDto(Member m, bool hidePix = false) =>
        new(m.Id, m.Name, hidePix ? null : m.PixKey, hidePix ? null : m.PixKeyType, m.ClaimedAt is not null, m.IsAdmin);

    public Member ToEntity(MemberRequest req, Guid groupId, DateTime createdAt)
    {
        string? pixKey = null;
        PixKeyType? pixKeyType = null;
        if (!string.IsNullOrWhiteSpace(req.PixKey))
        {
            PixKeyNormalizer.TryNormalize(req.PixKeyType!.Value, req.PixKey, out pixKey, out _);
            pixKeyType = req.PixKeyType;
        }

        return new Member
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            Name = req.Name!.Trim(),
            PixKey = pixKey,
            PixKeyType = pixKeyType,
            CreatedAt = createdAt,
        };
    }

    public void Apply(Member member, MemberRequest req)
    {
        string? pixKey = null;
        PixKeyType? pixKeyType = null;
        if (!string.IsNullOrWhiteSpace(req.PixKey))
        {
            PixKeyNormalizer.TryNormalize(req.PixKeyType!.Value, req.PixKey, out pixKey, out _);
            pixKeyType = req.PixKeyType;
        }
        member.Update(req.Name!.Trim(), pixKey, pixKeyType);
    }
}
