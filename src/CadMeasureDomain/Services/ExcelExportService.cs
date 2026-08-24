using CadMeasureDomain.Models;
using ClosedXML.Excel;

namespace CadMeasureDomain.Services;

/// <summary>
/// Выгрузка ведомости в .xlsx через ClosedXML.
/// Класс не зависит от AutoCAD: на вход подаются журнал и путь к файлу.
///
/// Состав и порядок строк берутся из <see cref="StatementBuilder"/> — того же,
/// что заполняет таблицу в чертеже. Поэтому выгрузка и таблица всегда совпадают.
/// </summary>
public sealed class ExcelExportService
{
    /// <summary>Единственный лист выгрузки.</summary>
    public const string SheetName = "Ведомость";

    private const string ExportSuffix = "_замеры";
    private const int ColumnCount = 4;

    /// <summary>
    /// Построить путь для нового файла экспорта рядом с DWG.
    /// Первый экспорт — «file_замеры.xlsx», далее «file_замеры_2.xlsx», «_3» и т.д.
    /// Существующие файлы никогда не перезаписываются.
    /// </summary>
    /// <param name="drawingFullPath">Полный путь к DWG.</param>
    /// <param name="fallbackDirectory">
    /// Куда класть файл, если чертёж ещё ни разу не сохранён (путь пустой).
    /// </param>
    public static string BuildExportPath(string? drawingFullPath, string fallbackDirectory)
    {
        string directory;
        string baseName;

        if (!string.IsNullOrWhiteSpace(drawingFullPath))
        {
            directory = Path.GetDirectoryName(drawingFullPath) ?? fallbackDirectory;
            baseName = Path.GetFileNameWithoutExtension(drawingFullPath);
        }
        else
        {
            directory = fallbackDirectory;
            baseName = "Чертеж";
        }

        // Несохранённый чертёж в AutoCAD имеет путь вида «Drawing1.dwg» без каталога.
        if (string.IsNullOrWhiteSpace(directory)) directory = fallbackDirectory;
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "Чертеж";

        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"{baseName}{ExportSuffix}.xlsx");
        var index = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(directory, $"{baseName}{ExportSuffix}_{index}.xlsx");
            index++;
        }

        return path;
    }

    /// <summary>
    /// Выгрузить ведомость по указанному чертежу. Возвращает путь к файлу.
    /// </summary>
    /// <param name="journal">Журнал замеров.</param>
    /// <param name="drawingFileName">Чертёж, по которому строится ведомость.</param>
    /// <param name="path">Куда сохранить файл.</param>
    public string Export(MeasurementJournal journal, string drawingFileName, string path)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Не задан путь к файлу экспорта.", nameof(path));

        var rows = StatementBuilder.Build(journal, drawingFileName);

        using var workbook = new XLWorkbook();
        WriteStatement(workbook.Worksheets.Add(SheetName), rows);

        workbook.SaveAs(path);
        return path;
    }

    private static void WriteStatement(IXLWorksheet sheet, IReadOnlyList<StatementRow> rows)
    {
        // --- Заголовок ведомости: одна ячейка на A:D ---
        sheet.Range(1, 1, 1, ColumnCount).Merge();

        var title = sheet.Cell(1, 1);
        title.Value = StatementBuilder.Title;
        title.Style.Font.Bold = true;
        title.Style.Font.FontSize = 12;
        title.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        title.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        // --- Шапка ---
        for (var i = 0; i < StatementBuilder.ColumnHeaders.Length; i++)
            sheet.Cell(2, i + 1).Value = StatementBuilder.ColumnHeaders[i];

        var header = sheet.Range(2, 1, 2, ColumnCount);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.LightGray;
        header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        header.Style.Alignment.WrapText = true;
        header.Style.Border.BottomBorder = XLBorderStyleValues.Medium;

        // --- Строки материалов ---
        var row = 3;
        foreach (var item in rows)
        {
            sheet.Cell(row, 1).Value = item.Number;
            sheet.Cell(row, 2).Value = item.MaterialName;
            sheet.Cell(row, 3).Value = item.Unit;

            // Число, а не текст: с ведомостью потом продолжают работать в Excel.
            sheet.Cell(row, 4).Value = item.Quantity;
            sheet.Cell(row, 4).Style.NumberFormat.Format = item.IsPiece ? "0" : "0.00";

            row++;
        }

        FinishSheet(sheet, rows.Count);
    }

    private static void FinishSheet(IXLWorksheet sheet, int dataRowCount)
    {
        sheet.SheetView.FreezeRows(2);

        if (dataRowCount > 0)
        {
            var data = sheet.Range(2, 1, dataRowCount + 2, ColumnCount);
            data.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            data.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;

            sheet.Range(3, 1, dataRowCount + 2, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            sheet.Range(3, 3, dataRowCount + 2, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            sheet.Range(3, 4, dataRowCount + 2, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        }

        sheet.Column(1).Width = 6;
        sheet.Column(2).Width = 60;
        sheet.Column(3).Width = 10;
        sheet.Column(4).Width = 12;

        sheet.Column(2).Style.Alignment.WrapText = true;
    }
}
