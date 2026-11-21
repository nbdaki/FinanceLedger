using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FinanceLedger.Core.Models;

namespace FinanceLedger.Core.Services;

public sealed class ImportParseResult
{
    public ImportBatch Batch { get; set; } = new();
    public List<ImportedRow> Rows { get; set; } = [];
    public string? Error { get; set; }
}

public static class CsvImportParser
{
    static CsvImportParser()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static ImportParseResult Parse(string filePath, byte[] bytes)
    {
        var result = new ImportParseResult();
        try
        {
            var text = Decode(bytes);
            var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList();
            var headerIndex = FindHeader(lines);
            if (headerIndex < 0)
            {
                result.Error = "未识别的账单格式：找不到表头行。";
                return result;
            }

            var headers = ParseCsvLine(lines[headerIndex]).Select(h => h.Trim()).ToList();
            var platform = headers.Any(h => h.Contains("交易创建时间")) || headers.Any(h => h.Contains("交易号"))
                ? ImportPlatform.Alipay
                : ImportPlatform.Wechat;

            result.Batch = new ImportBatch
            {
                FileName = Path.GetFileName(filePath),
                Platform = platform,
                ImportedAt = DateTime.Now
            };

            var map = BuildMap(headers, platform);
            for (int i = headerIndex + 1; i < lines.Count; i++)
            {
                var raw = lines[i];
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                var fields = ParseCsvLine(raw);
                var row = MapRow(fields, map, platform, result.Batch.Id, raw);
                if (row is not null)
                    result.Rows.Add(row);
            }
            result.Batch.RowCount = result.Rows.Count;
        }
        catch (Exception ex)
        {
            result.Error = "解析失败：" + ex.Message;
        }
        return result;
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
    }

    private static int FindHeader(List<string> lines)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var isAlipay = line.Contains("交易创建时间") && line.Contains("金额");
            var isWechat = line.Contains("交易时间") && line.Contains("收/支") && line.Contains("金额");
            if (isAlipay || isWechat)
                return i;
        }
        return -1;
    }

    private static Dictionary<string, int> BuildMap(List<string> headers, ImportPlatform platform)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < headers.Count; i++)
        {
            var h = headers[i];
            if (h.Contains("交易创建时间")) map["date"] = i;
            else if (h.Contains("交易时间") && !map.ContainsKey("date")) map["date"] = i;
            else if (h.Contains("付款时间") && !map.ContainsKey("paydate")) map["paydate"] = i;
            else if (h.Contains("交易对方") || h.Contains("对方")) map["party"] = i;
            else if (h.Contains("商品名称") || h == "商品") map["product"] = i;
            else if (h.Contains("金额")) map["amount"] = i;
            else if (h.Contains("收/支") || h.Contains("收支")) map["direction"] = i;
            else if (h.Contains("交易类型") && platform == ImportPlatform.Wechat) map["type"] = i;
            else if (h.Contains("类型") && platform == ImportPlatform.Alipay) map["type"] = i;
            else if (h.Contains("交易状态")) map["state"] = i;
            else if (h.Contains("支付方式") || h.Contains("资金状态")) map["fund"] = i;
            else if (h.Contains("交易单号") || h == "交易号") map["order"] = i;
            else if (h.Contains("商户单号") || h.Contains("商家订单号")) map["merchantOrder"] = i;
            else if (h.Contains("备注")) map["note"] = i;
        }
        return map;
    }

    private static ImportedRow? MapRow(List<string> fields, Dictionary<string, int> map,
        ImportPlatform platform, string batchId, string raw)
    {
        string Get(string key) => map.TryGetValue(key, out var i) && i < fields.Count ? fields[i].Trim() : string.Empty;

        var dateText = Get("date");
        if (!TryParseDate(dateText, out var occurred))
            return null;

        var amountText = Get("amount").Replace("¥", "").Replace("￥", "").Replace(",", "").Trim();
        if (!decimal.TryParse(amountText, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            return null;

        var direction = Get("direction");
        var typeName = Get("type");
        var state = Get("state");
        var note = Get("note");
        var fund = Get("fund");

        var type = direction.Contains("收入") ? TransactionType.Income : TransactionType.Expense;
        if (direction.Contains("不计") || typeName.Contains("转账") || typeName.Contains("红包") || typeName.Contains("退款"))
            type = TransactionType.Transfer;

        var party = Get("party");
        var product = Get("product");
        var merchant = string.IsNullOrWhiteSpace(party) ? product :
            string.IsNullOrWhiteSpace(product) ? party : $"{party} · {product}";

        var order = Get("order");
        var merchantOrder = Get("merchantOrder");
        var dedupeSource = string.IsNullOrWhiteSpace(order) ? $"{occurred:yyyyMMddHHmmss}|{amount}|{merchant}" : order;
        var dedupeKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{platform}|{dedupeSource}")));

        if (!string.IsNullOrWhiteSpace(fund))
            note = string.IsNullOrWhiteSpace(note) ? $"支付方式：{fund}" : $"{note}；支付方式：{fund}";

        return new ImportedRow
        {
            BatchId = batchId,
            RawJson = System.Text.Json.JsonSerializer.Serialize(new { raw, platform = platform.ToString() }),
            OccurredAt = occurred,
            Type = type,
            Channel = platform == ImportPlatform.Alipay ? PaymentChannel.Alipay : PaymentChannel.Wechat,
            Amount = amount,
            Merchant = merchant,
            Note = note,
            DedupeKey = dedupeKey,
            ErrorMessage = state.Contains("失败") || state.Contains("已关闭") ? $"交易状态：{state}" : null
        };
    }

    private static bool TryParseDate(string text, out DateTime date)
    {
        string[] formats =
        [
            "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm",
            "yyyy-MM-dd", "yyyy/MM/dd", "yyyy年M月d日 HH:mm", "yyyy.MM.dd HH:mm:ss"
        ];
        foreach (var f in formats)
            if (DateTime.TryParseExact(text.Trim(), f, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                return true;
        return DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out date);
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                    inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
                current.Append(c);
        }
        result.Add(current.ToString());
        return result;
    }
}
