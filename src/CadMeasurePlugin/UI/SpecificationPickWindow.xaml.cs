using System.Windows;
using System.Windows.Controls;
using CadMeasureDomain.Models;

namespace CadMeasurePlugin.UI;

/// <summary>
/// Выбор одной позиции спецификации — для ручной привязки строки журнала.
///
/// Нужен там, где наименования не совпали: проектировщик пишет «Труба ст. Dn80»,
/// а в реестре значится «Труба стальная электросварная Dn80». Автоматически
/// такое не сопоставить, а связать замер с позицией проекта нужно.
/// </summary>
public partial class SpecificationPickWindow : Window
{
    /// <summary>Строка списка: позиция и её представление для чтения.</summary>
    private sealed record Row(SpecificationItem Item, string Display);

    private readonly List<Row> _rows;

    public SpecificationPickWindow(Specification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);

        InitializeComponent();

        _rows = specification.Items
            .Select(i => new Row(i, $"{i.Number}. {i.Name}   [{i.Unit}, {i.Quantity:N2}]"))
            .ToList();

        ItemsList.ItemsSource = _rows;
        Title = $"Позиция спецификации — {specification.FileName}";
    }

    /// <summary>Выбранная позиция, либо null, если окно закрыли отменой.</summary>
    public SpecificationItem? SelectedItem { get; private set; }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();

        ItemsList.ItemsSource = query.Length == 0
            ? _rows
            : _rows.Where(r => r.Display.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private void Items_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Confirm();

    private void Ok_Click(object sender, RoutedEventArgs e) => Confirm();

    private void Confirm()
    {
        if (ItemsList.SelectedItem is not Row row)
        {
            AcadUiHelper.ShowWarning(this, "Позиция не выбрана.");
            return;
        }

        SelectedItem = row.Item;
        DialogResult = true;
    }
}
