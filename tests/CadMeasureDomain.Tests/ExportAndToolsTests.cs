using CadMeasureDomain.Models;
using CadMeasureDomain.Services;
using CadMeasureDomain.Tools;
using ClosedXML.Excel;

namespace CadMeasureDomain.Tests;

/// <summary>
/// Выгрузка ведомости в .xlsx.
/// Проверяется структура готового файла, а не только факт его создания:
/// ведомость передают заказчику, и «Кол-во» должно остаться числом,
/// с которым можно продолжить считать.
/// </summary>
public class ExcelExportServiceTests
{
    private const string Drawing = "объект.dwg";

    [Fact]
    public void BuildExportPath_PutsFileNextToDrawing()
    {
        using var temp = new TempDirectory();

        var path = ExcelExportService.BuildExportPath(temp.Combine("объект.dwg"), temp.Path);

        Assert.Equal(temp.Combine("объект_замеры.xlsx"), path);
    }

    [Fact]
    public void BuildExportPath_NeverOverwritesExistingFile()
    {
        using var temp = new TempDirectory();
        var drawingPath = temp.Combine("объект.dwg");

        var first = ExcelExportService.BuildExportPath(drawingPath, temp.Path);
        File.WriteAllText(first, string.Empty);

        var second = ExcelExportService.BuildExportPath(drawingPath, temp.Path);
        File.WriteAllText(second, string.Empty);

        var third = ExcelExportService.BuildExportPath(drawingPath, temp.Path);

        Assert.Equal(temp.Combine("объект_замеры_2.xlsx"), second);
        Assert.Equal(temp.Combine("объект_замеры_3.xlsx"), third);
    }

    [Fact]
    public void BuildExportPath_FallsBackForUnsavedDrawing()
    {
        // Несохранённый чертёж в AutoCAD называется «Drawing1.dwg» и каталога не имеет.
        using var temp = new TempDirectory();

        Assert.Equal(temp.Combine("Чертеж_замеры.xlsx"), ExcelExportService.BuildExportPath(null, temp.Path));
        Assert.Equal(temp.Combine("Drawing1_замеры.xlsx"), ExcelExportService.BuildExportPath("Drawing1.dwg", temp.Path));
    }

    [Fact]
    public void Export_WritesStatementSheet()
    {
        using var temp = new TempDirectory();
        var journal = new MeasurementJournal();
        journal.AddOrUpdateLinear(TestData.Pipe(), "", "PIPE_D89x4", 12.5, 0, 2, Drawing);
        journal.AddOrUpdatePiece(TestData.Piece(), "", "PIECE_Dn15", 4, Drawing);

        var path = new ExcelExportService().Export(journal, Drawing, temp.Combine("ведомость.xlsx"));

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet(ExcelExportService.SheetName);

        Assert.Equal(StatementBuilder.Title, sheet.Cell(1, 1).GetString());
        Assert.Equal(StatementBuilder.ColumnHeaders[0], sheet.Cell(2, 1).GetString());
        Assert.Equal(StatementBuilder.ColumnHeaders[3], sheet.Cell(2, 4).GetString());

        // Строка трубы: номер, наименование, единица ведомости и число.
        Assert.Equal(1, sheet.Cell(3, 1).GetValue<int>());
        Assert.Equal(TestData.Pipe().Name, sheet.Cell(3, 2).GetString());
        Assert.Equal("м", sheet.Cell(3, 3).GetString());
        Assert.Equal(12.5, sheet.Cell(3, 4).GetValue<double>());

        // Строка штучного изделия идёт после линейных.
        Assert.Equal("шт", sheet.Cell(4, 3).GetString());
        Assert.Equal(4, sheet.Cell(4, 4).GetValue<int>());
    }

    [Fact]
    public void Export_KeepsQuantityAsNumberNotText()
    {
        using var temp = new TempDirectory();
        var journal = new MeasurementJournal();
        journal.AddOrUpdateLinear(TestData.Pipe(), "", "PIPE_D89x4", 12.5, 0, 1, Drawing);

        var path = new ExcelExportService().Export(journal, Drawing, temp.Combine("ведомость.xlsx"));

        using var workbook = new XLWorkbook(path);
        var quantity = workbook.Worksheet(ExcelExportService.SheetName).Cell(3, 4);

        Assert.Equal(XLDataType.Number, quantity.DataType);
        Assert.Equal("0.00", quantity.Style.NumberFormat.Format);
    }

