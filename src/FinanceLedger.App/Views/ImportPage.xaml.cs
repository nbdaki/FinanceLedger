using FinanceLedger.Core.Models;
using FinanceLedger.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FinanceLedger.App.Views;

public sealed partial class ImportPage : Page
{
    private ImportedRow? _editing;

    public ImportPage()
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
        var imports = AppSession.Imports;
        if (imports is null)
            return;
        RowList.ItemsSource = imports.GetRows();
        var batches = imports.GetBatches();
        BatchCombo.ItemsSource = batches
            .Select(b => new ComboItem { Id = b.Id, Name = $"{b.FileName}  {b.ImportedAt:MM-dd HH:mm}" })
            .ToList();
        if (batches.Count > 0 && BatchCombo.SelectedIndex < 0)
            BatchCombo.SelectedIndex = 0;
    }

    private string? SelectedBatchId => (BatchCombo.SelectedItem as ComboItem)?.Id;

    private ImportedRow? SelectedRow => RowList.SelectedItem as ImportedRow;

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".csv");
            if (App.CurrentWindow is not null)
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.CurrentWindow));
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
                FilePathBox.Text = file.Path;
        }
        catch (Exception ex)
        {
            StatusText.Text = "无法打开文件选择器，请直接粘贴路径：" + ex.Message;
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var imports = AppSession.Imports;
        var path = FilePathBox.Text.Trim();
        if (imports is null || !File.Exists(path))
        {
            StatusText.Text = "请选择存在的 CSV 文件。";
            return;
        }
        try
        {
            var parsed = CsvImportParser.Parse(path, File.ReadAllBytes(path));
            if (!string.IsNullOrEmpty(parsed.Error))
            {
                StatusText.Text = parsed.Error;
                return;
            }
            var batch = imports.CreateBatch(parsed);
            var ignored = parsed.Rows.Count(r => r.Status == ImportRowStatus.Ignored);
            StatusText.Text = $"已导入 {batch.FileName}：{parsed.Rows.Count} 行，重复自动忽略 {ignored} 行。请逐条或批量确认。";
            Refresh();
        }
        catch (Exception ex)
        {
            StatusText.Text = "导入失败：" + ex.Message;
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null || AppSession.Imports is null)
        {
            StatusText.Text = "请先选中一条记录。";
            return;
        }
        try
        {
            AppSession.Imports.ConfirmRow(row.Id, RememberRulesCheck.IsChecked == true);
            StatusText.Text = "已确认入账。";
            Refresh();
        }
        catch (Exception ex)
        {
            StatusText.Text = "确认失败：" + ex.Message;
        }
    }

    private void Ignore_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null || AppSession.Imports is null)
            return;
        AppSession.Imports.IgnoreRow(row.Id);
        StatusText.Text = "已忽略。";
        Refresh();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null || AppSession.Imports is null)
            return;
        AppSession.Imports.DeleteRow(row.Id);
        StatusText.Text = "已删除（若已入账会同步冲回）。";
        Refresh();
    }

    private void ConfirmAll_Click(object sender, RoutedEventArgs e)
    {
        var imports = AppSession.Imports;
        if (imports is null)
            return;
        var batchId = SelectedBatchId;
        if (string.IsNullOrEmpty(batchId))
        {
            StatusText.Text = "请先选择一个导入批次。";
            return;
        }
        var count = imports.ConfirmAll(batchId, RememberRulesCheck.IsChecked == true);
        StatusText.Text = $"已确认 {count} 条。";
        Refresh();
    }

    private void UndoBatch_Click(object sender, RoutedEventArgs e)
    {
        var imports = AppSession.Imports;
        var batchId = SelectedBatchId;
        if (imports is null || string.IsNullOrEmpty(batchId))
        {
            StatusText.Text = "请先选择一个导入批次。";
            return;
        }
        var count = imports.UndoBatch(batchId);
        StatusText.Text = $"已撤销该批次 {count} 条入账记录。";
        Refresh();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        var service = AppSession.Service;
        if (row is null || service is null)
        {
            StatusText.Text = "请先选中一条记录。";
            return;
        }
        _editing = row;
        EditDateBox.Text = row.OccurredAt.ToString("yyyy-MM-dd HH:mm");
        EditAmountBox.Text = row.Amount.ToString("0.00");
        EditMerchantBox.Text = row.Merchant;

        EditTypeCombo.ItemsSource = new[]
        {
            new ComboItem { Id = "Expense", Name = "支出" },
            new ComboItem { Id = "Income", Name = "收入" },
            new ComboItem { Id = "Transfer", Name = "转账" },
            new ComboItem { Id = "Repayment", Name = "还款" }
        };
        EditTypeCombo.SelectedItem = (EditTypeCombo.ItemsSource as IEnumerable<ComboItem>)?
            .FirstOrDefault(i => i.Id == row.Type.ToString());

        EditCategoryCombo.ItemsSource = service.GetCategories()
            .Select(c => new ComboItem { Id = c.Id, Name = c.Name }).ToList();
        EditCategoryCombo.SelectedItem = (EditCategoryCombo.ItemsSource as IEnumerable<ComboItem>)?
            .FirstOrDefault(i => i.Id == row.CategoryId);

        var accounts = service.GetAccounts();
        var accountItems = accounts.Select(a => new ComboItem { Id = a.Id, Name = a.Name }).ToList();
        EditFundingCombo.ItemsSource = accountItems;
        var walletItems = new[] { new ComboItem { Id = "", Name = "（无）" } }.Concat(accountItems).ToList();
        EditWalletCombo.ItemsSource = walletItems;
        EditFundingCombo.SelectedItem = accountItems.FirstOrDefault(i => i.Id == row.FundingAccountId);
        EditWalletCombo.SelectedItem = walletItems.FirstOrDefault(i => i.Id == row.WalletAccountId) ?? walletItems[0];

        EditPanel.Visibility = Visibility.Visible;
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e)
    {
        _editing = null;
        EditPanel.Visibility = Visibility.Collapsed;
    }

    private void SaveEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null || AppSession.Imports is null)
            return;
        if (!DateTime.TryParse(EditDateBox.Text, out var date) ||
            !decimal.TryParse(EditAmountBox.Text, out var amount))
        {
            StatusText.Text = "日期或金额格式不正确。";
            return;
        }
        _editing.OccurredAt = date;
        _editing.Amount = amount;
        _editing.Merchant = EditMerchantBox.Text;
        _editing.Type = Enum.TryParse<TransactionType>((EditTypeCombo.SelectedItem as ComboItem)?.Id, out var t)
            ? t : _editing.Type;
        _editing.CategoryId = (EditCategoryCombo.SelectedItem as ComboItem)?.Id;
        _editing.FundingAccountId = (EditFundingCombo.SelectedItem as ComboItem)?.Id;
        var wallet = (EditWalletCombo.SelectedItem as ComboItem)?.Id;
        _editing.WalletAccountId = string.IsNullOrEmpty(wallet) ? null : wallet;

        AppSession.Imports.UpdateRow(_editing);
        StatusText.Text = "已保存修改。";
        CancelEdit_Click(sender, e);
        Refresh();
    }
}
