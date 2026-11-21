namespace FinanceLedger.Core.Models;

public enum AccountKind
{
    BankCard,
    AlipayBalance,
    WechatBalance,
    YuEBao,
    QianTongBao,
    SecuritiesCash,
    Cash,
    CreditCard,
    Huabei,
    Loan
}

public enum WalletKind
{
    None,
    Alipay,
    Wechat
}

public enum PaymentChannel
{
    Alipay,
    Wechat,
    BankCard,
    Cash,
    Other
}

public enum TransactionType
{
    Expense,
    Income,
    Transfer,
    Repayment,
    Investment
}

public enum PostingKind
{
    Transfer,
    Expense,
    Income,
    Repayment
}

public static class DomainNames
{
    public static string Chinese(AccountKind kind) => kind switch
    {
        AccountKind.BankCard => "银行卡",
        AccountKind.AlipayBalance => "支付宝余额",
        AccountKind.WechatBalance => "微信零钱",
        AccountKind.YuEBao => "余额宝",
        AccountKind.QianTongBao => "零钱通",
        AccountKind.SecuritiesCash => "证券资金账户",
        AccountKind.Cash => "现金",
        AccountKind.CreditCard => "信用卡",
        AccountKind.Huabei => "花呗",
        AccountKind.Loan => "贷款",
        _ => kind.ToString()
    };

    public static string Chinese(PaymentChannel channel) => channel switch
    {
        PaymentChannel.Alipay => "支付宝",
        PaymentChannel.Wechat => "微信支付",
        PaymentChannel.BankCard => "银行卡直付",
        PaymentChannel.Cash => "现金",
        PaymentChannel.Other => "其他",
        _ => channel.ToString()
    };

    public static string Chinese(TransactionType type) => type switch
    {
        TransactionType.Expense => "支出",
        TransactionType.Income => "收入",
        TransactionType.Transfer => "转账",
        TransactionType.Repayment => "还款",
        TransactionType.Investment => "投资",
        _ => type.ToString()
    };

    public static bool IsLiability(AccountKind kind) =>
        kind is AccountKind.CreditCard or AccountKind.Huabei or AccountKind.Loan;

    public static WalletKind WalletOf(PaymentChannel channel) => channel switch
    {
        PaymentChannel.Alipay => WalletKind.Alipay,
        PaymentChannel.Wechat => WalletKind.Wechat,
        _ => WalletKind.None
    };
}

public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public AccountKind Kind { get; set; }
    public WalletKind Wallet { get; set; }
    public string Currency { get; set; } = "CNY";

    /// <summary>资产账户：可用余额。负债账户：当前欠款额。</summary>
    public decimal Balance { get; set; }
    public string? Note { get; set; }
    public bool IsArchived { get; set; }
    public string CreatedAt { get; set; } = DateTimeOffset.Now.ToString("O");
}

public sealed class Category
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "Expense";
    public int Sort { get; set; }
}

public sealed class TransactionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime OccurredAt { get; set; }
    public DateTime PostedAt { get; set; }
    public TransactionType Type { get; set; }
    public PaymentChannel Channel { get; set; }
    public string? FundingAccountId { get; set; }
    public string? WalletAccountId { get; set; }
    public decimal Amount { get; set; }
    public string? Merchant { get; set; }
    public string? CategoryId { get; set; }
    public string? Note { get; set; }
    public string Source { get; set; } = "Manual";
    public string CreatedAt { get; set; } = DateTimeOffset.Now.ToString("O");
}

public sealed class Posting
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TransactionId { get; set; } = string.Empty;
    public int Seq { get; set; }
    public string? FromAccountId { get; set; }
    public string? ToAccountId { get; set; }
    public decimal Amount { get; set; }
    public PostingKind Kind { get; set; }
    public string? CategoryId { get; set; }
    public string? BillingCycleId { get; set; }
    public string? ExternalParty { get; set; }
}

public sealed class CreditAccountSettings
{
    public string AccountId { get; set; } = string.Empty;
    public int StatementDay { get; set; } = 5;
    public int DueDay { get; set; } = 25;
    public decimal CreditLimit { get; set; }
}

