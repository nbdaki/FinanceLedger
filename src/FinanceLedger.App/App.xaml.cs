using FinanceLedger.Core.Security;
using FinanceLedger.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;

namespace FinanceLedger.App;

public partial class App : Application
{
    private Window? _window;
    private DispatcherTimer? _idleTimer;

    public static Window? CurrentWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        CurrentWindow = _window;
        _window.Activate();
        MainWindow.Root.Navigate(typeof(Views.LockPage));

        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _idleTimer.Tick += (_, _) => AppSession.CheckIdle();
        _idleTimer.Start();
    }
}

public static class AppSession
{
    public static EncryptedLedger? Ledger { get; private set; }
    public static LedgerService? Service { get; private set; }
    public static ImportService? Imports { get; private set; }
    public static InvestmentService? Investments { get; private set; }
    public static LoanService? Loans { get; private set; }
    public static DateTime LastActivity { get; private set; } = DateTime.Now;

    public static event Action? Locked;

    public static void NoteActivity() => LastActivity = DateTime.Now;

    public static void Unlock(EncryptedLedger ledger)
    {
        Ledger = ledger;
        Service = new LedgerService(ledger);
        Imports = new ImportService(Service);
        Investments = new InvestmentService(Service);
        Loans = new LoanService(Service);
        LastActivity = DateTime.Now;
    }

    public static void Lock()
    {
        Service = null;
        Imports = null;
        Investments = null;
        Loans = null;
        Ledger?.Dispose();
        Ledger = null;
        Locked?.Invoke();
    }

    public static void CheckIdle()
    {
        if (Ledger is not null && DateTime.Now - LastActivity > TimeSpan.FromMinutes(5))
            Lock();
    }
}
