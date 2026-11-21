using FinanceLedger.Core.Data;
using FinanceLedger.Core.Models;
using FinanceLedger.Core.Security;
using Microsoft.Data.Sqlite;

namespace FinanceLedger.Core.Services;

public sealed class LedgerService
{
    private readonly EncryptedLedger _ledger;
    private readonly LedgerDatabase _db;

    public LedgerService(EncryptedLedger ledger)
    {
        _ledger = ledger;
        _db = ledger.Database ?? throw new InvalidOperationException("账簿尚未解锁。");
    }

    public LedgerDatabase Database => _db;

    public EncryptedLedger GetEncryptedLedger() => _ledger;

    public void Save() => _ledger.Save();

    // ---------- 账户 ----------

    public List<Account> GetAccounts(bool includeArchived = false)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT id,name,kind,wallet,currency,balance,note,is_archived,created_at
            FROM accounts
            WHERE ($all <> 0 OR is_archived = 0)
            ORDER BY kind,name
            """;
        cmd.Parameters.AddWithValue("$all", includeArchived ? 1 : 0);
        var list = new List<Account>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Account
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Kind = (AccountKind)reader.GetInt32(2),
                Wallet = (WalletKind)reader.GetInt32(3),
                Currency = reader.GetString(4),
                Balance = reader.GetDouble(5).ToDecimal(),
                Note = reader.IsDBNull(6) ? null : reader.GetString(6),
                IsArchived = reader.GetInt32(7) != 0,
                CreatedAt = reader.GetString(8)
            });
        }
        return list;
    }

    public List<AccountListItem> GetAccountItems()
    {
        return GetAccounts().Select(a => new AccountListItem
        {
            Id = a.Id,
            Name = a.Name,
            KindName = DomainNames.Chinese(a.Kind),
            Group = GroupOf(a),
            Balance = a.Balance,
            IsLiability = DomainNames.IsLiability(a.Kind),
            Note = a.Note
        }).ToList();
    }

    private static string GroupOf(Account a)
    {
        if (DomainNames.IsLiability(a.Kind))
            return "负债账户";
        if (a.Kind is AccountKind.AlipayBalance or AccountKind.YuEBao)
            return "支付宝";
        if (a.Kind is AccountKind.WechatBalance or AccountKind.QianTongBao)
            return "微信";
        if (a.Kind == AccountKind.SecuritiesCash)
            return "证券";
        return "银行卡与现金";
    }

    public string AddAccount(CreateAccountRequest request)
    {
        var account = new Account
        {
            Name = request.Name.Trim(),
            Kind = request.Kind,
            Wallet = request.Wallet,
            Balance = request.Balance,
            Note = request.Note
        };
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO accounts(id,name,kind,wallet,currency,balance,note,is_archived,created_at)
            VALUES($id,$name,$kind,$wallet,'CNY',$balance,$note,0,$created)
            """;
        cmd.Parameters.AddWithValue("$id", account.Id);
        cmd.Parameters.AddWithValue("$name", account.Name);
        cmd.Parameters.AddWithValue("$kind", (int)account.Kind);
        cmd.Parameters.AddWithValue("$wallet", (int)account.Wallet);
        cmd.Parameters.AddWithValue("$balance", (double)account.Balance);
        cmd.Parameters.AddWithValue("$note", (object?)account.Note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", account.CreatedAt);
        cmd.ExecuteNonQuery();

        if (account.Kind is AccountKind.CreditCard or AccountKind.Huabei)
            EnsureCreditSettings(account.Id);

        Save();
        return account.Id;
    }

