namespace FinanceLedger.App.Views;

public sealed class ComboItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public override string ToString() => Name;
}
