using FinanceLedger.Core.Models;
using FinanceLedger.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FinanceLedger.App.Views;

public sealed partial class AccountsPage : Page
{
    public AccountsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AppSession.NoteActivity();
        LoadOptions();
        Refresh();
    }

    private void LoadOptions()
    {
        if (KindCombo.ItemsSource is null)
        {
            KindCombo.ItemsSource = Enum.GetValues<AccountKind>()
                .Select(k => new ComboItem { Id = k.ToString(), Name = DomainNames.Chinese(k) })
                .ToList();
            KindCombo.SelectedIndex = 0;

            WalletCombo.ItemsSource = new[]
            {
                new ComboItem { Id = "None", Name = "（无）" },
                new ComboItem { Id = "Alipay", Name = "支付宝" },
                new ComboItem { Id = "Wechat", Name = "微信" }
            };
            WalletCombo.SelectedIndex = 0;
        }

        var accounts = AppSession.Service?.GetAccounts() ?? [];
        ChooseCombo.ItemsSource = accounts.Select(a => new ComboItem { Id = a.Id, Name = a.Name }).ToList();
        if (accounts.Count > 0 && ChooseCombo.SelectedIndex < 0)
            ChooseCombo.SelectedIndex = 0;
    }

    private void Refresh()
    {
        var service = AppSession.Service;
        if (service is not null)
            AccountList.ItemsSource = service.GetAccountItems();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var service = AppSession.Service;
        if (service is null)
            return;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            StatusText.Text = "请输入账户名称。";
            return;
        }
        decimal.TryParse(BalanceBox.Text, out var balance);

        var kind = Enum.TryParse<AccountKind>((KindCombo.SelectedItem as ComboItem)?.Id, out var k)
            ? k : AccountKind.BankCard;
        var wallet = Enum.TryParse<WalletKind>((WalletCombo.SelectedItem as ComboItem)?.Id, out var w)
            ? w : WalletKind.None;

        try
        {
            service.AddAccount(new CreateAccountRequest
            {
                Name = NameBox.Text,
                Kind = kind,
                Wallet = wallet,
                Balance = balance
            });
            NameBox.Text = string.Empty;
            BalanceBox.Text = string.Empty;
            StatusText.Text = "已添加";
            LoadOptions();
            Refresh();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void Adjust_Click(object sender, RoutedEventArgs e)
    {
        var service = AppSession.Service;
        var id = (ChooseCombo.SelectedItem as ComboItem)?.Id;
        if (service is null || string.IsNullOrEmpty(id) || !decimal.TryParse(NewBalanceBox.Text, out var balance))
        {
            StatusText.Text = "请选择账户并输入有效余额。";
            return;
        }
        service.UpdateAccountBalance(id, balance);
        NewBalanceBox.Text = string.Empty;
        StatusText.Text = "已校准";
        Refresh();
    }
}