    public void UpdateAccountBalance(string accountId, decimal balance)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE accounts SET balance=$balance WHERE id=$id";
        cmd.Parameters.AddWithValue("$balance", (double)balance);
        cmd.Parameters.AddWithValue("$id", accountId);
        cmd.ExecuteNonQuery();
        Save();
    }

    public Account? FindDefaultWallet(PaymentChannel channel)
    {
        var wallet = DomainNames.WalletOf(channel);
        if (wallet == WalletKind.None)
            return null;
        var kind = wallet == WalletKind.Alipay ? AccountKind.AlipayBalance : AccountKind.WechatBalance;
        return GetAccounts().FirstOrDefault(a => a.Kind == kind);
    }

    // ---------- 分类 ----------

    public List<Category> GetCategories()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT id,name,kind,sort FROM categories ORDER BY kind,sort";
        var list = new List<Category>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new Category
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Kind = reader.GetString(2),
                Sort = reader.GetInt32(3)
            });
        return list;
    }

    public string? GetCategoryName(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return string.Empty;
        return GetCategories().FirstOrDefault(c => c.Id == id)?.Name;
    }

    // ---------- 交易与分录 ----------

    public List<TransactionListItem> GetRecentTransactions(int limit = 200)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT t.id,t.occurred_at,t.type,t.channel,t.amount,t.merchant,t.note,c.name
            FROM transactions t LEFT JOIN categories c ON c.id = t.category_id
            ORDER BY t.occurred_at DESC, t.created_at DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<TransactionListItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new TransactionListItem
            {
                Id = reader.GetString(0),
                DateText = DateTime.Parse(reader.GetString(1)).ToString("MM-dd HH:mm"),
                TypeName = DomainNames.Chinese((TransactionType)reader.GetInt32(2)),
                ChannelName = DomainNames.Chinese((PaymentChannel)reader.GetInt32(3)),
                Amount = reader.GetDouble(4).ToDecimal(),
                Merchant = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                Note = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                CategoryName = reader.IsDBNull(7) ? string.Empty : reader.GetString(7)
            });
        }
        return list;
    }

    public string CreateTransaction(CreateTransactionRequest request)
    {
        if (request.Amount <= 0)
            throw new ArgumentException("金额必须大于零。");
        if (string.IsNullOrWhiteSpace(request.FundingAccountId) &&
            request.Type is TransactionType.Expense or TransactionType.Repayment or TransactionType.Transfer)
            throw new InvalidOperationException("请选择出资账户。");

        var tx = new TransactionRecord
        {
            OccurredAt = request.OccurredAt,
            PostedAt = request.PostedAt == default ? request.OccurredAt : request.PostedAt,
            Type = request.Type,
            Channel = request.Channel,
            FundingAccountId = request.FundingAccountId,
            WalletAccountId = request.WalletAccountId,
            Amount = request.Amount,
            Merchant = request.Merchant?.Trim(),
            CategoryId = request.CategoryId,
            Note = request.Note?.Trim(),
            Source = string.IsNullOrWhiteSpace(request.Source) ? "Manual" : request.Source
        };

        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO transactions(id,occurred_at,posted_at,type,channel,funding_account_id,
                    wallet_account_id,amount,merchant,category_id,note,source,source_key,created_at)
                VALUES($id,$occurred,$posted,$type,$channel,$funding,$wallet,$amount,$merchant,
                    $category,$note,$source,$sourceKey,$created)
                """;
            cmd.Parameters.AddWithValue("$id", tx.Id);
            cmd.Parameters.AddWithValue("$occurred", tx.OccurredAt.ToString("O"));
            cmd.Parameters.AddWithValue("$posted", tx.PostedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$type", (int)tx.Type);
            cmd.Parameters.AddWithValue("$channel", (int)tx.Channel);
            cmd.Parameters.AddWithValue("$funding", (object?)tx.FundingAccountId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$wallet", (object?)tx.WalletAccountId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$amount", (double)tx.Amount);
            cmd.Parameters.AddWithValue("$merchant", (object?)tx.Merchant ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$category", (object?)tx.CategoryId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$note", (object?)tx.Note ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source", tx.Source);
            cmd.Parameters.AddWithValue("$sourceKey", (object?)request.SourceKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created", tx.CreatedAt);
            cmd.ExecuteNonQuery();
        }

        foreach (var posting in BuildPostings(tx, request.BillingCycleId))
            InsertPosting(posting);

        Save();
        return tx.Id;
    }

    private List<Posting> BuildPostings(TransactionRecord tx, string? requestedCycleId)
    {
        var postings = new List<Posting>();
        int seq = 0;
        string? funding = tx.FundingAccountId;
        string? wallet = tx.WalletAccountId;
        bool throughWallet = tx.Channel is PaymentChannel.Alipay or PaymentChannel.Wechat;

        if (throughWallet && string.IsNullOrEmpty(wallet))
            wallet = FindDefaultWallet(tx.Channel)?.Id;

        switch (tx.Type)
        {
            case TransactionType.Expense:
                if (throughWallet && !string.IsNullOrEmpty(wallet))
                {
                    if (!string.IsNullOrEmpty(funding) && funding != wallet)
                    {
                        postings.Add(new Posting
                        {
                            TransactionId = tx.Id,
                            Seq = seq++,
                            FromAccountId = funding,
                            ToAccountId = wallet,
                            Amount = tx.Amount,
                            Kind = PostingKind.Transfer,
                            BillingCycleId = CycleForSource(funding, tx.PostedAt, requestedCycleId, allowNew: true)
                        });
                    }
                    postings.Add(new Posting
                    {
                        TransactionId = tx.Id,
                        Seq = seq++,
                        FromAccountId = wallet,
                        Amount = tx.Amount,
                        Kind = PostingKind.Expense,
                        CategoryId = tx.CategoryId,
                        ExternalParty = tx.Merchant
                    });
                }
                else
                {
                    postings.Add(new Posting
                    {
                        TransactionId = tx.Id,
                        Seq = seq++,
                        FromAccountId = funding,
                        Amount = tx.Amount,
                        Kind = PostingKind.Expense,
                        CategoryId = tx.CategoryId,
                        ExternalParty = tx.Merchant,
                        BillingCycleId = CycleForSource(funding, tx.PostedAt, requestedCycleId, allowNew: true)
                    });
                }
                break;

            case TransactionType.Income:
                var incomeTarget = throughWallet && !string.IsNullOrEmpty(wallet) ? wallet : funding;
                postings.Add(new Posting
                {
                    TransactionId = tx.Id,
                    Seq = seq++,
                    ToAccountId = incomeTarget,
                    Amount = tx.Amount,
                    Kind = PostingKind.Income,
                    CategoryId = tx.CategoryId,
                    ExternalParty = tx.Merchant
                });
                break;

            case TransactionType.Transfer:
                postings.Add(new Posting
                {
                    TransactionId = tx.Id,
                    Seq = seq++,
                    FromAccountId = funding,
                    ToAccountId = wallet ?? funding,
                    Amount = tx.Amount,
                    Kind = PostingKind.Transfer,
                    ExternalParty = tx.Merchant
                });
                break;

            case TransactionType.Repayment:
                postings.Add(new Posting
                {
                    TransactionId = tx.Id,
                    Seq = seq++,
                    FromAccountId = funding,
                    ToAccountId = wallet,
                    Amount = tx.Amount,
                    Kind = PostingKind.Repayment,
                    BillingCycleId = requestedCycleId ?? CycleForSource(wallet, tx.PostedAt, null, allowNew: false),
                    ExternalParty = tx.Merchant
                });
                break;

            case TransactionType.Investment:
                postings.Add(new Posting
                {
                    TransactionId = tx.Id,
                    Seq = seq++,
                    FromAccountId = funding,
                    ToAccountId = wallet,
                    Amount = tx.Amount,
                    Kind = PostingKind.Transfer,
                    ExternalParty = tx.Merchant
                });
                break;
        }

        return postings;
    }

    private string? CycleForSource(string? accountId, DateTime postedAt, string? explicitId, bool allowNew)
    {
        if (string.IsNullOrEmpty(accountId))
            return null;
        if (!string.IsNullOrEmpty(explicitId))
            return explicitId;
        var account = GetAccounts().FirstOrDefault(a => a.Id == accountId);
        if (account is null || account.Kind is not (AccountKind.CreditCard or AccountKind.Huabei))
            return null;
        return allowNew ? GetOrCreateCycle(accountId, postedAt).Id : null;
    }

    private void InsertPosting(Posting posting)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO postings(id,transaction_id,seq,from_account_id,to_account_id,amount,kind,
                category_id,billing_cycle_id,external_party)
            VALUES($id,$tx,$seq,$from,$to,$amount,$kind,$category,$cycle,$party)
            """;
        cmd.Parameters.AddWithValue("$id", posting.Id);
        cmd.Parameters.AddWithValue("$tx", posting.TransactionId);
        cmd.Parameters.AddWithValue("$seq", posting.Seq);
        cmd.Parameters.AddWithValue("$from", (object?)posting.FromAccountId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$to", (object?)posting.ToAccountId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$amount", (double)posting.Amount);
        cmd.Parameters.AddWithValue("$kind", (int)posting.Kind);
        cmd.Parameters.AddWithValue("$category", (object?)posting.CategoryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cycle", (object?)posting.BillingCycleId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$party", (object?)posting.ExternalParty ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        ApplyPosting(posting, 1);
    }

    public void DeleteTransaction(string id)
    {
        var postings = GetPostings(id);
        foreach (var posting in postings)
            ApplyPosting(posting, -1);

        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM postings WHERE transaction_id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM transactions WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        Save();
    }

    private List<Posting> GetPostings(string transactionId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT id,transaction_id,seq,from_account_id,to_account_id,amount,kind,category_id,
                   billing_cycle_id,external_party
            FROM postings WHERE transaction_id=$id ORDER BY seq
            """;
        cmd.Parameters.AddWithValue("$id", transactionId);
        var list = new List<Posting>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new Posting
            {
                Id = reader.GetString(0),
                TransactionId = reader.GetString(1),
                Seq = reader.GetInt32(2),
                FromAccountId = reader.IsDBNull(3) ? null : reader.GetString(3),
                ToAccountId = reader.IsDBNull(4) ? null : reader.GetString(4),
                Amount = reader.GetDouble(5).ToDecimal(),
                Kind = (PostingKind)reader.GetInt32(6),
                CategoryId = reader.IsDBNull(7) ? null : reader.GetString(7),
                BillingCycleId = reader.IsDBNull(8) ? null : reader.GetString(8),
                ExternalParty = reader.IsDBNull(9) ? null : reader.GetString(9)
            });
        return list;
    }

    private void ApplyPosting(Posting posting, int sign)
    {
        if (!string.IsNullOrEmpty(posting.FromAccountId))
            ApplyAccountDelta(posting.FromAccountId, -posting.Amount * sign);
        if (!string.IsNullOrEmpty(posting.ToAccountId))
            ApplyAccountDelta(posting.ToAccountId, posting.Amount * sign);
    }

    private void ApplyAccountDelta(string accountId, decimal delta)
    {
        var account = GetAccounts().FirstOrDefault(a => a.Id == accountId);
        if (account is null)
            return;
        // 负债账户的 balance 表示欠款，方向与资产相反。
        var storedDelta = DomainNames.IsLiability(account.Kind) ? -delta : delta;
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE accounts SET balance = balance + $delta WHERE id=$id";
        cmd.Parameters.AddWithValue("$delta", (double)storedDelta);
        cmd.Parameters.AddWithValue("$id", accountId);
        cmd.ExecuteNonQuery();
    }

    // ---------- 信用卡账单周期 ----------

    private CreditAccountSettings EnsureCreditSettings(string accountId)
    {
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT OR IGNORE INTO credit_accounts(account_id,statement_day,due_day,credit_limit)
                VALUES($id,5,25,0)
                """;
            cmd.Parameters.AddWithValue("$id", accountId);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT statement_day,due_day,credit_limit FROM credit_accounts WHERE account_id=$id";
            cmd.Parameters.AddWithValue("$id", accountId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
                return new CreditAccountSettings
                {
                    AccountId = accountId,
                    StatementDay = reader.GetInt32(0),
                    DueDay = reader.GetInt32(1),
                    CreditLimit = reader.GetDouble(2).ToDecimal()
                };
        }
        return new CreditAccountSettings { AccountId = accountId, StatementDay = 5, DueDay = 25 };
    }

    public CreditAccountSettings GetCreditSettings(string accountId) => EnsureCreditSettings(accountId);

    public void UpdateCreditSettings(string accountId, int statementDay, int dueDay, decimal limit)
    {
        EnsureCreditSettings(accountId);
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            UPDATE credit_accounts SET statement_day=$s, due_day=$d, credit_limit=$l WHERE account_id=$id
            """;
        cmd.Parameters.AddWithValue("$s", Math.Clamp(statementDay, 1, 28));
        cmd.Parameters.AddWithValue("$d", Math.Clamp(dueDay, 1, 28));
        cmd.Parameters.AddWithValue("$l", (double)limit);
        cmd.Parameters.AddWithValue("$id", accountId);
        cmd.ExecuteNonQuery();
        Save();
    }

    public BillingCycle GetOrCreateCycle(string accountId, DateTime date)
    {
        var settings = EnsureCreditSettings(accountId);
        var statement = settings.StatementDay;
        DateTime start, end;
        if (date.Day > statement)
        {
            start = new DateTime(date.Year, date.Month, statement).AddDays(1);
            end = new DateTime(date.Year, date.Month, statement).AddMonths(1);
        }
        else
        {
            end = new DateTime(date.Year, date.Month, statement);
            start = end.AddMonths(-1).AddDays(1);
        }
        var due = new DateTime(end.Year, end.Month, Math.Min(settings.DueDay, DateTime.DaysInMonth(end.Year, end.Month)));
        if (due <= end)
            due = due.AddMonths(1);

        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id,period_start,period_end,due_date,opening_balance,adjustment,status
                FROM billing_cycles
                WHERE account_id=$id AND period_start=$start
                """;
            cmd.Parameters.AddWithValue("$id", accountId);
            cmd.Parameters.AddWithValue("$start", start.ToString("yyyy-MM-dd"));
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
                return ReadCycle(reader);
        }

        var cycle = new BillingCycle
        {
            AccountId = accountId,
            PeriodStart = start,
            PeriodEnd = end,
            DueDate = due
        };
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO billing_cycles(id,account_id,period_start,period_end,due_date,opening_balance,adjustment,status)
                VALUES($id,$account,$start,$end,$due,0,0,'Open')
                """;
            cmd.Parameters.AddWithValue("$id", cycle.Id);
            cmd.Parameters.AddWithValue("$account", accountId);
            cmd.Parameters.AddWithValue("$start", cycle.PeriodStart.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$end", cycle.PeriodEnd.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$due", cycle.DueDate.ToString("yyyy-MM-dd"));
            cmd.ExecuteNonQuery();
        }
        Save();
        return cycle;
    }

    private static BillingCycle ReadCycle(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        PeriodStart = DateTime.Parse(reader.GetString(1)),
        PeriodEnd = DateTime.Parse(reader.GetString(2)),
        DueDate = DateTime.Parse(reader.GetString(3)),
        OpeningBalance = reader.GetDouble(4).ToDecimal(),
        Adjustment = reader.GetDouble(5).ToDecimal(),
        Status = reader.GetString(6)
    };

    public void UpdateCycleAdjustment(string cycleId, decimal adjustment)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE billing_cycles SET adjustment=$a WHERE id=$id";
        cmd.Parameters.AddWithValue("$a", (double)adjustment);
        cmd.Parameters.AddWithValue("$id", cycleId);
        cmd.ExecuteNonQuery();
        Save();
    }

    public List<BillingCycleView> GetBillingCycleViews()
    {
        var accounts = GetAccounts().ToDictionary(a => a.Id);
        var cycles = new List<BillingCycle>();
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id,account_id,period_start,period_end,due_date,opening_balance,adjustment,status FROM billing_cycles ORDER BY due_date";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                cycles.Add(new BillingCycle
                {
                    Id = reader.GetString(0),
                    AccountId = reader.GetString(1),
                    PeriodStart = DateTime.Parse(reader.GetString(2)),
                    PeriodEnd = DateTime.Parse(reader.GetString(3)),
                    DueDate = DateTime.Parse(reader.GetString(4)),
                    OpeningBalance = reader.GetDouble(5).ToDecimal(),
                    Adjustment = reader.GetDouble(6).ToDecimal(),
                    Status = reader.GetString(7)
                });
            }
        }

        var result = new List<BillingCycleView>();
        foreach (var cycle in cycles)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = """
                SELECT COALESCE(SUM(CASE WHEN kind IN (0,1) THEN amount ELSE 0 END),0),
                       COALESCE(SUM(CASE WHEN kind=3 THEN amount ELSE 0 END),0)
                FROM postings WHERE billing_cycle_id=$id
                """;
            cmd.Parameters.AddWithValue("$id", cycle.Id);
            using var reader = cmd.ExecuteReader();
            reader.Read();
            decimal charges = reader.GetDouble(0).ToDecimal();
            decimal paid = reader.GetDouble(1).ToDecimal();
            result.Add(new BillingCycleView
            {
                Id = cycle.Id,
                AccountName = accounts.TryGetValue(cycle.AccountId, out var a) ? a.Name : "未知账户",
                PeriodText = $"{cycle.PeriodStart:MM/dd} - {cycle.PeriodEnd:MM/dd}",
                DueText = cycle.DueDate.ToString("yyyy-MM-dd"),
                Charges = charges,
                Refunds = 0,
                Payable = cycle.OpeningBalance + cycle.Adjustment + charges - paid,
                Status = cycle.Status
            });
        }
        return result;
    }

    // ---------- 贷款 ----------

    public List<Loan> GetLoans()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT id,name,kind,original_principal,remaining_principal,annual_rate,term_months,
                   repayment_method,monthly_payment,payment_day,start_date,note
            FROM loans ORDER BY name
            """;
        var list = new List<Loan>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Loan
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Kind = reader.GetString(2),
                OriginalPrincipal = reader.GetDouble(3).ToDecimal(),
                RemainingPrincipal = reader.GetDouble(4).ToDecimal(),
                AnnualRate = reader.GetDouble(5),
                TermMonths = reader.GetInt32(6),
                RepaymentMethod = reader.GetString(7),
                MonthlyPayment = reader.GetDouble(8).ToDecimal(),
                PaymentDay = reader.GetInt32(9),
                StartDate = reader.IsDBNull(10) ? null : DateTime.Parse(reader.GetString(10)),
                Note = reader.IsDBNull(11) ? null : reader.GetString(11)
            });
        }
        return list;
    }

    public void AddLoan(Loan loan)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO loans(id,name,kind,original_principal,remaining_principal,annual_rate,term_months,
                repayment_method,monthly_payment,payment_day,start_date,note)
            VALUES($id,$name,$kind,$original,$remaining,$rate,$term,$method,$monthly,$day,$start,$note)
            """;
        cmd.Parameters.AddWithValue("$id", loan.Id);
        cmd.Parameters.AddWithValue("$name", loan.Name);
        cmd.Parameters.AddWithValue("$kind", loan.Kind);
        cmd.Parameters.AddWithValue("$original", (double)loan.OriginalPrincipal);
        cmd.Parameters.AddWithValue("$remaining", (double)loan.RemainingPrincipal);
        cmd.Parameters.AddWithValue("$rate", loan.AnnualRate);
        cmd.Parameters.AddWithValue("$term", loan.TermMonths);
        cmd.Parameters.AddWithValue("$method", loan.RepaymentMethod);
        cmd.Parameters.AddWithValue("$monthly", (double)loan.MonthlyPayment);
        cmd.Parameters.AddWithValue("$day", loan.PaymentDay);
        cmd.Parameters.AddWithValue("$start", (object?)loan.StartDate?.ToString("yyyy-MM-dd") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$note", (object?)loan.Note ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        Save();
    }

    public void UpdateLoanRemaining(string id, decimal remaining)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE loans SET remaining_principal=$r WHERE id=$id";
        cmd.Parameters.AddWithValue("$r", (double)remaining);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        Save();
    }

    public void UpdateLoanPlan(string id, decimal remaining, decimal monthly, int term)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE loans SET remaining_principal=$r, monthly_payment=$m, term_months=$t WHERE id=$id";
        cmd.Parameters.AddWithValue("$r", (double)remaining);
        cmd.Parameters.AddWithValue("$m", (double)monthly);
        cmd.Parameters.AddWithValue("$t", term);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        Save();
    }

    public void DeleteLoan(string id)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM loans WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        Save();
    }

    // ---------- 汇总 ----------

    public LedgerSummary GetSummary()
    {
        var accounts = GetAccounts();
        var summary = new LedgerSummary();
        foreach (var account in accounts)
        {
            if (account.Kind is AccountKind.CreditCard or AccountKind.Huabei)
                summary.CreditLiabilities += account.Balance;
            else if (!DomainNames.IsLiability(account.Kind))
                summary.LiquidAssets += account.Balance;
        }
        summary.InvestmentAssets = GetInvestmentMarketValue();
        summary.LoanLiabilities = GetLoans().Sum(l => l.RemainingPrincipal);

        var now = DateTime.Now;
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT type, SUM(amount) FROM transactions
            WHERE substr(occurred_at,1,7)=$month
            GROUP BY type
            """;
        cmd.Parameters.AddWithValue("$month", now.ToString("yyyy-MM"));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var type = (TransactionType)reader.GetInt32(0);
            var amount = reader.GetDouble(1).ToDecimal();
            if (type == TransactionType.Income)
                summary.MonthIncome += amount;
            if (type == TransactionType.Expense)
                summary.MonthExpense += amount;
        }
        return summary;
    }

    public decimal GetInvestmentMarketValue()
    {
        var value = _ledger.GetMeta("investment_market_value");
        return decimal.TryParse(value, out var result) ? result : 0m;
    }

    public void SetInvestmentMarketValue(decimal value)
    {
        _ledger.SetMeta("investment_market_value", value.ToString("0.00"));
        Save();
    }

    // ---------- 规则（为第二版导入预留） ----------

    public List<ClassificationRule> GetRules()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT id,keyword,category_id,funding_account_id FROM rules ORDER BY keyword";
        var list = new List<ClassificationRule>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new ClassificationRule
            {
                Id = reader.GetString(0),
                Keyword = reader.GetString(1),
                CategoryId = reader.IsDBNull(2) ? null : reader.GetString(2),
                FundingAccountId = reader.IsDBNull(3) ? null : reader.GetString(3)
            });
        return list;
    }

    public void AddRule(ClassificationRule rule)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO rules(id,keyword,category_id,funding_account_id) VALUES($id,$k,$c,$f)";
        cmd.Parameters.AddWithValue("$id", rule.Id);
        cmd.Parameters.AddWithValue("$k", rule.Keyword);
        cmd.Parameters.AddWithValue("$c", (object?)rule.CategoryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$f", (object?)rule.FundingAccountId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        Save();
    }
}

internal static class DecimalExtensions
{
    public static decimal ToDecimal(this double value) => Convert.ToDecimal(value);
}