    [Fact]
    public void Export_WritesEmptyStatementWithHeadersOnly()
    {
        using var temp = new TempDirectory();

        var path = new ExcelExportService().Export(new MeasurementJournal(), Drawing, temp.Combine("пусто.xlsx"));

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet(ExcelExportService.SheetName);

        Assert.Equal(StatementBuilder.Title, sheet.Cell(1, 1).GetString());
        Assert.True(sheet.Cell(3, 2).IsEmpty());
    }

    [Fact]
    public void Export_CreatesStatementAndTwoDetailSheets()
    {
        using var temp = new TempDirectory();

        var path = new ExcelExportService().Export(new MeasurementJournal(), Drawing, temp.Combine("книга.xlsx"));

        using var workbook = new XLWorkbook(path);

        Assert.Equal(
            new[] { ExcelExportService.SheetName, ExcelExportService.LinearSheetName, ExcelExportService.PieceSheetName },
            workbook.Worksheets.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void Export_LinearSheetCarriesEverythingStatementOmits()
    {
        // Ведомость по своей форме не показывает характеристику, участок, слой,
        // файл DWG и разбивку длины — всё это должно быть в детализации.
        using var _ = new CultureScope("ru-RU");
        using var temp = new TempDirectory();

        var journal = new MeasurementJournal();
        journal.AddOrUpdateLinear(TestData.RectDuct(), "Кровля", "DUCT_1250x800_t0.9_Кровля", 10, 2, 3, Drawing);

        var path = new ExcelExportService().Export(journal, Drawing, temp.Combine("книга.xlsx"));

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet(ExcelExportService.LinearSheetName);

        Assert.Equal("Характеристика", sheet.Cell(2, 4).GetString());
        Assert.Equal("Площадь, м²", sheet.Cell(2, 9).GetString());
        Assert.Equal("Файл DWG", sheet.Cell(2, 13).GetString());

        Assert.Equal(1, sheet.Cell(3, 1).GetValue<int>());
        Assert.Equal("Воздуховод", sheet.Cell(3, 2).GetString());
        Assert.Equal(TestData.RectDuct().Name, sheet.Cell(3, 3).GetString());
        Assert.Equal("1250x800, 0,9 мм", sheet.Cell(3, 4).GetString());
        Assert.Equal("м.п.", sheet.Cell(3, 5).GetString());
        Assert.Equal(10, sheet.Cell(3, 6).GetValue<double>());
        Assert.Equal(2, sheet.Cell(3, 7).GetValue<double>());
        Assert.Equal(12, sheet.Cell(3, 8).GetValue<double>());
        Assert.Equal(49.2, sheet.Cell(3, 9).GetValue<double>());
        Assert.Equal(3, sheet.Cell(3, 10).GetValue<int>());
        Assert.Equal("Кровля", sheet.Cell(3, 11).GetString());
        Assert.Equal("DUCT_1250x800_t0.9_Кровля", sheet.Cell(3, 12).GetString());
        Assert.Equal(Drawing, sheet.Cell(3, 13).GetString());
        Assert.Equal(ExcelExportService.MeasuredValueMark, sheet.Cell(3, 14).GetString());
    }

    [Fact]
    public void Export_LeavesAreaEmptyForPipesAndCables()
    {
        using var temp = new TempDirectory();
        var journal = new MeasurementJournal();
        journal.AddOrUpdateLinear(TestData.Pipe(), "", "PIPE_D89x4", 10, 0, 1, Drawing);

        var path = new ExcelExportService().Export(journal, Drawing, temp.Combine("книга.xlsx"));

        using var workbook = new XLWorkbook(path);

        // Ноль читался бы как «замерили и получили ноль», поэтому ячейка пустая.
        Assert.True(workbook.Worksheet(ExcelExportService.LinearSheetName).Cell(3, 9).IsEmpty());
    }

    [Fact]
    public void Export_LinearSheetTotalsUseFormulas()
    {
        using var temp = new TempDirectory();
        var journal = new MeasurementJournal();
        journal.AddOrUpdateLinear(TestData.Pipe(), "", "PIPE_D89x4", 5.5, 0, 1, Drawing);
        journal.AddOrUpdateLinear(TestData.RectDuct(), "", "DUCT_1250x800_t0.9", 10, 2, 3, Drawing);

        var path = new ExcelExportService().Export(journal, Drawing, temp.Combine("книга.xlsx"));

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet(ExcelExportService.LinearSheetName);

        // Трубы идут перед воздуховодами — тот же порядок, что в ведомости.
        Assert.Equal(TestData.Pipe().Name, sheet.Cell(3, 3).GetString());
        Assert.Equal(TestData.RectDuct().Name, sheet.Cell(4, 3).GetString());

        Assert.Equal("ИТОГО", sheet.Cell(5, 1).GetString());
        Assert.Equal("SUM(H3:H4)", sheet.Cell(5, 8).FormulaA1);
        Assert.Equal(17.5, sheet.Cell(5, 8).GetValue<double>());
        Assert.Equal(49.2, sheet.Cell(5, 9).GetValue<double>());
    }

    [Fact]
    public void Export_PieceSheetGroupsByKindAndMarksManualValues()
    {
        using var temp = new TempDirectory();
        var journal = new MeasurementJournal();
        journal.AddOrUpdatePiece(TestData.Piece("Кран шаровой Dn15", kind: "Запорная арматура"), "", "PIECE_Dn15", 2, Drawing);
        var manual = journal.AddOrUpdatePiece(TestData.Piece(), "Этаж 1", "PIECE_Dn15_Этаж 1", 4, Drawing);
        manual.ManualQuantity = 7;

        var path = new ExcelExportService().Export(journal, Drawing, temp.Combine("книга.xlsx"));

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet(ExcelExportService.PieceSheetName);

        // «Запорная арматура» перед «Фасонными изделиями трубопроводов».
        Assert.Equal("Запорная арматура", sheet.Cell(3, 2).GetString());
        Assert.Equal(2, sheet.Cell(3, 6).GetValue<int>());
        Assert.Equal(ExcelExportService.MeasuredValueMark, sheet.Cell(3, 10).GetString());

        Assert.Equal("Фасонные изделия трубопроводов", sheet.Cell(4, 2).GetString());
        Assert.Equal(7, sheet.Cell(4, 6).GetValue<int>());
        Assert.Equal("Этаж 1", sheet.Cell(4, 7).GetString());
        Assert.Equal(ExcelExportService.ManualValueMark, sheet.Cell(4, 10).GetString());

        Assert.Equal(9, sheet.Cell(5, 6).GetValue<double>());
    }

    [Fact]
    public void Export_DetailSheetsShowOnlyCurrentDrawing()
    {
        using var temp = new TempDirectory();
        var journal = new MeasurementJournal();
        journal.AddOrUpdateLinear(TestData.Pipe(), "", "PIPE_D89x4", 10, 0, 1, Drawing);
        journal.AddOrUpdateLinear(TestData.Cable(), "", "CABLE_3x2.5", 30, 0, 1, "другой.dwg");

        var path = new ExcelExportService().Export(journal, Drawing, temp.Combine("книга.xlsx"));

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet(ExcelExportService.LinearSheetName);

        Assert.Equal(TestData.Pipe().Name, sheet.Cell(3, 3).GetString());
        Assert.Equal("ИТОГО", sheet.Cell(4, 1).GetString());
    }

    [Fact]
    public void Export_DetailSheetsKeepHeadersWhenNothingMeasured()
    {
        using var temp = new TempDirectory();

        var path = new ExcelExportService().Export(new MeasurementJournal(), Drawing, temp.Combine("книга.xlsx"));

        using var workbook = new XLWorkbook(path);

        Assert.Equal("Тип", workbook.Worksheet(ExcelExportService.LinearSheetName).Cell(2, 2).GetString());
        Assert.Equal("Вид изделия", workbook.Worksheet(ExcelExportService.PieceSheetName).Cell(2, 2).GetString());
        Assert.True(workbook.Worksheet(ExcelExportService.LinearSheetName).Cell(3, 1).IsEmpty());
    }

    [Fact]
    public void Export_RejectsEmptyPath()
    {
        Assert.Throws<ArgumentException>(() => new ExcelExportService().Export(new MeasurementJournal(), Drawing, "  "));
    }
}

/// <summary>
/// Инструменты замера. Инструмент не выбирается пользователем — он следует
/// из класса материала, поэтому чужой материал должен отвергаться.
/// </summary>
public class MeasureToolTests
{
    /// <summary>Подставной чертёж: запоминает, какой слой у него просили создать.</summary>
    private sealed class FakeWorkspace : ICadWorkspace
    {
        public Material? LastMaterial { get; private set; }

        public string? LastLayerName { get; private set; }

        public string EnsureLayer(Material material, string layerName)
        {
            LastMaterial = material;
            LastLayerName = layerName;
            return layerName;
        }
    }

    [Fact]
    public void SelectMaterial_AcceptsMatchingClass()
    {
        var tool = new PipeMeasureTool(new FakeWorkspace(), new LayerNameFactory());
        var pipe = TestData.Pipe();

        tool.SelectMaterial(pipe);

        Assert.Same(pipe, tool.CurrentMaterial);
        Assert.Equal(MaterialClasses.Pipe, tool.MaterialClass);
    }

    [Fact]
    public void SelectMaterial_RejectsForeignClass()
    {
        var tool = new PipeMeasureTool(new FakeWorkspace(), new LayerNameFactory());

        var exception = Assert.Throws<InvalidOperationException>(() => tool.SelectMaterial(TestData.Cable()));

        Assert.Contains("Замер трубопровода", exception.Message);
        Assert.Null(tool.CurrentMaterial);
    }

    [Fact]
    public void PieceCountTool_RejectsLinearMaterial()
    {
        var tool = new PieceCountTool(new FakeWorkspace(), new LayerNameFactory());

        Assert.Throws<InvalidOperationException>(() => tool.SelectMaterial(TestData.Pipe()));
    }

    [Fact]
    public void GetLayerName_RequiresSelectedMaterial()
    {
        var tool = new DuctMeasureTool(new FakeWorkspace(), new LayerNameFactory());

        Assert.Throws<InvalidOperationException>(() => tool.GetLayerName("Этаж 1"));
    }

    [Fact]
    public void GetLayerName_UsesFactoryNameWithSection()
    {
        var factory = new LayerNameFactory();
        var tool = new CableMeasureTool(new FakeWorkspace(), factory);
        var cable = TestData.Cable();
        tool.SelectMaterial(cable);

        Assert.Equal(factory.GetLayerName(cable, "Этаж 1"), tool.GetLayerName("Этаж 1"));
        Assert.Equal("CABLE_3x2.5_Этаж 1", tool.GetLayerName("Этаж 1"));
    }

    [Fact]
    public void PrepareLayerOrSelection_CreatesLayerInWorkspace()
    {
        var workspace = new FakeWorkspace();
        var tool = new PipeMeasureTool(workspace, new LayerNameFactory());
        var pipe = TestData.Pipe();
        tool.SelectMaterial(pipe);

        var layerName = tool.PrepareLayerOrSelection("Этаж 1");

        Assert.Equal("PIPE_D89x4_Этаж 1", layerName);
        Assert.Same(pipe, workspace.LastMaterial);
        Assert.Equal(layerName, workspace.LastLayerName);
    }

    [Fact]
    public void ClearMaterial_MakesToolUnusableAgain()
    {
        // Материал удалили из реестра — инструмент не должен продолжать
        // работать по позиции, которой больше нет.
        var tool = new PipeMeasureTool(new FakeWorkspace(), new LayerNameFactory());
        tool.SelectMaterial(TestData.Pipe());

        tool.ClearMaterial();

        Assert.Null(tool.CurrentMaterial);
        Assert.Throws<InvalidOperationException>(() => tool.PrepareLayerOrSelection(null));
    }

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new PipeMeasureTool(null!, new LayerNameFactory()));
        Assert.Throws<ArgumentNullException>(() => new PipeMeasureTool(new FakeWorkspace(), null!));
        Assert.Throws<ArgumentNullException>(() => new PieceCountTool(null!, new LayerNameFactory()));
    }

    [Fact]
    public void ToolNames_DescribeTheirMaterialClass()
    {
        var workspace = new FakeWorkspace();
        var factory = new LayerNameFactory();

        Assert.Equal(MaterialClasses.Pipe, new PipeMeasureTool(workspace, factory).MaterialClass);
        Assert.Equal(MaterialClasses.Duct, new DuctMeasureTool(workspace, factory).MaterialClass);
        Assert.Equal(MaterialClasses.Cable, new CableMeasureTool(workspace, factory).MaterialClass);
        Assert.Equal(MaterialClasses.Piece, new PieceCountTool(workspace, factory).MaterialClass);
    }
}
