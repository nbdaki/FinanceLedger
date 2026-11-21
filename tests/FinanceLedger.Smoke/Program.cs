using System.Text;
using FinanceLedger.Core.Models;
using FinanceLedger.Core.Security;
using FinanceLedger.Core.Services;

static void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception("FAILED: " + message);
    Console.WriteLine("  OK  " + message);
}

var path = Path.Combine(Path.GetTempPath(), "ledger-smoke-" + Guid.NewGuid().ToString("N") + ".ledger");
Console.WriteLine($"账簿：{path}");

var created = EncryptedLedger.Create(path, "冒烟测试", "SmokeTest-2026!");
Check(!string.IsNullOrWhiteSpace(created.RecoveryCode), "生成恢复码");

var ledger = EncryptedLedger.Open(path);
Check(ledger.TryUnlock("wrong-password", false) == false, "错误密码被拒绝");
Check(ledger.TryUnlock("SmokeTest-2026!", false), "密码解锁成功");

var service = new LedgerService(ledger);
var accounts = service.GetAccounts();
var alipay = accounts.First(a => a.Kind == AccountKind.AlipayBalance);
var wechat = accounts.First(a => a.Kind == AccountKind.WechatBalance);

var bankId = service.AddAccount(new CreateAccountRequest { Name = "招商银行储蓄卡", Kind = AccountKind.BankCard, Balance = 10_000m });
var cardId = service.AddAccount(new CreateAccountRequest { Name = "招行信用卡", Kind = AccountKind.CreditCard, Balance = 0m });
service.UpdateCreditSettings(cardId, 5, 25, 50_000m);

var category = service.GetCategories().First(c => c.Name == "餐饮");

service.CreateTransaction(new CreateTransactionRequest
{
    OccurredAt = DateTime.Today,
    PostedAt = DateTime.Today,
    Type = TransactionType.Expense,
    Channel = PaymentChannel.Alipay,
    FundingAccountId = bankId,
    WalletAccountId = alipay.Id,
    Amount = 128m,
    Merchant = "测试商户",
    CategoryId = category.Id
});

Check(service.GetAccounts().First(a => a.Id == bankId).Balance == 9_872m, "银行卡出资扣减 128");
Check(service.GetAccounts().First(a => a.Id == alipay.Id).Balance == 0m, "支付宝余额一进一出净额为 0");

service.CreateTransaction(new CreateTransactionRequest
{
    OccurredAt = DateTime.Today,
    PostedAt = DateTime.Today,
    Type = TransactionType.Expense,
    Channel = PaymentChannel.Alipay,
    FundingAccountId = cardId,
    WalletAccountId = alipay.Id,
    Amount = 200m,
    Merchant = "信用卡消费",
    CategoryId = category.Id
});

Check(service.GetAccounts().First(a => a.Id == cardId).Balance == 200m, "信用卡负债增加 200");
var cycles = service.GetBillingCycleViews();
Check(cycles.Count == 1 && cycles[0].Payable == 200m, "账单周期归集 200");

var summary = service.GetSummary();
Check(summary.LiquidAssets == 9_872m, $"流动资金 9872（实际 {summary.LiquidAssets}）");
Check(summary.CreditLiabilities == 200m, "信用负债 200");
Check(summary.NetWorth == 9_672m, $"净资产 9672（实际 {summary.NetWorth}）");

ledger.Save();
ledger.Lock();
Check(!ledger.IsUnlocked, "锁定后释放数据库");

var reopened = EncryptedLedger.Open(path);
Check(reopened.TryUnlock("SmokeTest-2026!", false), "重新打开并解锁");
var service2 = new LedgerService(reopened);
Check(service2.GetAccounts().First(a => a.Id == bankId).Balance == 9_872m, "加密保存后余额持久化");
Check(service2.GetBillingCycleViews().Count == 1, "加密保存后账单周期持久化");

var recoveryLedger = EncryptedLedger.Open(path);
Check(recoveryLedger.TryUnlock(created.RecoveryCode, true), "恢复码可解锁");
recoveryLedger.Lock();

var passwordLedger = EncryptedLedger.Open(path);
Check(passwordLedger.TryUnlock("SmokeTest-2026!", false), "密码仍可解锁");
passwordLedger.ChangePassword("NewPassword-2026!");
Check(!passwordLedger.VerifyPassword("SmokeTest-2026!"), "旧密码失效");
Check(passwordLedger.VerifyPassword("NewPassword-2026!"), "新密码生效");
passwordLedger.Lock();

