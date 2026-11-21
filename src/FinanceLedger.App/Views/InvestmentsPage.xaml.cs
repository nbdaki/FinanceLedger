using FinanceLedger.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FinanceLedger.App.Views;

public sealed partial class InvestmentsPage : Page
{
    private string? _editingId;

    public InvestmentsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AppSession.NoteActivity();
        var service = AppSession.Investments;
        var ledger = AppSession.Service;
        if (service is null || ledger is null)
            return;

        TokenBox.Text = service.GetToken();
        if (MarketCombo.ItemsSource is null)
        {
            MarketCombo.ItemsSource = new[]
            {
                new ComboItem { Id = "A股", Name = "A股" },
                new ComboItem { Id = "ETF", Name = "ETF / 场内基金" }
            };
            MarketCombo.SelectedIndex = 0;
        }
        var accounts = ledger.GetAccounts().Where(a => a.Kind == AccountKind.SecuritiesCash).ToList();
        AccountCombo.ItemsSource = accounts.Select(a => new ComboItem { Id = a.Id, Name = a.Name }).ToList();
        if (accounts.Count > 0 && AccountCombo.SelectedIndex < 0)
            AccountCombo.SelectedIndex = 0;
        Refresh();
    }

    private void Refresh()
    {
        if (AppSession.Investments is not null)
            HoldingList.ItemsSource = AppSession.Investments.GetHoldings();
    }

    private void SaveToken_Click(object sender, RoutedEventArgs e)
    {
        AppSession.Investments?.SetToken(TokenBox.Text);
        StatusText.Text = "Token 已保存到加密账簿。";
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var service = AppSession.Investments;
        var ledger = AppSession.Service;
        if (service is null || ledger is null)
            return;
        if (!decimal.TryParse(QuantityBox.Text, out var quantity) || quantity <= 0 ||
            !decimal.TryParse(CostBox.Text, out var cost))
        {
            StatusText.Text = "请填写有效数量和成本价。";
            return;
        }
        var accountId = (AccountCombo.SelectedItem as ComboItem)?.Id ??
                        ledger.GetAccounts().First(a => a.Kind == AccountKind.SecuritiesCash).Id;
        var price = decimal.TryParse(PriceBox.Text, out var p) ? p : 0m;

        if (_editingId is not null)
        {
            var holding = service.GetHoldings().First(h => h.Id == _editingId);
            holding.Code = CodeBox.Text;
            holding.Name = NameBox.Text;
            holding.Market = (MarketCombo.SelectedItem as ComboItem)?.Id ?? "A股";
            holding.AccountId = accountId;
            holding.Quantity = quantity;
            holding.CostPrice = cost;
            service.UpdateHolding(holding);
            if (price > 0)
                service.SetManualPrice(holding.Id, price, DateTime.Today);
            StatusText.Text = "已更新持仓。";
        }
        else
        {
            service.AddHolding(new Holding
            {
                AccountId = accountId,
                Code = CodeBox.Text,
                Name = NameBox.Text,
                Market = (MarketCombo.SelectedItem as ComboItem)?.Id ?? "A股",
                Quantity = quantity,
                CostPrice = cost,
                CurrentPrice = price,
                PriceDate = price > 0 ? DateTime.Today : null,
                PriceSource = price > 0 ? "手工" : "未更新"
            });
            StatusText.Text = "已添加持仓。";
        }
        ClearForm();
        Refresh();
    }

    private void Select_Click(object sender, RoutedEventArgs e)
    {
        if (HoldingList.SelectedItem is not Holding holding)
        {
            StatusText.Text = "请先选中一条持仓。";
            return;
        }
        _editingId = holding.Id;
        CodeBox.Text = holding.Code;
        NameBox.Text = holding.Name;
        QuantityBox.Text = holding.Quantity.ToString("0.##");
        CostBox.Text = holding.CostPrice.ToString("0.000");
        PriceBox.Text = holding.CurrentPrice > 0 ? holding.CurrentPrice.ToString("0.000") : string.Empty;
        MarketCombo.SelectedItem = (MarketCombo.ItemsSource as IEnumerable<ComboItem>)?
            .FirstOrDefault(i => i.Id == holding.Market);
        AccountCombo.SelectedItem = (AccountCombo.ItemsSource as IEnumerable<ComboItem>)?
            .FirstOrDefault(i => i.Id == holding.AccountId);
        StatusText.Text = "已载入选中记录，修改后点“添加/保存”。";
    }

    private void SavePrice_Click(object sender, RoutedEventArgs e)
    {
        if (HoldingList.SelectedItem is not Holding holding ||
            !decimal.TryParse(PriceBox.Text, out var price))
        {
            StatusText.Text = "请选中持仓并填写现价。";
            return;
        }
        AppSession.Investments?.SetManualPrice(holding.Id, price, DateTime.Today);
        StatusText.Text = "已手工更新收盘价。";
        Refresh();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (HoldingList.SelectedItem is not Holding holding)
            return;
        AppSession.Investments?.DeleteHolding(holding.Id);
        StatusText.Text = "已删除持仓。";
        ClearForm();
        Refresh();
    }

    private async void UpdatePrices_Click(object sender, RoutedEventArgs e)
    {
        var service = AppSession.Investments;
        if (service is null)
            return;
        UpdatePricesButton.IsEnabled = false;
        try
        {
            var message = await service.UpdatePricesAsync(p => StatusText.Text = p);
            StatusText.Text = message;
        }
        finally
        {
            UpdatePricesButton.IsEnabled = true;
            Refresh();
        }
    }

    private void ClearForm()
    {
        _editingId = null;
        CodeBox.Text = string.Empty;
        NameBox.Text = string.Empty;
        QuantityBox.Text = string.Empty;
        CostBox.Text = string.Empty;
        PriceBox.Text = string.Empty;
    }
}
