using Microsoft.AspNetCore.Mvc;
using RachaConta.Api.Contracts;
using RachaConta.Api.Services;

namespace RachaConta.Api.Controllers;

[ApiController]
[Route("api/splits")]
public sealed class SplitsController(SplitService splits) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<SplitAdminDto>> Create([FromBody] CreateSplitRequest req, CancellationToken ct)
    {
        var dto = await splits.CreateAsync(req, ct);
        return CreatedAtAction(nameof(Get), new { slug = dto.Slug }, dto);
    }

    [HttpGet("{slug}")]
    public Task<SplitPublicDto> Get(string slug, CancellationToken ct) => splits.GetPublicAsync(slug, ct);

    [HttpGet("{slug}/participants/{participantId:guid}/pix")]
    public Task<SplitPixDto> Pix(string slug, Guid participantId, CancellationToken ct) =>
        splits.GetPixAsync(slug, participantId, ct);

    [HttpPost("{slug}/participants/{participantId:guid}/mark-paid")]
    public Task<SplitPublicDto> MarkPaid(string slug, Guid participantId, CancellationToken ct) =>
        splits.MarkPaidAsync(slug, participantId, ct);

    [HttpGet("{slug}/admin")]
    public Task<SplitAdminDto> Admin(string slug, [FromQuery] string? token, CancellationToken ct) =>
        splits.GetAdminAsync(slug, token, ct);

    [HttpPost("{slug}/admin/participants/{participantId:guid}/toggle-paid")]
    public Task<SplitAdminDto> TogglePaid(string slug, Guid participantId, [FromQuery] string? token, CancellationToken ct) =>
        splits.TogglePaidAsync(slug, participantId, token, ct);

    [HttpPost("{slug}/admin/participants/{participantId:guid}/reject-claim")]
    public Task<SplitAdminDto> RejectClaim(string slug, Guid participantId, [FromQuery] string? token, CancellationToken ct) =>
        splits.RejectClaimAsync(slug, participantId, token, ct);

    [HttpDelete("{slug}")]
    public async Task<IActionResult> Delete(string slug, [FromQuery] string? token, CancellationToken ct)
    {
        await splits.DeleteAsync(slug, token, ct);
        return NoContent();
    }
}
