namespace RachaPix.Api.Domain;

public enum PixKeyType
{
    Cpf,
    Cnpj,
    Email,
    Phone,
    Random,
}

public enum SplitMode
{
    Equal,
    Custom,
}

public enum ShareMode
{
    Equal,
    Custom,
    Percentage,
}

public enum PaymentMethod
{
    Pix,
    Cash,
    CreditCard,
    DebitCard,
    BankTransfer,
    MealVoucher,
    Other,
}

public enum ExpenseCategory
{
    Food,
    Groceries,
    Housing,
    Utilities,
    Transport,
    Entertainment,
    Health,
    Travel,
    Shopping,
    Other,
    Uncategorized,
}

public enum ConfirmationStatus
{
    Confirmed,
    Contested,
}

public enum SettlementStatus
{
    Pending,
    Confirmed,
}
