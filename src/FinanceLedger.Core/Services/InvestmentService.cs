using FinanceLedger.Core.Data;
using FinanceLedger.Core.Models;
using FinanceLedger.Core.Security;
using Microsoft.Data.Sqlite;

namespace FinanceLedger.Core.Services;

public sealed class InvestmentService
{
    private readonly EncryptedLedger _ledger;
    private readonly LedgerService _service;
    private readonly LedgerDatabase _db;

    public InvestmentService(LedgerService service)
    {
        _service = service;
        _ledger = service.GetEncryptedLedger();
        _db = service.Database;
    }

    public string GetToken() => _ledger.GetMeta("tushare_token") ?? string.Empty;

    public void SetToken(string token)
    {
        _ledger.SetMeta("tushare_token", token.Trim());
        _ledger.Save();
    }

    public static string NormalizeCode(string code)
    {
        var c = code.Trim().ToUpperInvariant();
        if (c.Contains('.'))
            return c;
        if (c.StartsWith('6'))
            return c + ".SH";
        if (c.StartsWith('0') || c.StartsWith('3'))
            return c + ".SZ";
        if (c.StartsWith("51") || c.StartsWith("15") || c.StartsWith("56"))
            return c + ".SH";
        return c;
    }

    public List<Holding> GetHoldings()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT id,account_id,code,name,market,quantity,cost_price,currency,price_date,current_price,price_source
            FROM holdings ORDER BY code
            """;
        var list = new List<Holding>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new Holding
            {
                Id = reader.GetString(0),
                AccountId = reader.GetString(1),
                Code = reader.GetString(2),
                Name = reader.GetString(3),
                Market = reader.GetString(4),
                Quantity = reader.GetDouble(5).ToDecimal(),
                CostPrice = reader.GetDouble(6).ToDecimal(),
                Currency = reader.GetString(7),
                PriceDate = reader.IsDBNull(8) ? null : DateTime.Parse(reader.GetString(8)),
                CurrentPrice = reader.GetDouble(9).ToDecimal(),
                PriceSource = reader.GetString(10)
            });
        return list;
    }

    public string AddHolding(Holding holding)
    {
        holding.Code = NormalizeCode(holding.Code);
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO holdings(id,account_id,code,name,market,quantity,cost_price,currency,price_date,current_price,price_source)
                VALUES($id,$account,$code,$name,$market,$qty,$cost,$currency,$date,$price,$source)
                """;
            cmd.Parameters.AddWithValue("$id", holding.Id);
            cmd.Parameters.AddWithValue("$account", holding.AccountId);
            cmd.Parameters.AddWithValue("$code", holding.Code);
            cmd.Parameters.AddWithValue("$name", holding.Name);
            cmd.Parameters.AddWithValue("$market", holding.Market);
            cmd.Parameters.AddWithValue("$qty", (double)holding.Quantity);
            cmd.Parameters.AddWithValue("$cost", (double)holding.CostPrice);
            cmd.Parameters.AddWithValue("$currency", holding.Currency);
            cmd.Parameters.AddWithValue("$date", (object?)holding.PriceDate?.ToString("yyyy-MM-dd") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$price", (double)holding.CurrentPrice);
            cmd.Parameters.AddWithValue("$source", holding.PriceSource);
            cmd.ExecuteNonQuery();
        }
        RecomputeTotal();
        return holding.Id;
    }

    public void UpdateHolding(Holding holding)
    {
        holding.Code = NormalizeCode(holding.Code);
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            UPDATE holdings SET account_id=$account,code=$code,name=$name,market=$market,quantity=$qty,
                cost_price=$cost,currency=$currency WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$account", holding.AccountId);
        cmd.Parameters.AddWithValue("$code", holding.Code);
        cmd.Parameters.AddWithValue("$name", holding.Name);
        cmd.Parameters.AddWithValue("$market", holding.Market);
        cmd.Parameters.AddWithValue("$qty", (double)holding.Quantity);
        cmd.Parameters.AddWithValue("$cost", (double)holding.CostPrice);
        cmd.Parameters.AddWithValue("$currency", holding.Currency);
        cmd.Parameters.AddWithValue("$id", holding.Id);
        cmd.ExecuteNonQuery();
        RecomputeTotal();
    }

    public void DeleteHolding(string id)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM holdings WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        RecomputeTotal();
    }

    public void SetManualPrice(string id, decimal price, DateTime date)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE holdings SET current_price=$price,price_date=$date,price_source='手工' WHERE id=$id";
        cmd.Parameters.AddWithValue("$price", (double)price);
        cmd.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        UpsertPrice(id, new PriceRecord { Code = GetHoldings().First(h => h.Id == id).Code, TradeDate = date, Close = price, Source = "手工" });
        RecomputeTotal();
    }

    public List<PriceRecord> GetPriceHistory(string code)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT code,trade_date,close,adj_factor,source FROM price_cache WHERE code=$code ORDER BY trade_date DESC LIMIT 60";
        cmd.Parameters.AddWithValue("$code", NormalizeCode(code));
        var list = new List<PriceRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new PriceRecord
            {
                Code = reader.GetString(0),
                TradeDate = DateTime.Parse(reader.GetString(1)),
                Close = reader.GetDouble(2).ToDecimal(),
                AdjFactor = reader.GetDouble(3).ToDecimal(),
                Source = reader.GetString(4)
            });
        return list;
    }

    public async Task<string> UpdatePricesAsync(Action<string>? progress = null)
    {
        var token = GetToken();
        if (string.IsNullOrWhiteSpace(token))
            return "请先在投资页填写 Tushare token。";

        var client = new TushareClient(token);
        var messages = new List<string>();
        var holdings = GetHoldings();
        if (holdings.Count == 0)
            return "还没有持仓记录。";

        foreach (var holding in holdings)
        {
            try
            {
                progress?.Invoke($"正在更新 {holding.Code} ...");
                var last = GetPriceHistory(holding.Code).FirstOrDefault();
                var start = last?.TradeDate.AddDays(1) ?? DateTime.Today.AddMonths(-3);
                if (start > DateTime.Today)
                {
                    messages.Add($"{holding.Code} 已是最新");
                    continue;
                }
                var records = await client.FetchAsync(holding.Code, holding.Market, start, DateTime.Today);
                foreach (var record in records)
                    UpsertPrice(holding.Id, record);

                var latest = records.OrderByDescending(r => r.TradeDate).FirstOrDefault();
                if (latest is not null)
                {
                    using var cmd = _db.Connection.CreateCommand();
                    cmd.CommandText = "UPDATE holdings SET current_price=$price,price_date=$date,price_source='Tushare' WHERE id=$id";
                    cmd.Parameters.AddWithValue("$price", (double)latest.Close);
                    cmd.Parameters.AddWithValue("$date", latest.TradeDate.ToString("yyyy-MM-dd"));
                    cmd.Parameters.AddWithValue("$id", holding.Id);
                    cmd.ExecuteNonQuery();
                    messages.Add($"{holding.Code} 收盘 {latest.Close:N3}");
                }
                else
                    messages.Add($"{holding.Code} 无新行情");
            }
            catch (Exception ex)
            {
                messages.Add($"{holding.Code} 失败：{ex.Message}");
            }
            await Task.Delay(250);
        }

        RecomputeTotal();
        WriteSnapshot();
        progress?.Invoke("更新完成");
        return string.Join("\n", messages);
    }

    private void UpsertPrice(string holdingId, PriceRecord record)
    {
        var code = GetHoldings().FirstOrDefault(h => h.Id == holdingId)?.Code ?? record.Code;
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO price_cache(code,trade_date,close,adj_factor,source)
            VALUES($code,$date,$close,$adj,$source)
            ON CONFLICT(code,trade_date) DO UPDATE SET close=$close, source=$source
            """;
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$date", record.TradeDate.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$close", (double)record.Close);
        cmd.Parameters.AddWithValue("$adj", (double)record.AdjFactor);
        cmd.Parameters.AddWithValue("$source", record.Source);
        cmd.ExecuteNonQuery();
    }

    public void RecomputeTotal()
    {
        var total = GetHoldings().Where(h => h.Currency == "CNY").Sum(h => h.MarketValue);
        _ledger.SetMeta("investment_market_value", total.ToString("0.00"));
        _ledger.Save();
    }

    public void WriteSnapshot()
    {
        var summary = _service.GetSummary();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO snapshots(id,date,liquid,investments,liabilities,net_worth)
            VALUES($id,$date,$liquid,$invest,$liab,$net)
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("$date", DateTime.Today.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$liquid", (double)summary.LiquidAssets);
        cmd.Parameters.AddWithValue("$invest", (double)summary.InvestmentAssets);
        cmd.Parameters.AddWithValue("$liab", (double)summary.TotalLiabilities);
        cmd.Parameters.AddWithValue("$net", (double)summary.NetWorth);
        cmd.ExecuteNonQuery();
        _ledger.Save();
    }
}
