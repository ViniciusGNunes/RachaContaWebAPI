using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using RachaConta.Api.Data;
using RachaConta.Api.Services;

namespace RachaConta.Api.Controllers;

/// <summary>
/// Who is calling a group endpoint. A personal member token gives a member identity;
/// the group's AdminToken gives admin power without identity; the AccessToken is only an invite.
/// </summary>
public sealed record GroupActor(Guid? MemberId, bool IsAdmin, bool IsInvite, bool ViaAdminToken = false)
{
    public bool IsIdentified => MemberId is not null;

    /// <summary>True when the actor owns a record created by <paramref name="createdBy"/>, or is an admin.</summary>
    public bool CanModify(Guid? createdBy) => IsAdmin || (MemberId is { } me && createdBy == me);
}

/// <summary>
/// Requires a group token in the X-Group-Token header and resolves the <see cref="GroupActor"/>.
/// Unknown group and wrong token are both reported as 404 so group ids cannot be probed.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class GroupAccessAttribute : Attribute, IAsyncActionFilter
{
    public const string HeaderName = "X-Group-Token";
    public const string ItemKey = "GroupActor";

    /// <summary>Only admins (admin member or AdminToken).</summary>
    public bool RequireAdmin { get; init; }

    /// <summary>Needs a member identity (or an admin). The bare invite token is read only.</summary>
    public bool RequireMember { get; init; }

    /// <summary>Endpoint used to pick an identity: invite or admin token, but no member token.</summary>
    public bool ClaimOnly { get; init; }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!context.ActionArguments.TryGetValue("id", out var raw) || raw is not Guid id)
            throw new NotFoundException("Group not found.");

        var http = context.HttpContext;
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var tokens = await db.Groups.AsNoTracking()
            .Where(g => g.Id == id)
            .Select(g => new { g.AccessToken, g.AdminToken })
            .FirstOrDefaultAsync(http.RequestAborted);

        var provided = http.Request.Headers[HeaderName].ToString();
        GroupActor? actor = null;
        if (tokens is not null)
        {
            if (Tokens.FixedTimeEquals(provided, tokens.AdminToken))
                actor = new GroupActor(null, true, false, ViaAdminToken: true);
            else if (Tokens.FixedTimeEquals(provided, tokens.AccessToken))
                actor = new GroupActor(null, false, true);
            else if (!string.IsNullOrEmpty(provided))
            {
                var hash = Tokens.Hash(provided);
                var member = await db.Members.AsNoTracking()
                    .Where(m => m.GroupId == id && m.TokenHash == hash)
                    .Select(m => new { m.Id, m.IsAdmin })
                    .FirstOrDefaultAsync(http.RequestAborted);
                if (member is not null) actor = new GroupActor(member.Id, member.IsAdmin, false);
            }
        }

        if (actor is null) throw new NotFoundException($"Group '{id}' not found.");

        if (ClaimOnly && actor.IsIdentified)
            throw new ConflictException("You already picked an identity in this group.");
        if (RequireAdmin && !actor.IsAdmin)
            throw new ForbiddenException("Only the group admin can do this.");
        if (RequireMember && !actor.IsIdentified && !actor.IsAdmin)
            throw new ForbiddenException("Pick who you are in this group before making changes.");

        http.Items[ItemKey] = actor;
        await next();
    }
}
