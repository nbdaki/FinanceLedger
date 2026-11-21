using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FinanceLedger.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AppSession.NoteActivity();
        var service = AppSession.Service;
        if (service is not null)
            InvestmentBox.Text = service.GetInvestmentMarketValue().ToString("0.00");
        if (AppSession.Ledger is not null)
            PathText.Text = $"账簿文件：{AppSession.Ledger.FilePath}";
    }

    private void SaveInvestment_Click(object sender, RoutedEventArgs e)
    {
        var service = AppSession.Service;
        if (service is null || !decimal.TryParse(InvestmentBox.Text, out var value))
        {
            StatusText.Text = "请输入有效金额。";
            return;
        }
        service.SetInvestmentMarketValue(value);
        StatusText.Text = "已保存投资市值";
    }

    private void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        var ledger = AppSession.Ledger;
        if (ledger is null)
            return;
        var old = OldPasswordBox.Password;
        var next = NewPasswordBox.Password;
        if (!ledger.VerifyPassword(old))
        {
            StatusText.Text = "当前密码不正确。";
            return;
        }
        var weak = next.Length == 0 || next.Length < 8;
        if (weak && WeakPasswordAck.IsChecked != true)
        {
            StatusText.Text = "新密码可留空，但需先勾选下方确认。";
            return;
        }
        if (next.Length > 0 && next != ConfirmPasswordBox.Password)
        {
            StatusText.Text = "两次输入的新密码不一致。";
            return;
        }
        ledger.ChangePassword(next);
        OldPasswordBox.Password = string.Empty;
        NewPasswordBox.Password = string.Empty;
        ConfirmPasswordBox.Password = string.Empty;
        WeakPasswordAck.IsChecked = false;
        StatusText.Text = next.Length == 0 ? "已取消密码保护" : "密码已修改";
    }

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        AppSession.Lock();
    }
}
