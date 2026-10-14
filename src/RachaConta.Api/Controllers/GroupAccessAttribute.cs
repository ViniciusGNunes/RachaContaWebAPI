using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using RachaConta.Api.Data;
using RachaConta.Api.Services;

namespace RachaConta.Api.Controllers;

/// <summary>
/// Requires the group's access (or admin) token in the X-Group-Token header.
/// Unknown group and wrong token are both reported as 404 so group ids cannot be probed.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class GroupAccessAttribute : Attribute, IAsyncActionFilter
{
    public const string HeaderName = "X-Group-Token";

    public bool RequireAdmin { get; init; }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!context.ActionArguments.TryGetValue("id", out var raw) || raw is not Guid id)
            throw new NotFoundException("Group not found.");

        var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        var tokens = await db.Groups.AsNoTracking()
            .Where(g => g.Id == id)
            .Select(g => new { g.AccessToken, g.AdminToken })
            .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

        var provided = context.HttpContext.Request.Headers[HeaderName].ToString();
        var allowed = tokens is not null && (
            Tokens.FixedTimeEquals(provided, tokens.AdminToken) ||
            (!RequireAdmin && Tokens.FixedTimeEquals(provided, tokens.AccessToken)));

        if (!allowed) throw new NotFoundException($"Group '{id}' not found.");

        await next();
    }
}
