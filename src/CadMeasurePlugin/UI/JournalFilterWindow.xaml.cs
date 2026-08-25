using System.Windows;
using CadMeasureDomain.Models;

namespace CadMeasurePlugin.UI;

/// <summary>
/// Фильтр таблицы журнала: материал и позиция спецификации.
///
/// Вынесен в отдельное окно, потому что нужен изредка — в журнале на десятки
/// строк. Держать его на палитре значило бы занимать строку интерфейса
/// постоянно ради того, чем пользуются раз в сессию.
/// </summary>
public partial class JournalFilterWindow : Window
{
    private const string AllItem = "— все —";

    private readonly List<(string Display, int Number)> _positions = new();

    /// <param name="materials">Наименования материалов реестра.</param>
    /// <param name="specification">Загруженная спецификация, либо null.</param>
    /// <param name="currentMaterial">Действующий фильтр по материалу.</param>
    /// <param name="currentPosition">Действующий фильтр по позиции.</param>
    public JournalFilterWindow(
        IEnumerable<string> materials,
        Specification? specification,
        string? currentMaterial,
        int? currentPosition)
    {
        ArgumentNullException.ThrowIfNull(materials);

        InitializeComponent();

        MaterialBox.ItemsSource = new[] { AllItem }.Concat(materials).ToList();
        MaterialBox.SelectedItem = currentMaterial ?? AllItem;

        if (specification is null)
        {
            // Без спецификации фильтровать по её позициям не по чему.
            PositionLabel.Visibility = Visibility.Collapsed;
            PositionBox.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var item in specification.Items)
            _positions.Add(($"{item.Number}. {item.Name}", item.Number));

        PositionBox.ItemsSource = new[] { AllItem }.Concat(_positions.Select(p => p.Display)).ToList();
        PositionBox.SelectedItem = currentPosition is null
            ? AllItem
            : _positions.FirstOrDefault(p => p.Number == currentPosition).Display ?? AllItem;
    }

    /// <summary>Выбранный материал, либо null — «все».</summary>
    public string? Material { get; private set; }

    /// <summary>Выбранная позиция спецификации, либо null — «все».</summary>
    public int? SpecificationNumber { get; private set; }

    private void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        Material = null;
        SpecificationNumber = null;
        DialogResult = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var material = MaterialBox.Text?.Trim();
        Material = string.IsNullOrEmpty(material) || material == AllItem ? null : material;

        var position = PositionBox.Text?.Trim();
        SpecificationNumber = string.IsNullOrEmpty(position) || position == AllItem
            ? null
            : _positions.FirstOrDefault(p => p.Display == position) is { Number: > 0 } found
                ? found.Number
                : null;

        DialogResult = true;
    }
}
