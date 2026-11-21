using FinanceLedger.Core.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FinanceLedger.App.Views;

public sealed partial class LockPage : Page
{
    private readonly string _ledgerPath = EncryptedLedger.DefaultLedgerPath();
    private EncryptedLedger? _pendingLedger;

    public LockPage()
    {
        InitializeComponent();

        if (EncryptedLedger.Exists(_ledgerPath))
        {
            ModeTitle.Text = "解锁账簿";
            HintText.Text = "输入账簿密码以继续；如果创建时留空，直接点“解锁”。";
        }
        else
        {
            ModeTitle.Text = "创建账簿";
            HintText.Text = "首次运行，请为本机账簿设置密码。";
            UnlockPanel.Visibility = Visibility.Collapsed;
            CreatePanel.Visibility = Visibility.Visible;
        }
    }

    private void Unlock_Click(object sender, RoutedEventArgs e)
    {
        var secret = PasswordBox.Password;
        var useRecovery = RecoveryToggle.IsChecked == true;
        if (useRecovery && string.IsNullOrWhiteSpace(secret))
        {
            StatusText.Text = "请输入恢复码。";
            return;
        }
        // 密码可以为空，直接尝试解锁即可。

        try
        {
            var ledger = EncryptedLedger.Open(_ledgerPath);
            if (!ledger.TryUnlock(secret, useRecovery))
            {
                StatusText.Text = "密码或恢复码不正确。";
                return;
            }
            AppSession.Unlock(ledger);
            MainWindow.Root.Navigate(typeof(ShellPage));
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var password = NewPasswordBox.Password;
        var weak = password.Length == 0 || password.Length < 8;
        if (weak && WeakPasswordAck.IsChecked != true)
        {
            StatusText.Text = "密码可留空，但需先勾选下方确认；非空密码建议至少 8 位。";
            return;
        }
        if (password.Length > 0 && password != ConfirmPasswordBox.Password)
        {
            StatusText.Text = "两次输入的密码不一致。";
            return;
        }

        try
        {
            var result = EncryptedLedger.Create(_ledgerPath, NameBox.Text.Trim(), password);
            _pendingLedger = result.Ledger;
            RecoveryCodeBox.Text = result.RecoveryCode;
            CreatePanel.Visibility = Visibility.Collapsed;
            RecoveryPanel.Visibility = Visibility.Visible;
            HintText.Text = "账簿已创建。";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async void DeleteLedger_Click(object sender, RoutedEventArgs e)
    {
        if (!EncryptedLedger.Exists(_ledgerPath))
        {
            StatusText.Text = "当前没有可删除的账簿。";
            return;
        }

        if (AppSession.Ledger is not null && AppSession.Ledger.FilePath == _ledgerPath)
            AppSession.Lock();

        var confirmBox = new TextBox
        {
            Header = "输入“删除”以确认",
            PlaceholderText = "删除"
        };
        var dialog = new ContentDialog
        {
            Title = "删除当前账簿？",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "此操作不可撤销。账簿内的全部账户、交易和账单记录都会被永久删除，恢复码也将失效。",
                        TextWrapping = TextWrapping.Wrap
                    },
                    confirmBox
                }
            },
            PrimaryButtonText = "确认删除",
            CloseButtonText = "取消",
            IsPrimaryButtonEnabled = false,
            XamlRoot = XamlRoot
        };
        confirmBox.TextChanged += (_, _) =>
            dialog.IsPrimaryButtonEnabled = confirmBox.Text.Trim() == "删除";

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return;

        try
        {
            EncryptedLedger.Delete(_ledgerPath);
            PasswordBox.Password = string.Empty;
            RecoveryToggle.IsChecked = false;
            UnlockPanel.Visibility = Visibility.Collapsed;
            CreatePanel.Visibility = Visibility.Visible;
            ModeTitle.Text = "创建账簿";
            HintText.Text = "原账簿已删除，请新建账簿。";
            StatusText.Text = "账簿已删除。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "删除失败：" + ex.Message;
        }
    }

    private void Enter_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingLedger is null)
            return;
        AppSession.Unlock(_pendingLedger);
        MainWindow.Root.Navigate(typeof(ShellPage));
    }
}
