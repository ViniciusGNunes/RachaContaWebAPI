using RachaConta.Api.Domain;

namespace RachaConta.Api.Contracts;

// Requests use nullable members so missing fields produce field-level 400s instead of defaults.

public sealed record CreateSplitRequest(
    string? Title,
    decimal? TotalAmount,
    string? PixKey,
    PixKeyType? PixKeyType,
    string? ReceiverName,
    string? ReceiverCity,
    SplitMode? SplitMode,
    bool? IncludeOwner,
    List<CreateSplitParticipantRequest?>? Participants);

public sealed record CreateSplitParticipantRequest(string? Name, decimal? Amount, bool? IsOwner);

public sealed record SplitParticipantDto(Guid Id, string Name, decimal Amount, bool IsOwner, bool Paid, bool PaymentPending);

public sealed record SplitPublicDto(
    string Slug,
    string Title,
    decimal TotalAmount,
    string ReceiverName,
    DateTime CreatedAt,
    IReadOnlyList<SplitParticipantDto> Participants,
    int PaidCount,
    decimal PaidAmount,
    int PendingCount);

public sealed record SplitAdminParticipantDto(
    Guid Id, string Name, decimal Amount, bool IsOwner, bool Paid, DateTime? PaidAt, bool PaymentPending, DateTime? ClaimedAt);

public sealed record SplitAdminDto(
    string Slug,
    string Title,
    decimal TotalAmount,
    string ReceiverName,
    DateTime CreatedAt,
    IReadOnlyList<SplitAdminParticipantDto> Participants,
    int PaidCount,
    decimal PaidAmount,
    int PendingCount,
    string PixKey,
    PixKeyType PixKeyType,
    string ReceiverCity,
    SplitMode SplitMode,
    string? AdminToken);

public sealed record SplitPixDto(Guid ParticipantId, string Name, decimal Amount, string PixKey, string ReceiverName, string PixCode);
