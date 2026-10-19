using Microsoft.AspNetCore.Mvc;
using RachaConta.Api.Contracts;
using RachaConta.Api.Services;

namespace RachaConta.Api.Controllers;

[ApiController]
[Route("api/groups")]
public sealed class GroupsController(GroupService groups, ExpenseService expenses) : ControllerBase
{
    /// <summary>Who is calling; set by <see cref="GroupAccessAttribute"/> on every group endpoint.</summary>
    private GroupActor Actor => (GroupActor)HttpContext.Items[GroupAccessAttribute.ItemKey]!;

    // ---------- Groups ----------

    [HttpPost("lookup")]
    public Task<IReadOnlyList<MyGroupDto>> Lookup([FromBody] List<GroupLookupItem?>? items, CancellationToken ct) =>
        groups.LookupAsync(items, ct);

    [HttpPost]
    public async Task<ActionResult<GroupDto>> Create([FromBody] CreateGroupRequest req, CancellationToken ct)
    {
        var dto = await groups.CreateAsync(req, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    [HttpGet("{id:guid}")]
    [GroupAccess]
    public Task<GroupDto> Get(Guid id, CancellationToken ct) => groups.GetAsync(id, Actor, ct);

    [HttpPut("{id:guid}")]
    [GroupAccess(RequireAdmin = true)]
    public Task<GroupDto> Update(Guid id, [FromBody] UpdateGroupRequest req, CancellationToken ct) =>
        groups.UpdateAsync(id, req, ct);

    [HttpDelete("{id:guid}")]
    [GroupAccess(RequireAdmin = true)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await groups.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>Replaces the invite token; the old invite link stops working.</summary>
    [HttpPost("{id:guid}/rotate-invite")]
    [GroupAccess(RequireAdmin = true)]
    public Task<InviteDto> RotateInvite(Guid id, CancellationToken ct) => groups.RotateInviteAsync(id, ct);

    /// <summary>Pick who you are in the group; returns your personal token once.</summary>
    [HttpPost("{id:guid}/claim")]
    [GroupAccess(ClaimOnly = true)]
    public Task<ClaimResultDto> Claim(Guid id, [FromBody] ClaimRequest req, CancellationToken ct) =>
        groups.ClaimAsync(id, req, Actor, ct);

    // ---------- Members ----------

    [HttpPost("{id:guid}/members")]
    [GroupAccess(RequireMember = true)]
    public async Task<ActionResult<MemberDto>> AddMember(Guid id, [FromBody] MemberRequest req, CancellationToken ct)
    {
        var dto = await groups.AddMemberAsync(id, req, ct);
        return Created($"/api/groups/{id}/members/{dto.Id}", dto);
    }

    [HttpPut("{id:guid}/members/{memberId:guid}")]
    [GroupAccess(RequireMember = true)]
    public Task<MemberDto> UpdateMember(Guid id, Guid memberId, [FromBody] MemberRequest req, CancellationToken ct) =>
        groups.UpdateMemberAsync(id, memberId, req, Actor, ct);

    [HttpDelete("{id:guid}/members/{memberId:guid}")]
    [GroupAccess(RequireAdmin = true)]
    public async Task<IActionResult> DeleteMember(Guid id, Guid memberId, CancellationToken ct)
    {
        await groups.DeleteMemberAsync(id, memberId, ct);
        return NoContent();
    }

    /// <summary>Frees a member whose person lost their device, so it can be claimed again.</summary>
    [HttpPost("{id:guid}/members/{memberId:guid}/reset-claim")]
    [GroupAccess(RequireAdmin = true)]
    public async Task<IActionResult> ResetClaim(Guid id, Guid memberId, CancellationToken ct)
    {
        await groups.ResetClaimAsync(id, memberId, ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/members/{memberId:guid}/pix")]
    [GroupAccess(RequireMember = true)]
    public Task<MemberPixDto> MemberPix(Guid id, Guid memberId, [FromQuery] decimal? amount, CancellationToken ct) =>
        groups.GetMemberPixAsync(id, memberId, amount, ct);

    // ---------- Expenses ----------

    [HttpGet("{id:guid}/expenses")]
    [GroupAccess]
    public Task<IReadOnlyList<ExpenseDto>> ListExpenses(Guid id, [FromQuery] ExpenseFilter filter, CancellationToken ct) =>
        expenses.ListAsync(id, filter, ct);

    [HttpPost("{id:guid}/expenses")]
    [GroupAccess(RequireMember = true)]
    public async Task<ActionResult<ExpenseDto>> CreateExpense(Guid id, [FromBody] ExpenseRequest req, CancellationToken ct)
    {
        var dto = await expenses.CreateAsync(id, req, Actor, ct);
        return CreatedAtAction(nameof(GetExpense), new { id, expenseId = dto.Id }, dto);
    }

    [HttpGet("{id:guid}/expenses/{expenseId:guid}")]
    [GroupAccess]
    public Task<ExpenseDto> GetExpense(Guid id, Guid expenseId, CancellationToken ct) =>
        expenses.GetAsync(id, expenseId, ct);

    [HttpPut("{id:guid}/expenses/{expenseId:guid}")]
    [GroupAccess(RequireMember = true)]
    public Task<ExpenseDto> UpdateExpense(Guid id, Guid expenseId, [FromBody] ExpenseRequest req, CancellationToken ct) =>
        expenses.UpdateAsync(id, expenseId, req, Actor, ct);

    [HttpDelete("{id:guid}/expenses/{expenseId:guid}")]
    [GroupAccess(RequireMember = true)]
    public async Task<IActionResult> DeleteExpense(Guid id, Guid expenseId, CancellationToken ct)
    {
        await expenses.DeleteAsync(id, expenseId, Actor, ct);
        return NoContent();
    }

    /// <summary>A person this expense was split with confirms or contests it.</summary>
    [HttpPost("{id:guid}/expenses/{expenseId:guid}/confirmation")]
    [GroupAccess(RequireMember = true)]
    public Task<ExpenseDto> ConfirmExpense(Guid id, Guid expenseId, [FromBody] ConfirmationRequest req, CancellationToken ct) =>
        expenses.ConfirmAsync(id, expenseId, req, Actor, ct);

    // ---------- Settlements ----------

    [HttpGet("{id:guid}/settlements")]
    [GroupAccess]
    public Task<IReadOnlyList<SettlementDto>> ListSettlements(Guid id, CancellationToken ct) =>
        expenses.ListSettlementsAsync(id, ct);

    [HttpPost("{id:guid}/settlements")]
    [GroupAccess(RequireMember = true)]
    public async Task<ActionResult<SettlementDto>> CreateSettlement(Guid id, [FromBody] CreateSettlementRequest req, CancellationToken ct)
    {
        var dto = await expenses.CreateSettlementAsync(id, req, Actor, ct);
        return Created($"/api/groups/{id}/settlements/{dto.Id}", dto);
    }

    /// <summary>The receiver confirms the money arrived; only then it counts in the balances.</summary>
    [HttpPost("{id:guid}/settlements/{settlementId:guid}/confirm")]
    [GroupAccess(RequireMember = true)]
    public Task<SettlementDto> ConfirmSettlement(Guid id, Guid settlementId, CancellationToken ct) =>
        expenses.ConfirmSettlementAsync(id, settlementId, Actor, ct);

    [HttpDelete("{id:guid}/settlements/{settlementId:guid}")]
    [GroupAccess(RequireMember = true)]
    public async Task<IActionResult> DeleteSettlement(Guid id, Guid settlementId, CancellationToken ct)
    {
        await expenses.DeleteSettlementAsync(id, settlementId, Actor, ct);
        return NoContent();
    }

    // ---------- Balances & stats ----------

    [HttpGet("{id:guid}/balances")]
    [GroupAccess]
    public Task<BalanceDto> Balances(Guid id, CancellationToken ct) => expenses.GetBalancesAsync(id, ct);

    [HttpGet("{id:guid}/stats")]
    [GroupAccess]
    public Task<StatsDto> Stats(Guid id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        expenses.GetStatsAsync(id, from, to, ct);
}
