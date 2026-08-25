using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CadMeasureDomain.Models;

namespace CadMeasurePlugin.UI;

/// <summary>
/// Строка списка позиций спецификации с отметкой «взять в журнал».
///
/// Отдельный класс вместо самой позиции нужен из-за отметки: она относится
/// к текущему импорту, а не к спецификации, и хранить её в доменной модели
/// было бы неправильно.
/// </summary>
public sealed class SpecificationImportRow : INotifyPropertyChanged
{
    private bool _isSelected;

    public SpecificationImportRow(SpecificationItem item, bool hasMaterial)
    {
        Item = item;
        HasMaterial = hasMaterial;
    }

    /// <summary>Позиция спецификации.</summary>
    public SpecificationItem Item { get; }

    /// <summary>Материал с таким наименованием есть в реестре.</summary>
    public bool HasMaterial { get; }

    public int Number => Item.Number;

    public string Name => Item.Name;

    public string Mark => Item.Mark;

    public string Unit => Item.Unit;

    public double Quantity => Item.Quantity;

    /// <summary>Единица измерения распознана и позицию можно замерить.</summary>
    public bool IsSupported => Item.IsSupported;

    /// <summary>Состояние привязки к реестру для колонки списка.</summary>
    public string MaterialState => HasMaterial ? "найден" : "не найден";

    /// <summary>Позиция готова к замеру: и единица понятна, и материал есть.</summary>
    public bool IsMeasurable => IsSupported && HasMaterial;

    /// <summary>Отметка «взять в журнал».</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Отметка изменилась — окну нужно обновить счётчик.</summary>
    public event EventHandler? SelectionChanged;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Выбор позиций спецификации для переноса в журнал.
///
/// Спецификация читается целиком и остаётся целой — по ней строится свод
/// в Excel. Здесь решается другое: какие позиции взять в работу сейчас.
/// Тащить в журнал все сотни строк без спроса нельзя, поэтому по умолчанию
/// отмечены только те, что реально можно замерить.
/// </summary>
public partial class SpecificationImportWindow : Window
{
    private readonly ObservableCollection<SpecificationImportRow> _rows = new();

    public SpecificationImportWindow(Specification specification, Func<SpecificationItem, bool> hasMaterial)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(hasMaterial);

        InitializeComponent();

        foreach (var item in specification.Items)
        {
            var row = new SpecificationImportRow(item, hasMaterial(item));
            row.IsSelected = row.IsMeasurable;
            row.SelectionChanged += (_, _) => UpdateSelectionText();
            _rows.Add(row);
        }

        ItemsGrid.ItemsSource = _rows;

        var unsupported = _rows.Count(r => !r.IsSupported);
        var withoutMaterial = _rows.Count(r => !r.HasMaterial);

        HeaderText.Text =
            $"Файл: {specification.FileName}. Позиций: {specification.Items.Count}, " +
            $"пропущено строк файла: {specification.SkippedRows}." +
            (unsupported > 0 ? $" Единица не распознана: {unsupported}." : string.Empty) +
            (withoutMaterial > 0 ? $" Нет материала в реестре: {withoutMaterial}." : string.Empty);

        UpdateSelectionText();
    }

    /// <summary>Отмеченные позиции. Заполняется при нажатии «ОК».</summary>
    public IReadOnlyList<SpecificationItem> SelectedItems { get; private set; } = Array.Empty<SpecificationItem>();

    private void UpdateSelectionText() =>
        SelectionText.Text = $"Отмечено позиций: {_rows.Count(r => r.IsSelected)} из {_rows.Count}";

    private void SetAll(Func<SpecificationImportRow, bool> value)
    {
        foreach (var row in _rows) row.IsSelected = value(row);
        UpdateSelectionText();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetAll(_ => true);

    private void SelectNone_Click(object sender, RoutedEventArgs e) => SetAll(_ => false);

    private void SelectMeasurable_Click(object sender, RoutedEventArgs e) => SetAll(r => r.IsMeasurable);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        // Правки чекбоксов подтверждаются при потере фокуса ячейкой:
        // без этого последняя отметка потерялась бы при нажатии «ОК».
        ItemsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, exitEditingMode: true);

        SelectedItems = _rows.Where(r => r.IsSelected).Select(r => r.Item).ToList();

        if (SelectedItems.Count == 0)
        {
            AcadUiHelper.ShowWarning(this, "Не отмечено ни одной позиции.");
            return;
        }

        DialogResult = true;
    }
}
