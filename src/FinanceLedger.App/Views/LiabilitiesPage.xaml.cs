using FinanceLedger.Core.Models;
using FinanceLedger.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FinanceLedger.App.Views;

public sealed partial class LiabilitiesPage : Page
{
    public LiabilitiesPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AppSession.NoteActivity();
        if (KindCombo.ItemsSource is null)
        {
            KindCombo.ItemsSource = new[]
            {
                new ComboItem { Id = "Mortgage", Name = "房贷" },
                new ComboItem { Id = "Auto", Name = "车贷" },
                new ComboItem { Id = "Other", Name = "其他" }
            };
            KindCombo.SelectedIndex = 0;
        }
        Refresh();
    }

    private void Refresh()
    {
        var service = AppSession.Service;
        if (service is null)
            return;
        var loans = service.GetLoans();
        var items = loans.Select(l => new ComboItem { Id = l.Id, Name = l.Name }).ToList();
        LoanList.ItemsSource = loans;
        ChooseCombo.ItemsSource = items;
        PlanLoanCombo.ItemsSource = items;
        if (loans.Count > 0 && ChooseCombo.SelectedIndex < 0)
            ChooseCombo.SelectedIndex = 0;
        if (loans.Count > 0 && PlanLoanCombo.SelectedIndex < 0)
            PlanLoanCombo.SelectedIndex = 0;

        var funding = service.GetAccounts()
            .Where(a => !FinanceLedger.Core.Models.DomainNames.IsLiability(a.Kind))
            .Select(a => new ComboItem { Id = a.Id, Name = a.Name }).ToList();
        PrepayFundingCombo.ItemsSource = funding;
        if (funding.Count > 0 && PrepayFundingCombo.SelectedIndex < 0)
            PrepayFundingCombo.SelectedIndex = 0;

        LoadSchedule();
    }

    private void PlanLoanCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadSchedule();

    private void LoadSchedule()
    {
        var loanId = (PlanLoanCombo.SelectedItem as ComboItem)?.Id;
        if (string.IsNullOrEmpty(loanId) || AppSession.Loans is null)
        {
            PaymentList.ItemsSource = new List<LoanPayment>();
            return;
        }
        PaymentList.ItemsSource = AppSession.Loans.GetPayments(loanId);
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        var loanId = (PlanLoanCombo.SelectedItem as ComboItem)?.Id;
        if (string.IsNullOrEmpty(loanId) || AppSession.Loans is null)
        {
            PlanStatusText.Text = "请先选择贷款。";
            return;
        }
        var schedule = AppSession.Loans.GenerateSchedule(loanId);
        PlanStatusText.Text = $"已生成 {schedule.Count} 期还款计划。";
        LoadSchedule();
    }

    private void Prepay_Click(object sender, RoutedEventArgs e)
    {
        var loanId = (PlanLoanCombo.SelectedItem as ComboItem)?.Id;
        if (string.IsNullOrEmpty(loanId) || AppSession.Loans is null ||
            !decimal.TryParse(PrepayAmountBox.Text, out var amount))
        {
            PlanStatusText.Text = "请选择贷款并输入有效金额。";
            return;
        }
        try
        {
            var message = AppSession.Loans.Prepay(
                loanId,
                amount,
                PrepayDatePicker.Date.DateTime.Date,
                ReduceTermCheck.IsChecked == true,
                (PrepayFundingCombo.SelectedItem as ComboItem)?.Id);
            PlanStatusText.Text = message;
            PrepayAmountBox.Text = string.Empty;
            Refresh();
        }
        catch (Exception ex)
        {
            PlanStatusText.Text = "提前还款失败：" + ex.Message;
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var service = AppSession.Service;
        if (service is null || string.IsNullOrWhiteSpace(NameBox.Text))
        {
            StatusText.Text = "请输入贷款名称。";
            return;
        }
        decimal.TryParse(PrincipalBox.Text, out var principal);
        decimal.TryParse(RemainingBox.Text, out var remaining);
        decimal.TryParse(RateBox.Text, out var rate);
        decimal.TryParse(MonthlyBox.Text, out var monthly);
        int.TryParse(TermBox.Text, out var term);
        int.TryParse(PaymentDayBox.Text, out var day);

        service.AddLoan(new Loan
        {
            Name = NameBox.Text,
            Kind = (KindCombo.SelectedItem as ComboItem)?.Id ?? "Other",
            OriginalPrincipal = principal,
            RemainingPrincipal = remaining,
            AnnualRate = (double)rate / 100.0,
            TermMonths = term,
            MonthlyPayment = monthly,
            PaymentDay = day == 0 ? 10 : Math.Clamp(day, 1, 28)
        });
        NameBox.Text = string.Empty;
        StatusText.Text = "已添加";
        Refresh();
    }

    private void Update_Click(object sender, RoutedEventArgs e)
    {
        var service = AppSession.Service;
        var id = (ChooseCombo.SelectedItem as ComboItem)?.Id;
        if (service is null || string.IsNullOrEmpty(id) || !decimal.TryParse(NewRemainingBox.Text, out var remaining))
        {
            StatusText.Text = "请选择贷款并输入有效剩余本金。";
            return;
        }
        service.UpdateLoanRemaining(id, remaining);
        NewRemainingBox.Text = string.Empty;
        StatusText.Text = "已更新";
        Refresh();
    }
}
