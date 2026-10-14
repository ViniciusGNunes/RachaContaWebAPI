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
}

public class Member
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public string Name { get; set; } = "";
    public string? PixKey { get; set; }
    public PixKeyType? PixKeyType { get; set; }
    public DateTime CreatedAt { get; set; }
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
    public List<ExpenseShare> Shares { get; set; } = [];
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
}
