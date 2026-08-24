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
