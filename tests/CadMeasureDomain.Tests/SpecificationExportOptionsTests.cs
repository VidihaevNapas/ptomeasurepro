using CadMeasureDomain.Models;
using CadMeasureDomain.Services;
using ClosedXML.Excel;

namespace CadMeasureDomain.Tests;

/// <summary>
/// Параметры выгрузки: какие листы, столбцы и строки уходят в книгу.
///
/// Ключевое требование — состав выгрузки задаётся только этими параметрами
/// и никак не зависит от того, что показано в палитре: скрытый в таблице
/// столбец обязан попасть в Excel, если он выбран в диалоге экспорта.
/// </summary>
public class SpecificationExportOptionsTests
{
    private const string Drawing = "Корпус-1.dwg";

    private static Specification BuildSpecification() => new()
    {
        FileName = "спецификация.xlsx",
        Items = new[]
        {
            new SpecificationItem { Number = 1, Name = "Труба стальная Dn80", Unit = "м.п.", Quantity = 200 },
            new SpecificationItem { Number = 2, Name = "Кран шаровой Dn80", Unit = "шт.", Quantity = 10 },
            new SpecificationItem { Number = 3, Name = "Отвод Dn80", Unit = "шт.", Quantity = 24 }
        }
    };

    /// <summary>Журнал, где замерена только первая позиция спецификации.</summary>
    private static MeasurementJournal BuildJournalWithOneMeasuredItem(Specification specification)
    {
        var journal = new MeasurementJournal();
        var record = journal.AddOrUpdateLinear(
            TestData.Pipe("Труба стальная Dn80"), "", "PIPE_D89x4", 120, 0, 4, Drawing);

        MeasurementJournal.BindToSpecification(record, specification.Items[0], specification.FileName);
        return journal;
    }

    private static List<string> ReadColumn(IXLWorksheet sheet, int column, int rowCount) =>
        Enumerable.Range(3, rowCount).Select(row => sheet.Cell(row, column).GetString()).ToList();

    [Fact]
    public void OnlyMeasured_ExportsRowsWithMeasurementOnly()
    {
        using var temp = new TempDirectory();
        var specification = BuildSpecification();
        var journal = BuildJournalWithOneMeasuredItem(specification);

        var path = new ExcelExportService().Export(
            journal, Drawing, temp.Combine("книга.xlsx"), specification,
            new SpecificationExportOptions { OnlyMeasured = true });

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet(ExcelExportService.SpecificationSheetName);

        // Одна строка данных и следом «ИТОГО» — незамеренные позиции не выводятся.
        Assert.Equal("Труба стальная Dn80", sheet.Cell(3, 2).GetString());
        Assert.Equal("ИТОГО", sheet.Cell(4, 1).GetString());
        Assert.Contains("только проверенные", sheet.Cell(1, 1).GetString());
    }

    [Fact]
    public void AllItems_ExportEveryRowOfSpecification()
    {
        using var temp = new TempDirectory();
        var specification = BuildSpecification();
        var journal = BuildJournalWithOneMeasuredItem(specification);

        var path = new ExcelExportService().Export(
            journal, Drawing, temp.Combine("книга.xlsx"), specification,
            new SpecificationExportOptions { OnlyMeasured = false });

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet(ExcelExportService.SpecificationSheetName);

        Assert.Equal(
            new[] { "Труба стальная Dn80", "Кран шаровой Dn80", "Отвод Dn80" },
            ReadColumn(sheet, 2, 3));

        // Незамеренные позиции идут с нулями, а не пропадают.
        Assert.Equal(0, sheet.Cell(4, 8).GetValue<double>());
    }

    [Fact]
    public void SelectedColumns_AreTheOnlyOnesExported()
    {
        using var temp = new TempDirectory();
        var specification = BuildSpecification();
        var journal = BuildJournalWithOneMeasuredItem(specification);

        var path = new ExcelExportService().Export(
            journal, Drawing, temp.Combine("книга.xlsx"), specification,
            new SpecificationExportOptions
            {
                Columns = new[] { SpecificationColumn.Name, SpecificationColumn.Total, SpecificationColumn.Difference }
            });

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet(ExcelExportService.SpecificationSheetName);

        // Столбцы: наименование, подсчёт по чертежу, всего, расхождение.
        Assert.Equal("Наименование материала", sheet.Cell(2, 1).GetString());
        Assert.Equal(SpecificationSummaryBuilder.CountColumnPrefix + Drawing, sheet.Cell(2, 2).GetString());
        Assert.Equal("Всего подсчитано", sheet.Cell(2, 3).GetString());
        Assert.Equal("Расхождение", sheet.Cell(2, 4).GetString());
        Assert.True(sheet.Cell(2, 5).IsEmpty());

        Assert.Equal(120, sheet.Cell(3, 3).GetValue<double>());
        Assert.Equal(-80, sheet.Cell(3, 4).GetValue<double>());
    }

