using FinanceLedger.Core.Data;
using FinanceLedger.Core.Models;
using Microsoft.Data.Sqlite;

namespace FinanceLedger.Core.Services;

public sealed class ImportService
{
    private readonly LedgerService _ledger;
    private readonly LedgerDatabase _db;

    public ImportService(LedgerService ledger)
    {
        _ledger = ledger;
        _db = ledger.Database;
    }

    public List<ImportBatch> GetBatches()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT id,file_name,platform,imported_at,row_count,is_undone FROM import_batches ORDER BY imported_at DESC";
        var list = new List<ImportBatch>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new ImportBatch
            {
                Id = reader.GetString(0),
                FileName = reader.GetString(1),
                Platform = (ImportPlatform)reader.GetInt32(2),
                ImportedAt = DateTime.Parse(reader.GetString(3)),
                RowCount = reader.GetInt32(4),
                IsUndone = reader.GetInt32(5) != 0
            });
        return list;
    }

    public ImportBatch CreateBatch(ImportParseResult result)
    {
        var batch = result.Batch;
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO import_batches(id,file_name,platform,imported_at,row_count,is_undone)
                VALUES($id,$file,$platform,$at,$count,0)
                """;
            cmd.Parameters.AddWithValue("$id", batch.Id);
            cmd.Parameters.AddWithValue("$file", batch.FileName);
            cmd.Parameters.AddWithValue("$platform", (int)batch.Platform);
            cmd.Parameters.AddWithValue("$at", batch.ImportedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$count", result.Rows.Count);
            cmd.ExecuteNonQuery();
        }

        foreach (var row in result.Rows)
        {
            if (IsDuplicate(row))
            {
                row.Status = ImportRowStatus.Ignored;
                row.ErrorMessage = "重复记录，已自动忽略";
            }
            InsertRow(row);
        }
        _ledger.Save();
        return batch;
    }

    private bool IsDuplicate(ImportedRow row)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT CASE WHEN EXISTS(SELECT 1 FROM transactions WHERE source_key=$k) THEN 1
                        WHEN EXISTS(SELECT 1 FROM import_rows WHERE dedupe_key=$k AND status<>3) THEN 1
                        ELSE 0 END
            """;
        cmd.Parameters.AddWithValue("$k", row.DedupeKey);
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    private void InsertRow(ImportedRow row)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO import_rows(id,batch_id,raw_json,occurred_at,type,channel,amount,merchant,
                category_id,funding_account_id,wallet_account_id,note,dedupe_key,status,error_message,transaction_id)
            VALUES($id,$batch,$raw,$at,$type,$channel,$amount,$merchant,$category,$funding,$wallet,
                $note,$key,$status,$error,$tx)
            """;
        cmd.Parameters.AddWithValue("$id", row.Id);
        cmd.Parameters.AddWithValue("$batch", row.BatchId);
        cmd.Parameters.AddWithValue("$raw", row.RawJson);
        cmd.Parameters.AddWithValue("$at", row.OccurredAt.ToString("O"));
        cmd.Parameters.AddWithValue("$type", (int)row.Type);
        cmd.Parameters.AddWithValue("$channel", (int)row.Channel);
        cmd.Parameters.AddWithValue("$amount", (double)row.Amount);
        cmd.Parameters.AddWithValue("$merchant", row.Merchant);
        cmd.Parameters.AddWithValue("$category", (object?)row.CategoryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$funding", (object?)row.FundingAccountId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$wallet", (object?)row.WalletAccountId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$note", (object?)row.Note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$key", row.DedupeKey);
        cmd.Parameters.AddWithValue("$status", (int)row.Status);
        cmd.Parameters.AddWithValue("$error", (object?)row.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tx", (object?)row.TransactionId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public List<ImportedRow> GetRows(string? batchId = null, ImportRowStatus? status = null)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT id,batch_id,raw_json,occurred_at,type,channel,amount,merchant,category_id,
                   funding_account_id,wallet_account_id,note,dedupe_key,status,error_message,transaction_id
            FROM import_rows
            WHERE ($batch = '' OR batch_id=$batch) AND ($status = -1 OR status=$status)
            ORDER BY occurred_at DESC
            """;
        cmd.Parameters.AddWithValue("$batch", batchId ?? string.Empty);
        cmd.Parameters.AddWithValue("$status", status is null ? -1 : (int)status);
        var list = new List<ImportedRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(ReadRow(reader));
        return list;
    }

    private static ImportedRow ReadRow(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        BatchId = reader.GetString(1),
        RawJson = reader.GetString(2),
        OccurredAt = DateTime.Parse(reader.GetString(3)),
        Type = (TransactionType)reader.GetInt32(4),
        Channel = (PaymentChannel)reader.GetInt32(5),
        Amount = reader.GetDouble(6).ToDecimal(),
        Merchant = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
        CategoryId = reader.IsDBNull(8) ? null : reader.GetString(8),
        FundingAccountId = reader.IsDBNull(9) ? null : reader.GetString(9),
        WalletAccountId = reader.IsDBNull(10) ? null : reader.GetString(10),
        Note = reader.IsDBNull(11) ? null : reader.GetString(11),
        DedupeKey = reader.GetString(12),
        Status = (ImportRowStatus)reader.GetInt32(13),
        ErrorMessage = reader.IsDBNull(14) ? null : reader.GetString(14),
        TransactionId = reader.IsDBNull(15) ? string.Empty : reader.GetString(15)
    };

    public void UpdateRow(ImportedRow row)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            UPDATE import_rows SET occurred_at=$at,type=$type,amount=$amount,merchant=$merchant,
                category_id=$category,funding_account_id=$funding,wallet_account_id=$wallet,note=$note
            WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$at", row.OccurredAt.ToString("O"));
        cmd.Parameters.AddWithValue("$type", (int)row.Type);
        cmd.Parameters.AddWithValue("$amount", (double)row.Amount);
        cmd.Parameters.AddWithValue("$merchant", row.Merchant);
        cmd.Parameters.AddWithValue("$category", (object?)row.CategoryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$funding", (object?)row.FundingAccountId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$wallet", (object?)row.WalletAccountId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$note", (object?)row.Note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", row.Id);
        cmd.ExecuteNonQuery();
        _ledger.Save();
    }

    public string ConfirmRow(string rowId, bool rememberRule = false)
    {
        var row = GetRows().First(r => r.Id == rowId);
        if (row.Status == ImportRowStatus.Confirmed)
            return row.TransactionId;

        ApplyRules(row);
        var request = new CreateTransactionRequest
        {
            OccurredAt = row.OccurredAt,
            PostedAt = row.OccurredAt,
            Type = row.Type,
            Channel = row.Channel,
            FundingAccountId = row.FundingAccountId,
            WalletAccountId = row.WalletAccountId,
            Amount = row.Amount,
            Merchant = row.Merchant,
            CategoryId = row.CategoryId,
            Note = row.Note,
            Source = "Import",
            SourceKey = row.DedupeKey
        };
        var txId = _ledger.CreateTransaction(request);

        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE import_rows SET status=1, transaction_id=$tx, error_message=NULL WHERE id=$id";
            cmd.Parameters.AddWithValue("$tx", txId);
            cmd.Parameters.AddWithValue("$id", row.Id);
            cmd.ExecuteNonQuery();
        }

        if (rememberRule && !string.IsNullOrWhiteSpace(row.Merchant))
            _ledger.AddRule(new ClassificationRule
            {
                Keyword = ExtractKeyword(row.Merchant),
                CategoryId = row.CategoryId,
                FundingAccountId = row.FundingAccountId
            });

        _ledger.Save();
        return txId;
    }

    private void ApplyRules(ImportedRow row)
    {
        var rule = _ledger.GetRules().FirstOrDefault(r =>
            !string.IsNullOrWhiteSpace(r.Keyword) && row.Merchant.Contains(r.Keyword, StringComparison.OrdinalIgnoreCase));
        if (rule is not null)
        {
            row.CategoryId ??= rule.CategoryId;
            row.FundingAccountId ??= rule.FundingAccountId;
        }

        row.CategoryId ??= _ledger.GetCategories()
            .FirstOrDefault(c => c.Kind == (row.Type == TransactionType.Income ? "Income" : "Expense"))?.Id;

        var accounts = _ledger.GetAccounts();
        var wallet = _ledger.FindDefaultWallet(row.Channel);
        if (row.Type == TransactionType.Expense && row.WalletAccountId is null && wallet is not null)
            row.WalletAccountId = wallet.Id;

        row.FundingAccountId ??= GuessFundingAccount(row, accounts) ??
            accounts.FirstOrDefault(a => a.Kind == AccountKind.BankCard)?.Id ?? wallet?.Id;
    }

    private static string? GuessFundingAccount(ImportedRow row, List<Account> accounts)
    {
        var hint = row.Note ?? string.Empty;
        foreach (var account in accounts)
        {
            if (hint.Contains(account.Name, StringComparison.OrdinalIgnoreCase))
                return account.Id;
        }
        if (hint.Contains("零钱")) return accounts.FirstOrDefault(a => a.Kind == AccountKind.WechatBalance)?.Id;
        if (hint.Contains("余额")) return accounts.FirstOrDefault(a => a.Kind == AccountKind.AlipayBalance)?.Id;
        if (hint.Contains("银行卡") || hint.Contains("储蓄卡") || hint.Contains("信用卡"))
            return accounts.FirstOrDefault(a => a.Kind == AccountKind.BankCard || a.Kind == AccountKind.CreditCard)?.Id;
        return null;
    }

    private static string ExtractKeyword(string merchant)
    {
        var clean = merchant.Split('·')[0].Trim();
        return clean.Length > 12 ? clean[..12] : clean;
    }

    public void IgnoreRow(string rowId) => SetStatus(rowId, ImportRowStatus.Ignored);

    public void DeleteRow(string rowId)
    {
        var row = GetRows().First(r => r.Id == rowId);
        if (!string.IsNullOrEmpty(row.TransactionId))
            _ledger.DeleteTransaction(row.TransactionId);
        SetStatus(rowId, ImportRowStatus.Deleted);
    }

    private void SetStatus(string rowId, ImportRowStatus status)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE import_rows SET status=$s WHERE id=$id";
        cmd.Parameters.AddWithValue("$s", (int)status);
        cmd.Parameters.AddWithValue("$id", rowId);
        cmd.ExecuteNonQuery();
        _ledger.Save();
    }

    public int ConfirmAll(string batchId, bool rememberRules = false)
    {
        var rows = GetRows(batchId, ImportRowStatus.Pending);
        int count = 0;
        foreach (var row in rows)
        {
            try
            {
                ConfirmRow(row.Id, rememberRules);
                count++;
            }
            catch
            {
                // 单条失败不影响整批，保留在待确认列表里。
            }
        }
        return count;
    }

    public int UndoBatch(string batchId)
    {
        var rows = GetRows(batchId).Where(r => r.Status == ImportRowStatus.Confirmed).ToList();
        foreach (var row in rows)
        {
            if (!string.IsNullOrEmpty(row.TransactionId))
                _ledger.DeleteTransaction(row.TransactionId);
            SetStatus(row.Id, ImportRowStatus.Deleted);
        }
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE import_batches SET is_undone=1 WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", batchId);
            cmd.ExecuteNonQuery();
        }
        _ledger.Save();
        return rows.Count;
    }
}
