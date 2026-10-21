namespace RachaConta.Api.Domain;

// ---------- Module 1: Expense Divider ----------

public class Split
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = "";
    public string AdminToken { get; set; } = "";
    public string Title { get; set; } = "";
    public decimal TotalAmount { get; set; }
    public string PixKey { get; set; } = "";
    public PixKeyType PixKeyType { get; set; }
    public string ReceiverName { get; set; } = "";
    public string ReceiverCity { get; set; } = "";
    public SplitMode SplitMode { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<SplitParticipant> Participants { get; set; } = [];

    public int PaidCount => Participants.Count(p => p.Paid);
    public decimal PaidAmount => Participants.Where(p => p.Paid).Sum(p => p.Amount);
    public int PendingCount => Participants.Count(p => p.PaymentPending);

    public SplitParticipant FindParticipant(Guid participantId) =>
        Participants.FirstOrDefault(p => p.Id == participantId)
        ?? throw new Services.NotFoundException($"Participant '{participantId}' not found in this split.");
}

public class SplitParticipant
{
    public Guid Id { get; set; }
    public Guid SplitId { get; set; }
    public int Position { get; set; }
    public string Name { get; set; } = "";
    public decimal Amount { get; set; }
    public bool IsOwner { get; set; }
    public bool Paid { get; set; }
    public DateTime? PaidAt { get; set; }
    public bool PaymentPending { get; set; }
    public DateTime? ClaimedAt { get; set; }

    public void MarkPendingPayment(DateTime now)
    {
        if (!Paid && !PaymentPending)
        {
            PaymentPending = true;
            ClaimedAt = now;
        }
    }

    public void TogglePaid(DateTime now)
    {
        if (IsOwner)
            throw new Services.ValidationException("participantId", "The owner is always marked as paid.");

        Paid = !Paid;
        PaidAt = Paid ? now : null;
        PaymentPending = false;
        ClaimedAt = null;
    }

    public void RejectClaim()
    {
        PaymentPending = false;
        ClaimedAt = null;
    }
}

// ---------- Module 2: Expense Manager ----------

public class Group
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Currency { get; set; } = "BRL";
    public string AccessToken { get; set; } = "";
    public string AdminToken { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public List<Member> Members { get; set; } = [];
    public List<Expense> Expenses { get; set; } = [];
    public List<Settlement> Settlements { get; set; } = [];

    public Member FindMember(Guid memberId) =>
        Members.FirstOrDefault(m => m.Id == memberId)
        ?? throw new Services.NotFoundException($"Member '{memberId}' not found in this group.");

    public bool HasMemberNamed(string name, Guid? excludeMemberId = null) =>
        Members.Any(m => (excludeMemberId == null || m.Id != excludeMemberId) &&
                         string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
}

public class Member
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public string Name { get; set; } = "";
    public string? PixKey { get; set; }
    public PixKeyType? PixKeyType { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>When the member was first claimed; null while nobody holds it. Personal tokens live in <see cref="MemberDevice"/>.</summary>
    public DateTime? ClaimedAt { get; set; }
    public bool IsAdmin { get; set; }

    public void Update(string name, string? pixKey, PixKeyType? pixKeyType)
    {
        Name = name;
        PixKey = pixKey;
        PixKeyType = pixKeyType;
    }
}

/// <summary>One personal token per browser/device that holds a member's identity.</summary>
public class MemberDevice
{
    public Guid Id { get; set; }
    public Guid MemberId { get; set; }
    /// <summary>SHA-256 (hex) of the device's personal token.</summary>
    public string TokenHash { get; set; } = "";
    public string? Label { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

/// <summary>Short-lived, single-use code that lets another device take over the same member.</summary>
public class MemberLinkCode
{
    public Guid Id { get; set; }
    public Guid MemberId { get; set; }
    /// <summary>SHA-256 (hex) of the code.</summary>
    public string CodeHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
}

public class Expense
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public DateOnly Date { get; set; }
    public ExpenseCategory Category { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public Guid PaidByMemberId { get; set; }
    public Member? PaidBy { get; set; }
    public ShareMode ShareMode { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? CreatedByMemberId { get; set; }
    public Guid? UpdatedByMemberId { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public List<ExpenseShare> Shares { get; set; } = [];
    public List<ExpenseConfirmation> Confirmations { get; set; } = [];

    /// <summary>Members who must confirm: everyone sharing the expense except whoever paid it.</summary>
    public HashSet<Guid> RequiredConfirmers() =>
        Shares.Select(s => s.MemberId).Where(id => id != PaidByMemberId).ToHashSet();

    /// <summary>The person who adds or edits an expense already agrees with it, if they take part in it.</summary>
    public List<ExpenseConfirmation> GenerateAutoConfirmations(Guid? actorMemberId, DateTime now) =>
        actorMemberId is { } me && RequiredConfirmers().Contains(me)
            ? [new ExpenseConfirmation { Id = Guid.NewGuid(), ExpenseId = Id, MemberId = me, Status = ConfirmationStatus.Confirmed, CreatedAt = now }]
            : [];

    public ExpenseConfirmation SetConfirmation(Guid memberId, ConfirmationStatus status, string? comment, DateTime now)
    {
        var existing = Confirmations.FirstOrDefault(c => c.MemberId == memberId);
        if (existing is null)
        {
            existing = new ExpenseConfirmation
            {
                Id = Guid.NewGuid(), ExpenseId = Id, MemberId = memberId, Status = status, Comment = comment, CreatedAt = now,
            };
            Confirmations.Add(existing);
        }
        else
        {
            existing.Status = status;
            existing.Comment = comment;
            existing.CreatedAt = now;
        }
        return existing;
    }
}

public class ExpenseConfirmation
{
    public Guid Id { get; set; }
    public Guid ExpenseId { get; set; }
    public Guid MemberId { get; set; }
    public ConfirmationStatus Status { get; set; }
    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ExpenseShare
{
    public Guid Id { get; set; }
    public Guid ExpenseId { get; set; }
    public int Position { get; set; }
    public Guid MemberId { get; set; }
    public Member? Member { get; set; }
    public decimal Amount { get; set; }
    public decimal? Percentage { get; set; }
}

public class Settlement
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public Guid FromMemberId { get; set; }
    public Member? FromMember { get; set; }
    public Guid ToMemberId { get; set; }
    public Member? ToMember { get; set; }
    public decimal Amount { get; set; }
    public DateOnly Date { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? CreatedByMemberId { get; set; }
    public SettlementStatus Status { get; set; } = SettlementStatus.Confirmed;
    public DateTime? ConfirmedAt { get; set; }

    public void Confirm(DateTime now)
    {
        if (Status != SettlementStatus.Confirmed)
        {
            Status = SettlementStatus.Confirmed;
            ConfirmedAt = now;
        }
    }
}
