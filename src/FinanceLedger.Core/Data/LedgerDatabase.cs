using Microsoft.Data.Sqlite;

namespace FinanceLedger.Core.Data;

public static class LedgerSchema
{
    public const string Sql = """
        PRAGMA journal_mode=DELETE;
        PRAGMA foreign_keys=ON;

        CREATE TABLE IF NOT EXISTS meta (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS accounts (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            kind INTEGER NOT NULL,
            wallet INTEGER NOT NULL DEFAULT 0,
            currency TEXT NOT NULL DEFAULT 'CNY',
            balance REAL NOT NULL DEFAULT 0,
            note TEXT,
            is_archived INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS categories (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            kind TEXT NOT NULL DEFAULT 'Expense',
            sort INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS transactions (
            id TEXT PRIMARY KEY,
            occurred_at TEXT NOT NULL,
            posted_at TEXT NOT NULL,
            type INTEGER NOT NULL,
            channel INTEGER NOT NULL,
            funding_account_id TEXT,
            wallet_account_id TEXT,
            amount REAL NOT NULL,
            merchant TEXT,
            category_id TEXT,
            note TEXT,
            source TEXT NOT NULL DEFAULT 'Manual',
            source_key TEXT,
            created_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_transactions_occurred ON transactions(occurred_at);

        CREATE TABLE IF NOT EXISTS postings (
            id TEXT PRIMARY KEY,
            transaction_id TEXT NOT NULL,
            seq INTEGER NOT NULL,
            from_account_id TEXT,
            to_account_id TEXT,
            amount REAL NOT NULL,
            kind INTEGER NOT NULL,
            category_id TEXT,
            billing_cycle_id TEXT,
            external_party TEXT,
            FOREIGN KEY(transaction_id) REFERENCES transactions(id)
        );
        CREATE INDEX IF NOT EXISTS ix_postings_tx ON postings(transaction_id);
        CREATE INDEX IF NOT EXISTS ix_postings_cycle ON postings(billing_cycle_id);

        CREATE TABLE IF NOT EXISTS credit_accounts (
            account_id TEXT PRIMARY KEY,
            statement_day INTEGER NOT NULL DEFAULT 5,
            due_day INTEGER NOT NULL DEFAULT 25,
            credit_limit REAL NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS billing_cycles (
            id TEXT PRIMARY KEY,
            account_id TEXT NOT NULL,
            period_start TEXT NOT NULL,
            period_end TEXT NOT NULL,
            due_date TEXT NOT NULL,
            opening_balance REAL NOT NULL DEFAULT 0,
            adjustment REAL NOT NULL DEFAULT 0,
            status TEXT NOT NULL DEFAULT 'Open'
        );
        CREATE INDEX IF NOT EXISTS ix_cycles_account ON billing_cycles(account_id, period_start);

        CREATE TABLE IF NOT EXISTS loans (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            kind TEXT NOT NULL DEFAULT 'Mortgage',
            original_principal REAL NOT NULL,
            remaining_principal REAL NOT NULL,
            annual_rate REAL NOT NULL DEFAULT 0,
            term_months INTEGER NOT NULL DEFAULT 0,
            repayment_method TEXT NOT NULL DEFAULT 'EqualInstallment',
            monthly_payment REAL NOT NULL DEFAULT 0,
            payment_day INTEGER NOT NULL DEFAULT 10,
            start_date TEXT,
            note TEXT
        );

        CREATE TABLE IF NOT EXISTS snapshots (
            id TEXT PRIMARY KEY,
            date TEXT NOT NULL,
            liquid REAL NOT NULL,
            investments REAL NOT NULL,
            liabilities REAL NOT NULL,
            net_worth REAL NOT NULL
        );

        CREATE TABLE IF NOT EXISTS rules (
            id TEXT PRIMARY KEY,
            keyword TEXT NOT NULL,
            category_id TEXT,
            funding_account_id TEXT
        );

        CREATE TABLE IF NOT EXISTS import_batches (
            id TEXT PRIMARY KEY,
            file_name TEXT NOT NULL,
            platform INTEGER NOT NULL,
            imported_at TEXT NOT NULL,
            row_count INTEGER NOT NULL DEFAULT 0,
            is_undone INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS import_rows (
            id TEXT PRIMARY KEY,
            batch_id TEXT NOT NULL,
            raw_json TEXT NOT NULL,
            occurred_at TEXT NOT NULL,
            type INTEGER NOT NULL,
            channel INTEGER NOT NULL,
            amount REAL NOT NULL,
            merchant TEXT,
            category_id TEXT,
            funding_account_id TEXT,
            wallet_account_id TEXT,
            note TEXT,
            dedupe_key TEXT NOT NULL,
            status INTEGER NOT NULL DEFAULT 0,
            error_message TEXT,
            transaction_id TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_import_rows_batch ON import_rows(batch_id, status);

        CREATE TABLE IF NOT EXISTS holdings (
            id TEXT PRIMARY KEY,
            account_id TEXT NOT NULL,
            code TEXT NOT NULL,
            name TEXT NOT NULL,
            market TEXT NOT NULL DEFAULT 'A股',
            quantity REAL NOT NULL,
            cost_price REAL NOT NULL,
            currency TEXT NOT NULL DEFAULT 'CNY',
            price_date TEXT,
            current_price REAL NOT NULL DEFAULT 0,
            price_source TEXT NOT NULL DEFAULT '手工'
        );

        CREATE TABLE IF NOT EXISTS price_cache (
            code TEXT NOT NULL,
            trade_date TEXT NOT NULL,
            close REAL NOT NULL,
            adj_factor REAL NOT NULL DEFAULT 1,
            source TEXT NOT NULL DEFAULT 'Tushare',
            PRIMARY KEY(code, trade_date)
        );

        CREATE TABLE IF NOT EXISTS loan_payments (
            id TEXT PRIMARY KEY,
            loan_id TEXT NOT NULL,
            period INTEGER NOT NULL,
            due_date TEXT NOT NULL,
            payment REAL NOT NULL,
            principal REAL NOT NULL,
            interest REAL NOT NULL,
            remaining_principal REAL NOT NULL,
            status TEXT NOT NULL DEFAULT 'Planned',
            paid_at TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_loan_payments_loan ON loan_payments(loan_id, period);
        """;

