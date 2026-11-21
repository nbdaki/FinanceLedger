using FinanceLedger.Core.Models;
using FinanceLedger.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FinanceLedger.App.Views;

public sealed partial class BillingPage : Page
{
    public BillingPage()
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

        var cards = service.GetAccounts()
            .Where(a => a.Kind is AccountKind.CreditCard or AccountKind.Huabei)
            .ToList();
        CardCombo.ItemsSource = cards.Select(a => new ComboItem { Id = a.Id, Name = a.Name }).ToList();
        if (cards.Count > 0 && CardCombo.SelectedIndex < 0)
            CardCombo.SelectedIndex = 0;

        var cycles = service.GetBillingCycleViews();
        CycleList.ItemsSource = cycles;
        CycleCombo.ItemsSource = cycles
            .Select(c => new ComboItem { Id = c.Id, Name = $"{c.AccountName}  {c.PeriodText}" })
            .ToList();
        if (cycles.Count > 0)
            CycleCombo.SelectedIndex = 0;
    }

    private void CardCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var service = AppSession.Service;
        var id = (CardCombo.SelectedItem as ComboItem)?.Id;
        if (service is null || string.IsNullOrEmpty(id))
            return;
        var settings = service.GetCreditSettings(id);
        StatementDayBox.Text = settings.StatementDay.ToString();
        DueDayBox.Text = settings.DueDay.ToString();
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var service = AppSession.Service;
        var id = (CardCombo.SelectedItem as ComboItem)?.Id;
        if (service is null || string.IsNullOrEmpty(id) ||
            !int.TryParse(StatementDayBox.Text, out var statementDay) ||
            !int.TryParse(DueDayBox.Text, out var dueDay))
        {
            StatusText.Text = "请选择账户并输入有效的账单日、还款日。";
            return;
        }
        service.UpdateCreditSettings(id, statementDay, dueDay, 0);
        StatusText.Text = "已保存";
    }

    private void UpdateAdjust_Click(object sender, RoutedEventArgs e)
    {
        var service = AppSession.Service;
        var id = (CycleCombo.SelectedItem as ComboItem)?.Id;
        if (service is null || string.IsNullOrEmpty(id) || !decimal.TryParse(AdjustmentBox.Text, out var adjustment))
        {
            StatusText.Text = "请选择周期并输入有效金额。";
            return;
        }
        service.UpdateCycleAdjustment(id, adjustment);
        AdjustmentBox.Text = string.Empty;
        StatusText.Text = "已更新";
        Refresh();
    }
}
