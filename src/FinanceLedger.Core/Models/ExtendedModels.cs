namespace FinanceLedger.Core.Models;

public enum ImportPlatform
{
    Alipay,
    Wechat
}

public enum ImportRowStatus
{
    Pending,
    Confirmed,
    Ignored,
    Deleted
}

public sealed class ImportBatch
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FileName { get; set; } = string.Empty;
    public ImportPlatform Platform { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.Now;
    public int RowCount { get; set; }
    public bool IsUndone { get; set; }
}

public sealed class ImportedRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string BatchId { get; set; } = string.Empty;
    public string RawJson { get; set; } = "{}";
    public DateTime OccurredAt { get; set; }
    public TransactionType Type { get; set; }
    public PaymentChannel Channel { get; set; }
    public decimal Amount { get; set; }
    public string Merchant { get; set; } = string.Empty;
    public string? CategoryId { get; set; }
    public string? FundingAccountId { get; set; }
    public string? WalletAccountId { get; set; }
    public string? Note { get; set; }
    public string DedupeKey { get; set; } = string.Empty;
    public ImportRowStatus Status { get; set; } = ImportRowStatus.Pending;
    public string? ErrorMessage { get; set; }
    public string TransactionId { get; set; } = string.Empty;

    public string DateText => OccurredAt.ToString("yyyy-MM-dd HH:mm");
    public string TypeName => DomainNames.Chinese(Type);
    public string ChannelName => DomainNames.Chinese(Channel);
    public string AmountText => $"¥{Amount:N2}";
    public string StatusText => Status switch
    {
        ImportRowStatus.Confirmed => "已入账",
        ImportRowStatus.Ignored => "已忽略",
        ImportRowStatus.Deleted => "已删除",
        _ => "待确认"
    };
    public string ErrorText => ErrorMessage ?? string.Empty;
}

public sealed class Holding
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AccountId { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Market { get; set; } = "A股";
    public decimal Quantity { get; set; }
    public decimal CostPrice { get; set; }
    public string Currency { get; set; } = "CNY";
    public DateTime? PriceDate { get; set; }
    public decimal CurrentPrice { get; set; }
    public string PriceSource { get; set; } = "手工";

    public decimal MarketValue => Quantity * CurrentPrice;
    public decimal CostValue => Quantity * CostPrice;
    public decimal Profit => MarketValue - CostValue;
    public string MarketValueText => $"¥{MarketValue:N2}";
    public string ProfitText => $"¥{Profit:N2}";
    public string PriceText => CurrentPrice <= 0 ? "未更新" : $"{CurrentPrice:N3} ({PriceDate:yyyy-MM-dd})";
    public string QuantityText => Quantity.ToString("0.##");
    public string CostText => $"{CostPrice:N3}";
}

public sealed class PriceRecord
{
    public string Code { get; set; } = string.Empty;
    public DateTime TradeDate { get; set; }
    public decimal Close { get; set; }
    public decimal AdjFactor { get; set; } = 1m;
    public string Source { get; set; } = "Tushare";
}

public sealed class LoanPayment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string LoanId { get; set; } = string.Empty;
    public int Period { get; set; }
    public DateTime DueDate { get; set; }
    public decimal Payment { get; set; }
    public decimal Principal { get; set; }
    public decimal Interest { get; set; }
    public decimal RemainingPrincipal { get; set; }
    public string Status { get; set; } = "Planned";
    public DateTime? PaidAt { get; set; }

    public string DueText => DueDate.ToString("yyyy-MM-dd");
    public string PaymentText => $"¥{Payment:N2}";
    public string PrincipalText => $"¥{Principal:N2}";
    public string InterestText => $"¥{Interest:N2}";
    public string RemainingText => $"¥{RemainingPrincipal:N2}";
}
