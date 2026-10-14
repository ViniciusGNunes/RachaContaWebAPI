using Microsoft.EntityFrameworkCore;
using RachaConta.Api.Contracts;
using RachaConta.Api.Data;
using RachaConta.Api.Domain;

namespace RachaConta.Api.Services;

public sealed class PixOptions
{
    /// <summary>Receiver city used for member Pix codes (members have no city).</summary>
    public string DefaultCity { get; set; } = "BRASIL";
}

/// <summary>Groups and members (Module 2).</summary>
public sealed class GroupService(AppDbContext db, TimeProvider clock, Microsoft.Extensions.Options.IOptions<PixOptions> pixOptions)
{
    private const int MaxLookupItems = 100;

    public async Task<IReadOnlyList<MyGroupDto>> LookupAsync(IReadOnlyList<GroupLookupItem?>? items, CancellationToken ct)
    {
        var requested = (items ?? [])
            .Where(i => i is { Id: not null, Token: not null })
            .GroupBy(i => i!.Id!.Value)
            .Select(g => g.First()!)
            .Take(MaxLookupItems)
            .ToList();
        if (requested.Count == 0) return [];

        var ids = requested.Select(i => i.Id!.Value).ToList();
        var groups = await db.Groups.AsNoTracking()
            .Where(g => ids.Contains(g.Id))
            .Select(g => new
            {
                g.Id, g.Name, g.Description, g.Currency, g.CreatedAt, g.AccessToken, g.AdminToken,
                MemberCount = g.Members.Count,
            })
            .ToListAsync(ct);

        var authorized = new List<(Guid Id, bool IsAdmin)>();
        var byId = requested.ToDictionary(i => i.Id!.Value, i => i.Token!);
        foreach (var g in groups)
        {
            var token = byId[g.Id];
            var isAdmin = Tokens.FixedTimeEquals(token, g.AdminToken);
            if (isAdmin || Tokens.FixedTimeEquals(token, g.AccessToken)) authorized.Add((g.Id, isAdmin));
        }
        if (authorized.Count == 0) return [];

        var allowedIds = authorized.Select(a => a.Id).ToList();
        // Sum in memory: SQLite cannot aggregate decimals server-side.
        var amounts = await db.Expenses.AsNoTracking()
            .Where(e => allowedIds.Contains(e.GroupId))
            .Select(e => new { e.GroupId, e.Amount })
            .ToListAsync(ct);
        var totals = amounts.GroupBy(a => a.GroupId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        var adminById = authorized.ToDictionary(a => a.Id, a => a.IsAdmin);

        return groups
            .Where(g => adminById.ContainsKey(g.Id))
            .OrderByDescending(g => g.CreatedAt)
            .Select(g => new MyGroupDto(g.Id, g.Name, g.Description, g.Currency, g.MemberCount,
                totals.GetValueOrDefault(g.Id), g.CreatedAt, adminById[g.Id]))
            .ToList();
    }

    public async Task<GroupDto> GetAsync(Guid id, CancellationToken ct) => ToDto(await LoadAsync(id, ct));

    public async Task<GroupDto> CreateAsync(CreateGroupRequest req, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var (name, description) = ValidateGroupFields(req.Name, req.Description, errors);

        var now = clock.GetUtcNow().UtcDateTime;
        var group = new Group
        {
            Id = Guid.NewGuid(), Name = name, Description = description, Currency = "BRL", CreatedAt = now,
            AccessToken = Tokens.NewAdminToken(), AdminToken = Tokens.NewAdminToken(),
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var members = req.Members ?? [];
        for (var i = 0; i < members.Count; i++)
        {
            var m = ValidateMember(members[i], $"members[{i}].", errors);
            if (m is null) continue;
            if (!seen.Add(m.Name)) { errors.Add($"members[{i}].name", $"Duplicate member name '{m.Name}'."); continue; }
            m.GroupId = group.Id;
            m.CreatedAt = now.AddTicks(i); // keeps creation order stable
            group.Members.Add(m);
        }
        errors.ThrowIfAny();

        db.Groups.Add(group);
        await db.SaveChangesAsync(ct);
        return ToDto(group) with { AccessToken = group.AccessToken, AdminToken = group.AdminToken };
    }

    public async Task<GroupDto> UpdateAsync(Guid id, UpdateGroupRequest req, CancellationToken ct)
    {
        var group = await LoadAsync(id, ct);
        var errors = new ValidationErrors();
        var (name, description) = ValidateGroupFields(req.Name, req.Description, errors);
        errors.ThrowIfAny();

        group.Name = name;
        group.Description = description;
        await db.SaveChangesAsync(ct);
        return ToDto(group);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        // Load the whole aggregate so EF deletes dependents in a valid order on any provider.
        var group = await db.Groups
            .Include(g => g.Members)
            .Include(g => g.Expenses).ThenInclude(e => e.Shares)
            .Include(g => g.Settlements)
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == id, ct)
            ?? throw new NotFoundException($"Group '{id}' not found.");

        db.ExpenseShares.RemoveRange(group.Expenses.SelectMany(e => e.Shares));
        db.Expenses.RemoveRange(group.Expenses);
        db.Settlements.RemoveRange(group.Settlements);
        await db.SaveChangesAsync(ct);
        db.Members.RemoveRange(group.Members);
        db.Groups.Remove(group);
        await db.SaveChangesAsync(ct);
    }

    public async Task<MemberDto> AddMemberAsync(Guid groupId, MemberRequest req, CancellationToken ct)
    {
        var group = await LoadAsync(groupId, ct);
        var errors = new ValidationErrors();
        var member = ValidateMember(req, "", errors);
        if (member is not null && group.Members.Any(m => string.Equals(m.Name, member.Name, StringComparison.OrdinalIgnoreCase)))
            errors.Add("name", $"A member named '{member.Name}' already exists in this group.");
        errors.ThrowIfAny();

        member!.GroupId = groupId;
        member.CreatedAt = clock.GetUtcNow().UtcDateTime;
        db.Members.Add(member);
        await db.SaveChangesAsync(ct);
        return ToDto(member);
    }

    public async Task<MemberDto> UpdateMemberAsync(Guid groupId, Guid memberId, MemberRequest req, CancellationToken ct)
    {
        var group = await LoadAsync(groupId, ct);
        var member = group.Members.FirstOrDefault(m => m.Id == memberId)
                     ?? throw new NotFoundException($"Member '{memberId}' not found in this group.");

        var errors = new ValidationErrors();
        var updated = ValidateMember(req, "", errors);
        if (updated is not null && group.Members.Any(m => m.Id != memberId &&
                string.Equals(m.Name, updated.Name, StringComparison.OrdinalIgnoreCase)))
            errors.Add("name", $"A member named '{updated.Name}' already exists in this group.");
        errors.ThrowIfAny();

        member.Name = updated!.Name;
        member.PixKey = updated.PixKey;
        member.PixKeyType = updated.PixKeyType;
        await db.SaveChangesAsync(ct);
        return ToDto(member);
    }

    public async Task DeleteMemberAsync(Guid groupId, Guid memberId, CancellationToken ct)
    {
        var member = await db.Members.FirstOrDefaultAsync(m => m.Id == memberId && m.GroupId == groupId, ct);
        if (member is null)
        {
            await EnsureGroupExistsAsync(groupId, ct);
            throw new NotFoundException($"Member '{memberId}' not found in this group.");
        }

        var referenced =
            await db.Expenses.AnyAsync(e => e.PaidByMemberId == memberId, ct) ||
            await db.ExpenseShares.AnyAsync(s => s.MemberId == memberId, ct) ||
            await db.Settlements.AnyAsync(s => s.FromMemberId == memberId || s.ToMemberId == memberId, ct);
        if (referenced)
            throw new ConflictException("This member is referenced by expenses or settlements and cannot be removed.");

        db.Members.Remove(member);
        await db.SaveChangesAsync(ct);
    }

    public async Task<MemberPixDto> GetMemberPixAsync(Guid groupId, Guid memberId, decimal? amount, CancellationToken ct)
    {
        var member = await db.Members.AsNoTracking().FirstOrDefaultAsync(m => m.Id == memberId && m.GroupId == groupId, ct);
        if (member is null)
        {
            await EnsureGroupExistsAsync(groupId, ct);
            throw new NotFoundException($"Member '{memberId}' not found in this group.");
        }

        if (amount is not { } value || value <= 0 || value > SplitService.MaxTotal || !ShareCalculator.HasAtMostTwoDecimals(value))
            throw new ValidationException("amount", "Query parameter 'amount' must be greater than 0 with at most 2 decimal places.");
        if (string.IsNullOrEmpty(member.PixKey))
            throw new ValidationException("pixKey", $"Member '{member.Name}' has no Pix key.");

        var receiverName = PixText.Sanitize(member.Name, PixPayload.MaxNameLength);
        if (receiverName.Length == 0) receiverName = "RECEBEDOR";
        var city = PixText.Sanitize(pixOptions.Value.DefaultCity, PixPayload.MaxCityLength);
        if (city.Length == 0) city = "BRASIL";

        var code = PixPayload.Build(member.PixKey, receiverName, city, value);
        return new MemberPixDto(code, value, member.Name, member.PixKey);
    }

    public async Task EnsureGroupExistsAsync(Guid groupId, CancellationToken ct)
    {
        if (!await db.Groups.AnyAsync(g => g.Id == groupId, ct))
            throw new NotFoundException($"Group '{groupId}' not found.");
    }

    private async Task<Group> LoadAsync(Guid id, CancellationToken ct) =>
        await db.Groups.Include(g => g.Members).FirstOrDefaultAsync(g => g.Id == id, ct)
        ?? throw new NotFoundException($"Group '{id}' not found.");

    private static (string Name, string? Description) ValidateGroupFields(string? rawName, string? rawDescription, ValidationErrors errors)
    {
        var name = rawName?.Trim() ?? "";
        if (name.Length is < 1 or > 80) errors.Add("name", "Name must have 1 to 80 characters.");
        var description = string.IsNullOrWhiteSpace(rawDescription) ? null : rawDescription.Trim();
        if (description?.Length > 500) errors.Add("description", "Description must have at most 500 characters.");
        return (name, description);
    }

    /// <summary>Validates a member request; returns a new (unsaved) Member or null when invalid.</summary>
    private static Member? ValidateMember(MemberRequest? req, string prefix, ValidationErrors errors)
    {
        if (req is null)
        {
            errors.Add(prefix.TrimEnd('.'), "Member is required.");
            return null;
        }

        var ok = true;
        var name = req.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 60)
        {
            errors.Add(prefix + "name", "Name must have 1 to 60 characters.");
            ok = false;
        }

        string? pixKey = null;
        PixKeyType? pixKeyType = null;
        if (!string.IsNullOrWhiteSpace(req.PixKey))
        {
            if (req.PixKeyType is not { } type)
            {
                errors.Add(prefix + "pixKeyType", "Pix key type is required when a Pix key is given.");
                ok = false;
            }
            else if (!PixKeyNormalizer.TryNormalize(type, req.PixKey, out var normalized, out var error))
            {
                errors.Add(prefix + "pixKey", error);
                ok = false;
            }
            else
            {
                pixKey = normalized;
                pixKeyType = type;
            }
        }

        return ok ? new Member { Id = Guid.NewGuid(), Name = name, PixKey = pixKey, PixKeyType = pixKeyType } : null;
    }

    private static GroupDto ToDto(Group g) => new(g.Id, g.Name, g.Description, g.Currency, g.CreatedAt,
        g.Members.OrderBy(m => m.CreatedAt).Select(ToDto).ToList());

    public static MemberDto ToDto(Member m) => new(m.Id, m.Name, m.PixKey, m.PixKeyType);
}
