using RachaConta.Api.Domain;

namespace RachaConta.Api.Contracts;

// ---------- Requests ----------

public sealed record CreateGroupRequest(string? Name, string? Description, List<MemberRequest?>? Members);

public sealed record UpdateGroupRequest(string? Name, string? Description);

public sealed record MemberRequest(string? Name, string? PixKey, PixKeyType? PixKeyType);

public sealed record ExpenseRequest(
    string? Description,
    decimal? Amount,
    DateOnly? Date,
    ExpenseCategory? Category,
    PaymentMethod? PaymentMethod,
    Guid? PaidByMemberId,
    ShareMode? ShareMode,
    List<ExpenseShareRequest?>? Shares,
    string? Notes);

public sealed record ExpenseShareRequest(Guid? MemberId, decimal? Amount, decimal? Percentage);

public sealed record CreateSettlementRequest(
    Guid? FromMemberId,
    Guid? ToMemberId,
    decimal? Amount,
    DateOnly? Date,
    PaymentMethod? PaymentMethod = null,
    string? Notes = null);

/// <summary>Pick who you are: an existing unclaimed member, or a new one by name.</summary>
public sealed record ClaimRequest(Guid? MemberId, string? NewName);

public sealed record ConfirmationRequest(ConfirmationStatus? Status, string? Comment);

public sealed record ExpenseFilter(DateOnly? From, DateOnly? To, ExpenseCategory? Category, Guid? MemberId);

// ---------- Responses ----------

public sealed record GroupDto(
    Guid Id,
    string Name,
    string? Description,
    string Currency,
    DateTime CreatedAt,
    IReadOnlyList<MemberDto> Members,
    string? AccessToken = null,
    string? AdminToken = null,
    Guid? YourMemberId = null,
    bool IsAdmin = false);

/// <summary>Returned once when someone claims a member: the personal token is never shown again.</summary>
public sealed record ClaimResultDto(string MemberToken, MemberDto Member);

public sealed record InviteDto(string AccessToken);

public sealed record GroupLookupItem(Guid? Id, string? Token);

public sealed record MyGroupDto(
    Guid Id,
    string Name,
    string? Description,
    string Currency,
    int MemberCount,
    decimal TotalSpent,
    DateTime CreatedAt,
    bool IsAdmin);

public sealed record MemberDto(Guid Id, string Name, string? PixKey, PixKeyType? PixKeyType, bool Claimed = false, bool IsAdmin = false);

public sealed record ExpenseConfirmationDto(Guid MemberId, string MemberName, ConfirmationStatus Status, string? Comment, DateTime CreatedAt);

public sealed record ExpenseShareDto(Guid MemberId, string MemberName, decimal Amount, decimal? Percentage);

public sealed record ExpenseDto(
    Guid Id,
    Guid GroupId,
    string Description,
    decimal Amount,
    DateOnly Date,
    ExpenseCategory Category,
    PaymentMethod PaymentMethod,
    Guid PaidByMemberId,
    string PaidByName,
    ShareMode ShareMode,
    IReadOnlyList<ExpenseShareDto> Shares,
    string? Notes,
    DateTime CreatedAt,
    Guid? CreatedByMemberId,
    string? CreatedByName,
    string? UpdatedByName,
    DateTime? UpdatedAt,
    IReadOnlyList<ExpenseConfirmationDto> Confirmations);

public sealed record SettlementDto(
    Guid Id,
    Guid GroupId,
    Guid FromMemberId,
    string FromName,
    Guid ToMemberId,
    string ToName,
    decimal Amount,
    DateOnly Date,
    PaymentMethod PaymentMethod,
    string? Notes,
    DateTime CreatedAt,
    Guid? CreatedByMemberId,
    string? CreatedByName,
    SettlementStatus Status,
    DateTime? ConfirmedAt);

public sealed record MemberBalanceDto(Guid MemberId, string Name, decimal Paid, decimal Owed, decimal SettlementsSent, decimal SettlementsReceived, decimal Net);

public sealed record TransferDto(Guid FromMemberId, string FromName, Guid ToMemberId, string ToName, decimal Amount);

public sealed record BalanceDto(Guid GroupId, IReadOnlyList<MemberBalanceDto> Members, IReadOnlyList<TransferDto> SuggestedTransfers);

public sealed record CategoryTotalDto(ExpenseCategory Category, decimal Total);
public sealed record MemberStatsDto(Guid MemberId, string Name, decimal Paid, decimal Owed);
public sealed record MonthTotalDto(string Month, decimal Total);

public sealed record StatsDto(
    Guid GroupId,
    decimal TotalSpent,
    IReadOnlyList<CategoryTotalDto> ByCategory,
    IReadOnlyList<MemberStatsDto> ByMember,
    IReadOnlyList<MonthTotalDto> ByMonth);

public sealed record MemberPixDto(string PixCode, decimal Amount, string Name, string PixKey);