public sealed class BillingCycle
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AccountId { get; set; } = string.Empty;
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public DateTime DueDate { get; set; }
    public decimal OpeningBalance { get; set; }
    public decimal Adjustment { get; set; }
    public string Status { get; set; } = "Open";
}

public sealed class Loan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "Mortgage";
    public decimal OriginalPrincipal { get; set; }
    public decimal RemainingPrincipal { get; set; }
    public double AnnualRate { get; set; }
    public int TermMonths { get; set; }
    public string RepaymentMethod { get; set; } = "EqualInstallment";
    public decimal MonthlyPayment { get; set; }
    public int PaymentDay { get; set; } = 10;
    public DateTime? StartDate { get; set; }
    public string? Note { get; set; }
    public string KindName => Kind switch { "Mortgage" => "房贷", "Auto" => "车贷", _ => "其他" };
    public string RemainingText => $"¥{RemainingPrincipal:N2}";
    public string MonthlyText => $"¥{MonthlyPayment:N2}";
    public string DayText => $"每月 {PaymentDay} 日";
}

public sealed class NetWorthSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Date { get; set; }
    public decimal LiquidAssets { get; set; }
    public decimal InvestmentValue { get; set; }
    public decimal Liabilities { get; set; }
    public decimal NetWorth { get; set; }
}

public sealed class ClassificationRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Keyword { get; set; } = string.Empty;
    public string? CategoryId { get; set; }
    public string? FundingAccountId { get; set; }
}

public sealed class CreateTransactionRequest
{
    public DateTime OccurredAt { get; set; } = DateTime.Today;
    public DateTime PostedAt { get; set; } = DateTime.Today;
    public TransactionType Type { get; set; } = TransactionType.Expense;
    public PaymentChannel Channel { get; set; } = PaymentChannel.Alipay;
    public string? FundingAccountId { get; set; }
    public string? WalletAccountId { get; set; }
    public decimal Amount { get; set; }
    public string? Merchant { get; set; }
    public string? CategoryId { get; set; }
    public string? Note { get; set; }
    public string? BillingCycleId { get; set; }
    public string Source { get; set; } = "Manual";
    public string? SourceKey { get; set; }
}

public sealed class CreateAccountRequest
{
    public string Name { get; set; } = string.Empty;
    public AccountKind Kind { get; set; } = AccountKind.BankCard;
    public WalletKind Wallet { get; set; }
    public decimal Balance { get; set; }
    public string? Note { get; set; }
}

public sealed class AccountListItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string KindName { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public decimal Balance { get; set; }
    public bool IsLiability { get; set; }
    public string? Note { get; set; }
    public string BalanceText => $"¥{Balance:N2}";
}

public sealed class TransactionListItem
{
    public string Id { get; set; } = string.Empty;
    public string DateText { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public string ChannelName { get; set; } = string.Empty;
    public string Merchant { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Note { get; set; } = string.Empty;
    public string AmountText => $"¥{Math.Abs(Amount):N2}";
}

public sealed class BillingCycleView
{
    public string Id { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string PeriodText { get; set; } = string.Empty;
    public string DueText { get; set; } = string.Empty;
    public decimal Charges { get; set; }
    public decimal Refunds { get; set; }
    public decimal Payable { get; set; }
    public string Status { get; set; } = string.Empty;
    public string PayableText => $"¥{Payable:N2}";
}

public sealed class LedgerSummary
{
    public decimal LiquidAssets { get; set; }
    public decimal InvestmentAssets { get; set; }
    public decimal CreditLiabilities { get; set; }
    public decimal LoanLiabilities { get; set; }
    public decimal TotalAssets => LiquidAssets + InvestmentAssets;
    public decimal TotalLiabilities => CreditLiabilities + LoanLiabilities;
    public decimal NetWorth => TotalAssets - TotalLiabilities;
    public decimal MonthIncome { get; set; }
    public decimal MonthExpense { get; set; }
    public decimal MonthBalance => MonthIncome - MonthExpense;
}
