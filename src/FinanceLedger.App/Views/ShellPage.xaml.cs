using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FinanceLedger.App.Views;

public sealed partial class ShellPage : Page
{
    private bool _ready;

    public ShellPage()
    {
        InitializeComponent();
        AppSession.Locked += OnLocked;
        Loaded += ShellPage_Loaded;
    }

    private void ShellPage_Loaded(object sender, RoutedEventArgs e)
    {
        _ready = true;
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    private void OnLocked()
    {
        AppSession.Locked -= OnLocked;
        MainWindow.Root.Navigate(typeof(LockPage));
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_ready || args.SelectedItem is not NavigationViewItem item)
            return;

        AppSession.NoteActivity();
        var tag = item.Tag?.ToString();
        Type? target = tag switch
        {
            "Dashboard" => typeof(DashboardPage),
            "Transactions" => typeof(TransactionsPage),
            "Import" => typeof(ImportPage),
            "Accounts" => typeof(AccountsPage),
            "Investments" => typeof(InvestmentsPage),
            "Liabilities" => typeof(LiabilitiesPage),
            "Billing" => typeof(BillingPage),
            "Settings" => typeof(SettingsPage),
            "Lock" => typeof(LockPage),
            _ => null
        };

        if (target == typeof(LockPage))
        {
            // AppSession.Lock() 会触发 Locked 事件，由 OnLocked 导航回锁屏首页。
            // 不要在这里退订，否则只会清空会话而不切换页面。
            AppSession.Lock();
            return;
        }

        if (target is not null)
            ContentFrame.Navigate(target);
    }
}