    [Fact]
    public void ColumnSelection_DoesNotDependOnAnythingElse()
    {
        // Полный набор столбцов даёт тот же состав данных, что и урезанный:
        // выбор столбцов влияет только на выгрузку, а не на журнал и свод.
        using var temp = new TempDirectory();
        var specification = BuildSpecification();
        var journal = BuildJournalWithOneMeasuredItem(specification);

        var full = new ExcelExportService().Export(
            journal, Drawing, temp.Combine("полная.xlsx"), specification);

        var narrow = new ExcelExportService().Export(
            journal, Drawing, temp.Combine("узкая.xlsx"), specification,
            new SpecificationExportOptions { Columns = new[] { SpecificationColumn.Name, SpecificationColumn.Total } });

        using var fullBook = new XLWorkbook(full);
        using var narrowBook = new XLWorkbook(narrow);

        var fullSheet = fullBook.Worksheet(ExcelExportService.SpecificationSheetName);
        var narrowSheet = narrowBook.Worksheet(ExcelExportService.SpecificationSheetName);

        // Наименования и итоги совпадают, различается только набор столбцов.
        Assert.Equal(ReadColumn(fullSheet, 2, 3), ReadColumn(narrowSheet, 1, 3));
        Assert.Equal(fullSheet.Cell(3, 9).GetValue<double>(), narrowSheet.Cell(3, 3).GetValue<double>());

        // Свод в памяти не изменился ни от одной из выгрузок.
        var summary = SpecificationSummaryBuilder.Build(journal, specification);
        Assert.Equal(3, summary.Count);
        Assert.Equal(120, summary[0].Total);
    }