var emptyPath = Path.Combine(Path.GetTempPath(), "ledger-empty-" + Guid.NewGuid().ToString("N") + ".ledger");
var emptyCreated = EncryptedLedger.Create(emptyPath, "空密码账簿", "");
var emptyLedger = EncryptedLedger.Open(emptyPath);
Check(!emptyLedger.TryUnlock("随意密码", false), "空密码账簿拒绝错误密码");
emptyLedger.Lock();
var emptyAgain = EncryptedLedger.Open(emptyPath);
Check(emptyAgain.TryUnlock("", false), "空密码可直接解锁");
emptyAgain.Lock();
Check(!string.IsNullOrWhiteSpace(emptyCreated.RecoveryCode), "空密码账簿仍生成恢复码");
EncryptedLedger.Delete(emptyPath);
Check(!File.Exists(emptyPath), "删除账簿后文件不存在");

var p12Path = Path.Combine(Path.GetTempPath(), "ledger-p12-" + Guid.NewGuid().ToString("N") + ".ledger");
var p12 = EncryptedLedger.Create(p12Path, "P1P2测试", "");
var p12Ledger = EncryptedLedger.Open(p12Path);
p12Ledger.TryUnlock("", false);
var p12Service = new LedgerService(p12Ledger);

var csv = """
微信支付账单明细
微信昵称：[测试]
起始时间：2026-01-01 00:00:00
终止时间：2026-01-31 23:59:59
----------------------
交易时间,交易类型,交易对方,商品,收/支,金额(元),支付方式,交易状态,交易单号,商户单号,备注
2026-01-05 12:30:00,商户消费,星巴克,咖啡,支出,¥28.00,零钱,支付成功,wx001,m001,
2026-01-06 09:00:00,转账,张三,转账,不计收支,¥100.00,零钱,支付成功,wx002,m002,
""";
var csvPath = Path.Combine(Path.GetTempPath(), "wechat-smoke-" + Guid.NewGuid().ToString("N") + ".csv");
File.WriteAllText(csvPath, csv, new UTF8Encoding(true));
var parsed = CsvImportParser.Parse(csvPath, File.ReadAllBytes(csvPath));
Check(parsed.Error is null && parsed.Rows.Count == 2, $"解析微信账单 2 行（实际 {parsed.Rows.Count}）");
Check(parsed.Rows.Any(r => r.Type == TransactionType.Expense), "识别支出");
Check(parsed.Rows.Any(r => r.Type == TransactionType.Transfer), "识别转账不计收支");

var imports = new ImportService(p12Service);
var batch = imports.CreateBatch(parsed);
var confirmed = imports.ConfirmAll(batch.Id);
Check(confirmed == 2, $"批量确认 2 条（实际 {confirmed}）");
Check(p12Service.GetRecentTransactions(50).Count == 2, "确认后生成 2 条交易");
var undone = imports.UndoBatch(batch.Id);
Check(undone == 2 && p12Service.GetRecentTransactions(50).Count == 0, "撤销批次冲回交易");

var investments = new InvestmentService(p12Service);
var securities = p12Service.GetAccounts().First(a => a.Kind == AccountKind.SecuritiesCash);
var holdingId = investments.AddHolding(new Holding
{
    AccountId = securities.Id,
    Code = "600000",
    Name = "浦发银行",
    Quantity = 1000m,
    CostPrice = 8m
});
investments.SetManualPrice(holdingId, 9.5m, DateTime.Today);
Check(investments.GetHoldings().First().MarketValue == 9500m, "持仓市值计算 9500");
Check(p12Service.GetInvestmentMarketValue() == 9500m, "持仓市值写入汇总");
Check(investments.GetHoldings().First().Code == "600000.SH", "证券代码自动补后缀");

var loans = new LoanService(p12Service);
var loan = new Loan
{
    Name = "测试房贷",
    Kind = "Mortgage",
    OriginalPrincipal = 1_000_000m,
    RemainingPrincipal = 1_000_000m,
    AnnualRate = 0.042,
    TermMonths = 360,
    RepaymentMethod = "EqualInstallment",
    PaymentDay = 10
};
p12Service.AddLoan(loan);
var schedule = loans.GenerateSchedule(loan.Id);
Check(schedule.Count > 300 && schedule.First().Interest > schedule.Last().Interest, "等额本息计划生成且利息递减");
Check(Math.Abs(schedule.Sum(p => p.Principal) - 1_000_000m) < 1m, "摊还本金合计等于本金");
var prepayMessage = loans.Prepay(loan.Id, 100_000m, DateTime.Today, true, null);
Check(p12Service.GetLoans().First().RemainingPrincipal == 900_000m, $"提前还款后剩余本金 900000（{prepayMessage}）");
Check(loans.GetPayments(loan.Id).Count < schedule.Count, "缩短期限后期数减少");

p12Ledger.Lock();
Console.WriteLine("SMOKE TEST PASSED");
