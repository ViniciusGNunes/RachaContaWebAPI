using Microsoft.EntityFrameworkCore;
using RachaPix.Api.Contracts;
using RachaPix.Api.Data;
using RachaPix.Api.Domain;

namespace RachaPix.Api.Services;

/// <summary>Query and loading operations for groups (Module 2).</summary>
public sealed class GroupQueries(AppDbContext db)
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
        var hashes = byId.Values.Select(Tokens.Hash).ToList();
        var memberHits = await db.MemberDevices.AsNoTracking()
            .Join(db.Members, d => d.MemberId, m => m.Id, (d, m) => new { m.GroupId, d.TokenHash, m.IsAdmin })
            .Where(h => ids.Contains(h.GroupId) && hashes.Contains(h.TokenHash))
            .ToListAsync(ct);
        foreach (var g in groups)
        {
            var token = byId[g.Id];
            var tokenHash = Tokens.Hash(token);
            var isAdmin = Tokens.FixedTimeEquals(token, g.AdminToken);
            var memberHit = memberHits.FirstOrDefault(h => h.GroupId == g.Id && h.TokenHash == tokenHash);
            if (isAdmin || memberHit is not null || Tokens.FixedTimeEquals(token, g.AccessToken))
                authorized.Add((g.Id, isAdmin || memberHit is { IsAdmin: true }));
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

    public async Task<Group> LoadAsync(Guid id, CancellationToken ct) =>
        await db.Groups.Include(g => g.Members).FirstOrDefaultAsync(g => g.Id == id, ct)
        ?? throw new NotFoundException($"Group '{id}' not found.");

    public async Task EnsureGroupExistsAsync(Guid groupId, CancellationToken ct)
    {
        if (!await db.Groups.AnyAsync(g => g.Id == groupId, ct))
            throw new NotFoundException($"Group '{groupId}' not found.");
    }

    public async Task<Group> LoadForDeletionAsync(Guid id, CancellationToken ct) =>
        await db.Groups
            .Include(g => g.Members)
            .Include(g => g.Expenses).ThenInclude(e => e.Shares)
            .Include(g => g.Expenses).ThenInclude(e => e.Confirmations)
            .Include(g => g.Settlements)
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == id, ct)
            ?? throw new NotFoundException($"Group '{id}' not found.");

    public async Task DeleteGroupCascadeAsync(Guid id, CancellationToken ct)
    {
        var group = await LoadForDeletionAsync(id, ct);
        db.ExpenseConfirmations.RemoveRange(group.Expenses.SelectMany(e => e.Confirmations));
        db.ExpenseShares.RemoveRange(group.Expenses.SelectMany(e => e.Shares));
        db.Expenses.RemoveRange(group.Expenses);
        db.Settlements.RemoveRange(group.Settlements);
        await db.SaveChangesAsync(ct);
        db.Members.RemoveRange(group.Members);
        db.Groups.Remove(group);
        await db.SaveChangesAsync(ct);
    }

    public async Task<MemberPixDto> GetMemberPixAsync(Guid groupId, Guid memberId, decimal? amount, string defaultCity, CancellationToken ct)
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
        var city = PixText.Sanitize(defaultCity, PixPayload.MaxCityLength);
        if (city.Length == 0) city = "BRASIL";

        var code = PixPayload.Build(member.PixKey, receiverName, city, value);
        return new MemberPixDto(code, value, member.Name, member.PixKey);
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

        await db.ExpenseConfirmations.Where(c => c.MemberId == memberId).ExecuteDeleteAsync(ct);
        db.Members.Remove(member);
        await db.SaveChangesAsync(ct);
    }
}