    [Fact]
    public void SheetSelection_ControlsWorkbookContents()
    {
        using var temp = new TempDirectory();
        var specification = BuildSpecification();
        var journal = BuildJournalWithOneMeasuredItem(specification);

        var path = new ExcelExportService().Export(
            journal, Drawing, temp.Combine("книга.xlsx"), specification,
            new SpecificationExportOptions
            {
                IncludeStatement = false,
                IncludeLinearDetails = false,
                IncludePieceDetails = false,
                IncludeSpecification = true
            });

        using var workbook = new XLWorkbook(path);

        Assert.Equal(
            new[] { ExcelExportService.SpecificationSheetName },
            workbook.Worksheets.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void EmptySheetSelection_IsRejected()
    {
        using var temp = new TempDirectory();

        // Пустая книга — не результат, а молчаливая потеря работы.
        Assert.Throws<ArgumentException>(() => new ExcelExportService().Export(
            new MeasurementJournal(), Drawing, temp.Combine("книга.xlsx"), BuildSpecification(),
            new SpecificationExportOptions
            {
                IncludeStatement = false,
                IncludeLinearDetails = false,
                IncludePieceDetails = false,
                IncludeSpecification = false
            }));
    }

    [Fact]
    public void DrawingSelection_LimitsCountColumns()
    {
        using var temp = new TempDirectory();
        var specification = BuildSpecification();
        var journal = BuildJournalWithOneMeasuredItem(specification);

        var second = journal.AddOrUpdateLinear(
            TestData.Pipe("Труба стальная Dn80"), "", "PIPE_D89x4", 55, 0, 2, "Корпус-2.dwg");
        MeasurementJournal.BindToSpecification(second, specification.Items[0], specification.FileName);

        var path = new ExcelExportService().Export(
            journal, Drawing, temp.Combine("книга.xlsx"), specification,
            new SpecificationExportOptions
            {
                Columns = new[] { SpecificationColumn.Name, SpecificationColumn.Total },
                Drawings = new[] { "Корпус-2.dwg" }
            });

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet(ExcelExportService.SpecificationSheetName);

        Assert.Equal(SpecificationSummaryBuilder.CountColumnPrefix + "Корпус-2.dwg", sheet.Cell(2, 2).GetString());

        // «Всего» считается по всем чертежам, а не только по выбранным столбцам:
        // это итог замера, а не сумма показанных ячеек.
        Assert.Equal(175, sheet.Cell(3, 3).GetValue<double>());
    }
}

/// <summary>Ручная правка полей спецификации в строках, которые импорт не прочитал.</summary>
public class SpecificationManualEditTests
{
    private const string Drawing = "Корпус-1.dwg";

    private static (MeasurementJournal Journal, Specification Specification, MeasurementRecord Record) BuildCase(
        string name = "",
        string unit = "",
        double quantity = 0)
    {
        var specification = new Specification
        {
            FileName = "спецификация.xlsx",
            Items = new[] { new SpecificationItem { Number = 1, Name = name, Unit = unit, Quantity = quantity } }
        };

        var journal = new MeasurementJournal();
        var record = journal.AddFromSpecification(
            specification.Items[0], specification.FileName, Drawing, material: null);

        return (journal, specification, record);
    }

    [Fact]
    public void IsUnread_DetectsEmptyFields()
    {
        var (_, _, record) = BuildCase(name: "Труба", unit: "");

        Assert.False(SpecificationManualEdit.IsUnread(record, SpecificationField.Name));
        Assert.True(SpecificationManualEdit.IsUnread(record, SpecificationField.Unit));
        Assert.True(SpecificationManualEdit.IsUnread(record, SpecificationField.Mark));
        Assert.True(SpecificationManualEdit.IsUnread(record, SpecificationField.Quantity));
    }

    [Fact]
    public void Apply_UpdatesRecordAndSpecificationItem()
    {
        var (_, specification, record) = BuildCase(name: "Труба", unit: "");

        var log = SpecificationManualEdit.Apply(record, specification, SpecificationField.Unit, "м.п.");

        Assert.Equal("м.п.", record.Unit);
        Assert.Equal("м.п.", specification.Items[0].Unit);
        Assert.True(record.SpecificationEditedManually);
        Assert.Equal("Позиция спецификации п/п 1 изменена вручную: ед. изм. = м.п.", log);
    }

    [Fact]
    public void Apply_QuantityRecalculatesDifference()
    {
        var (journal, specification, record) = BuildCase(name: "Труба стальная Dn80", unit: "м.п.");
        record.HorizontalLengthM = 120;

        var log = SpecificationManualEdit.Apply(record, specification, SpecificationField.Quantity, "200,5");

        Assert.Equal(200.5, record.SpecificationQuantity);
        Assert.Equal(200.5, specification.Items[0].Quantity);
        Assert.Equal(-80.5, record.SpecificationDifference);
        Assert.Contains("кол-во = 200,5", log);

        // Свод по спецификации пересчитывается от тех же полей.
        var summary = SpecificationSummaryBuilder.Build(journal, specification).Single();
        Assert.Equal(120, summary.Total);
        Assert.Equal(-80.5, summary.Difference);
    }

    [Fact]
    public void Apply_RejectsNonNumericQuantity()
    {
        var (_, specification, record) = BuildCase(name: "Труба", unit: "м");

        Assert.Throws<InvalidOperationException>(() =>
            SpecificationManualEdit.Apply(record, specification, SpecificationField.Quantity, "много"));
    }

    [Fact]
    public void Apply_IgnoresUnchangedValue()
    {
        var (_, specification, record) = BuildCase(name: "Труба", unit: "м.п.");

        Assert.Null(SpecificationManualEdit.Apply(record, specification, SpecificationField.Unit, "м.п."));
        Assert.False(record.SpecificationEditedManually);
    }

    [Fact]
    public void Apply_NameChangeReachesSpecificationSummary()
    {
        var (journal, specification, record) = BuildCase(name: "", unit: "шт.", quantity: 5);

        SpecificationManualEdit.Apply(record, specification, SpecificationField.Name, "Кран шаровой Dn80");

        Assert.Equal("Кран шаровой Dn80", record.MaterialName);

        var summary = SpecificationSummaryBuilder.Build(journal, specification).Single();
        Assert.Equal("Кран шаровой Dn80", summary.Item.Name);
    }
}
