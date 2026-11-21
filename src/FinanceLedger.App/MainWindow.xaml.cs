using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FinanceLedger.App;

public sealed partial class MainWindow : Window
{
    public static Frame Root { get; private set; } = null!;

    public MainWindow()
    {
        InitializeComponent();
        Root = RootFrame;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 760));
    }
}
