using FinanceLedger.Core.Models;
using FinanceLedger.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FinanceLedger.App.Views;

public sealed partial class TransactionsPage : Page
{
    public TransactionsPage()
    {
        InitializeComponent();
        TypeCombo.SelectionChanged += TypeCombo_SelectionChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AppSession.NoteActivity();
        LoadOptions();
        RefreshList();
    }

    private void LoadOptions()
    {
        var service = AppSession.Service;
        if (service is null)
            return;

        TypeCombo.ItemsSource = new[]
        {
            new ComboItem { Id = "Expense", Name = "支出" },
            new ComboItem { Id = "Income", Name = "收入" },
            new ComboItem { Id = "Transfer", Name = "转账" },
            new ComboItem { Id = "Repayment", Name = "还款" }
        };
        TypeCombo.SelectedIndex = 0;

        ChannelCombo.ItemsSource = new[]
        {
            new ComboItem { Id = "Alipay", Name = "支付宝" },
            new ComboItem { Id = "Wechat", Name = "微信支付" },
            new ComboItem { Id = "BankCard", Name = "银行卡直付" },
            new ComboItem { Id = "Cash", Name = "现金" },
            new ComboItem { Id = "Other", Name = "其他" }
        };
        ChannelCombo.SelectedIndex = 0;

        var accounts = service.GetAccounts();
        FundingCombo.ItemsSource = accounts.Select(a => new ComboItem { Id = a.Id, Name = a.Name }).ToList();
        WalletCombo.ItemsSource = new[] { new ComboItem { Id = "", Name = "（无）" } }
            .Concat(accounts.Select(a => new ComboItem { Id = a.Id, Name = a.Name })).ToList();
        if (accounts.Count > 0)
            FundingCombo.SelectedIndex = 0;
        WalletCombo.SelectedIndex = 0;
        LoadCategories("Expense");
    }

    private void TypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TypeCombo.SelectedItem is not ComboItem item)
            return;
        LoadCategories(item.Id);
    }

    private void LoadCategories(string filter)
    {
        var service = AppSession.Service;
        if (service is null)
            return;
        var items = service.GetCategories()
            .Where(c => c.Kind == filter)
            .Select(c => new ComboItem { Id = c.Id, Name = c.Name })
            .ToList();
        CategoryCombo.ItemsSource = items;
        CategoryCombo.SelectedIndex = items.Count > 0 ? 0 : -1;
    }

    private void RefreshList()
    {
        var service = AppSession.Service;
        if (service is not null)
            TransactionList.ItemsSource = service.GetRecentTransactions(300);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var service = AppSession.Service;
        if (service is null)
            return;

        if (!decimal.TryParse(AmountBox.Text, out var amount) || amount <= 0)
        {
            StatusText.Text = "请输入有效金额。";
            return;
        }

        var type = Enum.TryParse<TransactionType>((TypeCombo.SelectedItem as ComboItem)?.Id, out var t)
            ? t : TransactionType.Expense;
        var channel = Enum.TryParse<PaymentChannel>((ChannelCombo.SelectedItem as ComboItem)?.Id, out var c)
            ? c : PaymentChannel.Alipay;

        var request = new CreateTransactionRequest
        {
            OccurredAt = DatePicker.Date.DateTime.Date.Add(DateTime.Now.TimeOfDay),
            Type = type,
            Channel = channel,
            FundingAccountId = (FundingCombo.SelectedItem as ComboItem)?.Id,
            WalletAccountId = string.IsNullOrEmpty((WalletCombo.SelectedItem as ComboItem)?.Id)
                ? null : (WalletCombo.SelectedItem as ComboItem)?.Id,
            Amount = amount,
            Merchant = MerchantBox.Text,
            CategoryId = (CategoryCombo.SelectedItem as ComboItem)?.Id,
            Note = NoteBox.Text
        };

        try
        {
            service.CreateTransaction(request);
            AmountBox.Text = string.Empty;
            MerchantBox.Text = string.Empty;
            NoteBox.Text = string.Empty;
            StatusText.Text = "已保存";
            RefreshList();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var service = AppSession.Service;
        if (service is null || TransactionList.SelectedItem is not TransactionListItem item)
        {
            StatusText.Text = "请先选中一条记录。";
            return;
        }
        try
        {
            service.DeleteTransaction(item.Id);
            StatusText.Text = "已删除并冲回账户余额";
            RefreshList();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }
}
