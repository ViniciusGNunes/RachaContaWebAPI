using Microsoft.EntityFrameworkCore;
using RachaPix.Api.Contracts;
using RachaPix.Api.Controllers;
using RachaPix.Api.Data;
using RachaPix.Api.Domain;

namespace RachaPix.Api.Services;

/// <summary>Groups and members orchestration (Module 2).</summary>
public sealed class GroupService(
    AppDbContext db,
    TimeProvider clock,
    Microsoft.Extensions.Options.IOptions<PixOptions> pixOptions,
    GroupQueries queries,
    GroupMapper mapper)
{
    public GroupService(AppDbContext db, TimeProvider clock, Microsoft.Extensions.Options.IOptions<PixOptions> pixOptions)
        : this(db, clock, pixOptions, new GroupQueries(db), new GroupMapper()) { }

    public Task<IReadOnlyList<MyGroupDto>> LookupAsync(IReadOnlyList<GroupLookupItem?>? items, CancellationToken ct) =>
        queries.LookupAsync(items, ct);

    public async Task<GroupDto> GetAsync(Guid id, GroupActor actor, CancellationToken ct)
    {
        var group = await queries.LoadAsync(id, ct);
        return mapper.ToDto(group, actor) with
        {
            AccessToken = actor.IsInvite ? null : group.AccessToken,
            AdminToken = actor.ViaAdminToken ? group.AdminToken : null,
            YourMemberId = actor.MemberId,
            IsAdmin = actor.IsAdmin,
        };
    }

    /// <summary>Picks an identity: claim a free member, or join as a new one. Returns the personal token once.</summary>
    public async Task<ClaimResultDto> ClaimAsync(Guid groupId, ClaimRequest req, GroupActor actor, CancellationToken ct)
    {
        var group = await queries.LoadAsync(groupId, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        Member member;

        if (req.MemberId is { } memberId)
        {
            member = group.FindMember(memberId);
            if (member.ClaimedAt is not null) throw new ConflictException("This member was already claimed by someone.");
        }
        else
        {
            var errors = new ValidationErrors();
            req.Validate(errors);
            var name = req.NewName?.Trim() ?? "";
            if (!errors.Any && group.HasMemberNamed(name))
                errors.Add("newName", $"A member named '{name}' already exists in this group.");
            errors.ThrowIfAny();
            member = new Member { Id = Guid.NewGuid(), Name = name, GroupId = groupId, CreatedAt = now };
            db.Members.Add(member);
            await db.SaveChangesAsync(ct);
        }

        var token = Tokens.NewAdminToken();
        var hash = Tokens.Hash(token);
        var isAdmin = actor.ViaAdminToken;
        var rows = await db.Members
            .Where(m => m.Id == member.Id && m.GroupId == groupId && m.ClaimedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.ClaimedAt, now).SetProperty(m => m.IsAdmin, isAdmin), ct);
        if (rows == 0) throw new ConflictException("This member was already claimed by someone.");

        db.MemberDevices.Add(new MemberDevice
        {
            Id = Guid.NewGuid(), MemberId = member.Id, TokenHash = hash,
            Label = DeviceLinkService.CleanLabel(req.Label), CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);

        member.ClaimedAt = now;
        member.IsAdmin = isAdmin;
        return new ClaimResultDto(token, mapper.ToDto(member));
    }

    /// <summary>Admin frees a member (lost device); the next person to claim gets a fresh token.</summary>
    public async Task ResetClaimAsync(Guid groupId, Guid memberId, CancellationToken ct)
    {
        var rows = await db.Members
            .Where(m => m.Id == memberId && m.GroupId == groupId)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.ClaimedAt, (DateTime?)null).SetProperty(m => m.IsAdmin, false), ct);
        if (rows == 0) throw new NotFoundException($"Member '{memberId}' not found in this group.");
        await db.MemberDevices.Where(d => d.MemberId == memberId).ExecuteDeleteAsync(ct);
        await db.MemberLinkCodes.Where(c => c.MemberId == memberId).ExecuteDeleteAsync(ct);
    }

    public async Task<InviteDto> RotateInviteAsync(Guid groupId, CancellationToken ct)
    {
        var token = Tokens.NewAdminToken();
        var rows = await db.Groups.Where(g => g.Id == groupId)
            .ExecuteUpdateAsync(u => u.SetProperty(g => g.AccessToken, token), ct);
        if (rows == 0) throw new NotFoundException($"Group '{groupId}' not found.");
        return new InviteDto(token);
    }

    public async Task<GroupDto> CreateAsync(CreateGroupRequest req, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        req.Validate(errors);
        errors.ThrowIfAny();

        var now = clock.GetUtcNow().UtcDateTime;
        var group = new Group
        {
            Id = Guid.NewGuid(), Name = req.Name!.Trim(),
            Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
            Currency = "BRL", CreatedAt = now,
            AccessToken = Tokens.NewAdminToken(), AdminToken = Tokens.NewAdminToken(),
        };

        var members = req.Members ?? [];
        for (var i = 0; i < members.Count; i++)
            group.Members.Add(mapper.ToEntity(members[i]!, group.Id, now.AddMilliseconds(i)));

        db.Groups.Add(group);
        await db.SaveChangesAsync(ct);
        return mapper.ToDto(group) with { AccessToken = group.AccessToken, AdminToken = group.AdminToken };
    }

    public async Task<GroupDto> UpdateAsync(Guid id, UpdateGroupRequest req, CancellationToken ct)
    {
        var group = await queries.LoadAsync(id, ct);
        var errors = new ValidationErrors();
        req.Validate(errors);
        errors.ThrowIfAny();

        group.Name = req.Name!.Trim();
        group.Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();
        await db.SaveChangesAsync(ct);
        return mapper.ToDto(group);
    }

    public Task DeleteAsync(Guid id, CancellationToken ct) => queries.DeleteGroupCascadeAsync(id, ct);

    public async Task<MemberDto> AddMemberAsync(Guid groupId, MemberRequest req, CancellationToken ct)
    {
        var group = await queries.LoadAsync(groupId, ct);
        var errors = new ValidationErrors();
        if (req is null) errors.Add("", "Member is required.");
        else
        {
            req.Validate(errors);
            var name = req.Name?.Trim() ?? "";
            if (!errors.Any && group.HasMemberNamed(name))
                errors.Add("name", $"A member named '{name}' already exists in this group.");
        }
        errors.ThrowIfAny();

        var member = mapper.ToEntity(req!, groupId, clock.GetUtcNow().UtcDateTime);
        db.Members.Add(member);
        await db.SaveChangesAsync(ct);
        return mapper.ToDto(member);
    }

    public async Task<MemberDto> UpdateMemberAsync(Guid groupId, Guid memberId, MemberRequest req, GroupActor actor, CancellationToken ct)
    {
        if (!actor.IsAdmin && actor.MemberId != memberId)
            throw new ForbiddenException("You can only edit your own data.");
        var group = await queries.LoadAsync(groupId, ct);
        var member = group.FindMember(memberId);

        var errors = new ValidationErrors();
        if (req is null) errors.Add("", "Member is required.");
        else
        {
            req.Validate(errors);
            var name = req.Name?.Trim() ?? "";
            if (!errors.Any && group.HasMemberNamed(name, memberId))
                errors.Add("name", $"A member named '{name}' already exists in this group.");
        }
        errors.ThrowIfAny();

        mapper.Apply(member, req!);
        await db.SaveChangesAsync(ct);
        return mapper.ToDto(member);
    }

    public Task DeleteMemberAsync(Guid groupId, Guid memberId, CancellationToken ct) =>
        queries.DeleteMemberAsync(groupId, memberId, ct);

    public Task<MemberPixDto> GetMemberPixAsync(Guid groupId, Guid memberId, decimal? amount, CancellationToken ct) =>
        queries.GetMemberPixAsync(groupId, memberId, amount, pixOptions.Value.DefaultCity, ct);

    public Task EnsureGroupExistsAsync(Guid groupId, CancellationToken ct) => queries.EnsureGroupExistsAsync(groupId, ct);

    public static MemberDto ToDto(Member m, bool hidePix = false) => new GroupMapper().ToDto(m, hidePix);
}
