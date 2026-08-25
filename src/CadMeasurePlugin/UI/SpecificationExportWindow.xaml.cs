using System.Windows;
using CadMeasureDomain.Services;

namespace CadMeasurePlugin.UI;

/// <summary>
/// Параметры выгрузки в Excel: листы, строки и столбцы листа «Спецификация».
///
/// Диалог показывается только когда спецификация загружена: без неё
/// настраивать нечего, и экспорт идёт как раньше, одной кнопкой.
/// </summary>
public partial class SpecificationExportWindow : Window
{
    private readonly List<(SpecificationColumn Column, ColumnVisibilityRow Row)> _columns = new();
    private readonly List<(string Drawing, ColumnVisibilityRow Row)> _drawings = new();

    public SpecificationExportWindow(IReadOnlyList<string> drawings)
    {
        ArgumentNullException.ThrowIfNull(drawings);

        InitializeComponent();

        foreach (var (column, title) in ColumnTitles)
            _columns.Add((column, new ColumnVisibilityRow(title, isVisible: true)));

        foreach (var drawing in drawings)
            _drawings.Add((drawing, new ColumnVisibilityRow(drawing, isVisible: true)));

        ColumnsList.ItemsSource = _columns.Select(c => c.Row).ToList();
        DrawingsList.ItemsSource = _drawings.Select(d => d.Row).ToList();

        if (_drawings.Count == 0)
        {
            DrawingsHeader.Text = "Подсчётов по чертежам пока нет: ни по одной позиции спецификации не сделано замеров.";
            DrawingsHeader.FontWeight = FontWeights.Normal;
        }
    }

    /// <summary>Названия столбцов в порядке вывода на лист.</summary>
    private static IReadOnlyList<(SpecificationColumn Column, string Title)> ColumnTitles { get; } = new[]
    {
        (SpecificationColumn.Number, "п/п"),
        (SpecificationColumn.Name, "Наименование материала"),
        (SpecificationColumn.Mark, "Марка"),
        (SpecificationColumn.EquipmentCode, "Код оборудования"),
        (SpecificationColumn.Manufacturer, "Изготовитель"),
        (SpecificationColumn.Unit, "Ед. изм."),
        (SpecificationColumn.Quantity, "Кол-во по спецификации"),
        (SpecificationColumn.Total, "Всего подсчитано"),
        (SpecificationColumn.Difference, "Расхождение")
    };

    /// <summary>Выбранные параметры. Заполняются при нажатии «Экспортировать».</summary>
    public SpecificationExportOptions Options { get; private set; } = SpecificationExportOptions.Default;

    private void SpecificationSheet_Changed(object sender, RoutedEventArgs e)
    {
        // Без листа «Спецификация» настраивать его столбцы бессмысленно.
        if (ColumnsGroup is not null)
            ColumnsGroup.IsEnabled = SpecificationSheetCheck.IsChecked == true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var includeSpecification = SpecificationSheetCheck.IsChecked == true;

        if (StatementSheetCheck.IsChecked != true &&
            LinearSheetCheck.IsChecked != true &&
            PieceSheetCheck.IsChecked != true &&
            !includeSpecification)
        {
            AcadUiHelper.ShowWarning(this, "Не выбрано ни одного листа — экспортировать нечего.");
            return;
        }

        var columns = _columns.Where(c => c.Row.IsVisible).Select(c => c.Column).ToList();
        if (includeSpecification && columns.Count == 0 && _drawings.All(d => !d.Row.IsVisible))
        {
            AcadUiHelper.ShowWarning(this, "Для листа «Спецификация» не выбрано ни одного столбца.");
            return;
        }

        Options = new SpecificationExportOptions
        {
            IncludeStatement = StatementSheetCheck.IsChecked == true,
            IncludeLinearDetails = LinearSheetCheck.IsChecked == true,
            IncludePieceDetails = PieceSheetCheck.IsChecked == true,
            IncludeSpecification = includeSpecification,
            Columns = columns,
            Drawings = _drawings.Where(d => d.Row.IsVisible).Select(d => d.Drawing).ToList(),
            OnlyMeasured = OnlyMeasuredRadio.IsChecked == true
        };

        DialogResult = true;
    }
}