    public static void Apply(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Sql;
        cmd.ExecuteNonQuery();
    }

    public static void Seed(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM categories";
        if (Convert.ToInt32(cmd.ExecuteScalar()) > 0)
            return;

        string[] expense =
        [
            "餐饮", "交通", "购物", "居住", "通讯", "娱乐", "医疗", "教育",
            "人情往来", "旅行", "数码", "运动", "宠物", "其他支出"
        ];
        string[] income = ["工资", "奖金", "投资收益", "报销", "其他收入"];

        for (int i = 0; i < expense.Length; i++)
            InsertCategory(connection, expense[i], "Expense", i);
        for (int i = 0; i < income.Length; i++)
            InsertCategory(connection, income[i], "Income", i);

        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM accounts";
        if (Convert.ToInt32(count.ExecuteScalar()) > 0)
            return;

        InsertAccount(connection, "支付宝余额", 1, 1);
        InsertAccount(connection, "微信零钱", 2, 2);
        InsertAccount(connection, "证券资金账户", 5, 0);
    }

    private static void InsertAccount(SqliteConnection connection, string name, int kind, int wallet)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO accounts(id,name,kind,wallet,currency,balance,created_at)
            VALUES($id,$name,$kind,$wallet,'CNY',0,$created)
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$wallet", wallet);
        cmd.Parameters.AddWithValue("$created", DateTimeOffset.Now.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static void InsertCategory(SqliteConnection connection, string name, string kind, int sort)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO categories(id,name,kind,sort) VALUES($id,$name,$kind,$sort)";
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$sort", sort);
        cmd.ExecuteNonQuery();
    }
}

public sealed class LedgerDatabase : IDisposable
{
    public SqliteConnection Connection { get; }

    public LedgerDatabase(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        Connection = new SqliteConnection(builder.ToString());
        Connection.Open();
    }

    public void Initialize()
    {
        LedgerSchema.Apply(Connection);
        EnsureColumns();
        LedgerSchema.Seed(Connection);
    }

    private void EnsureColumns()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(transactions)";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                names.Add(reader.GetString(1));
        if (!names.Contains("source_key"))
        {
            using var alter = Connection.CreateCommand();
            alter.CommandText = "ALTER TABLE transactions ADD COLUMN source_key TEXT";
            alter.ExecuteNonQuery();
        }
    }

    public void Dispose() => Connection.Dispose();
}
