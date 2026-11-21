using FinanceLedger.Core.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FinanceLedger.App.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AppSession.NoteActivity();
        Refresh();
    }

    private void Refresh()
    {
        var service = AppSession.Service;
        if (service is null)
            return;

        var summary = service.GetSummary();
        NetWorthText.Text = Money(summary.NetWorth);
        TotalText.Text = $"总资产 {Money(summary.TotalAssets)}    总负债 {Money(summary.TotalLiabilities)}";
        IncomeText.Text = Money(summary.MonthIncome);
        ExpenseText.Text = Money(summary.MonthExpense);
        BalanceText.Text = Money(summary.MonthBalance);

        AssetList.ItemsSource = new[]
        {
            $"流动资金（银行卡、余额、零钱等）    {Money(summary.LiquidAssets)}",
            $"投资市值（手工更新）                {Money(summary.InvestmentAssets)}",
            $"信用卡与花呗欠款                    {Money(summary.CreditLiabilities)}",
            $"房贷与车贷剩余本金                  {Money(summary.LoanLiabilities)}"
        };

        RecentList.ItemsSource = service.GetRecentTransactions(12);

        var due = new List<string>();
        var today = DateTime.Today;
        foreach (var cycle in service.GetBillingCycleViews())
        {
            if (DateTime.TryParse(cycle.DueText, out var date) && date >= today && date <= today.AddDays(60))
                due.Add($"{cycle.DueText}  {cycle.AccountName} 账单应还 {Money(cycle.Payable)}");
        }
        var loans = service.GetLoans().ToDictionary(l => l.Id);
        var payments = AppSession.Loans?.GetUpcoming(60) ?? [];
        foreach (var payment in payments.OrderBy(p => p.DueDate))
        {
            var name = loans.TryGetValue(payment.LoanId, out var loan) ? loan.Name : "贷款";
            due.Add($"{payment.DueText}  {name} 第 {payment.Period} 期  {Money(payment.Payment)}");
        }
        DueList.ItemsSource = due.Count == 0 ? new[] { "未来 60 天暂无还款计划" } : due.ToArray();
    }

    private static string Money(decimal value) => $"¥{value:N2}";
}
