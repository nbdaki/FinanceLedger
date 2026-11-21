using System.Globalization;
using System.Text;
using System.Text.Json;
using FinanceLedger.Core.Models;

namespace FinanceLedger.Core.Services;

public sealed class TushareClient
{
    private const string Endpoint = "https://api.tushare.pro";
    private readonly string _token;
    private readonly HttpClient _http;

    public TushareClient(string token)
    {
        _token = token;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<List<PriceRecord>> FetchAsync(string tsCode, string market, DateTime start, DateTime end)
    {
        var apiName = market == "ETF" ? "fund_daily" : "daily";
        var payload = new
        {
            api_name = apiName,
            token = _token,
            @params = new
            {
                ts_code = tsCode,
                start_date = start.ToString("yyyyMMdd"),
                end_date = end.ToString("yyyyMMdd")
            },
            fields = "ts_code,trade_date,close"
        };
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(Endpoint, content);
        var body = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var code = root.GetProperty("code").GetInt32();
        if (code != 0)
            throw new InvalidOperationException(root.TryGetProperty("msg", out var msg) ? msg.GetString() ?? "接口错误" : "接口错误");

        var data = root.GetProperty("data");
        var fields = data.GetProperty("fields").EnumerateArray().Select(f => f.GetString() ?? string.Empty).ToList();
        var dateIndex = fields.IndexOf("trade_date");
        var closeIndex = fields.IndexOf("close");
        if (dateIndex < 0 || closeIndex < 0)
            throw new InvalidOperationException("返回字段不完整。");

        var result = new List<PriceRecord>();
        foreach (var item in data.GetProperty("items").EnumerateArray())
        {
            var arr = item.EnumerateArray().ToArray();
            if (dateIndex >= arr.Length || closeIndex >= arr.Length)
                continue;
            var dateText = arr[dateIndex].GetString() ?? string.Empty;
            if (!DateTime.TryParseExact(dateText, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;
            var closeElement = arr[closeIndex];
            var closeText = closeElement.ValueKind == JsonValueKind.String
                ? closeElement.GetString() ?? string.Empty
                : closeElement.ToString();
            if (!decimal.TryParse(closeText, NumberStyles.Any, CultureInfo.InvariantCulture, out var close))
                continue;
            result.Add(new PriceRecord { Code = tsCode, TradeDate = date, Close = close, Source = "Tushare" });
        }
        return result;
    }
}
