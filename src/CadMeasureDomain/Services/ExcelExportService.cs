using CadMeasureDomain.Models;
using ClosedXML.Excel;

namespace CadMeasureDomain.Services;

/// <summary>
/// Выгрузка замеров в .xlsx через ClosedXML.
/// Класс не зависит от AutoCAD: на вход подаются журнал и путь к файлу.
///
/// Книга состоит из трёх листов:
///   • «Ведомость» — то, что идёт заказчику: состав и порядок строк берутся
///     из <see cref="StatementBuilder"/>, того же, что заполняет таблицу
///     в чертеже, поэтому выгрузка и таблица всегда совпадают;
///   • «Линейные материалы» и «Штучные изделия» — рабочая детализация:
///     всё, что журнал знает о каждой записи и что в ведомость по её форме
///     не попадает (характеристика, участок, слой, файл DWG, разбивка длины
///     на горизонталь и вертикаль, площадь поверхности воздуховодов).
///
/// Детализация нужна, чтобы итог ведомости можно было проверить: по какому
/// слою и участку набралась строка и не правил ли её кто-то руками.
/// </summary>
public sealed class ExcelExportService
{
    /// <summary>Лист ведомости — первый в книге.</summary>
    public const string SheetName = "Ведомость";

    /// <summary>Лист детализации по трубам, воздуховодам и кабелям.</summary>
    public const string LinearSheetName = "Линейные материалы";

    /// <summary>Лист детализации по штучным изделиям.</summary>
    public const string PieceSheetName = "Штучные изделия";

    /// <summary>Лист свода по первоначальной спецификации.</summary>
    public const string SpecificationSheetName = "Спецификация";

    /// <summary>Пометка строки, значение которой задано вручную.</summary>
    public const string ManualValueMark = "вручную";

    /// <summary>Пометка строки, значение которой посчитано по чертежу.</summary>
    public const string MeasuredValueMark = "по чертежу";

    private const string ExportSuffix = "_замеры";
    private const int ColumnCount = 4;

    private static readonly string[] LinearHeaders =
    {
        "п/п",
        "Тип",
        "Наименование материала",
        "Характеристика",
        "Ед. изм.",
        "Длина гориз., м",
        "Длина вертик., м",
        "Длина всего, м",
        "Площадь, м²",
        "Полилиний",
        "Участок",
        "Слой",
        "Файл DWG",
        "Значение"
    };

    private static readonly string[] PieceHeaders =
    {
        "п/п",
        "Вид изделия",
        "Наименование материала",
        "Характеристика",
        "Ед. изм.",
        "Количество",
        "Участок",
        "Слой",
        "Файл DWG",
        "Значение"
    };

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
    /// Выгрузить замеры по указанному чертежу. Возвращает путь к файлу.
    ///
    /// Все три листа строятся по одному и тому же набору записей — записям
    /// этого DWG, — поэтому итог ведомости всегда сходится с детализацией.
    /// </summary>
    /// <param name="journal">Журнал замеров.</param>
    /// <param name="drawingFileName">Чертёж, по которому строится выгрузка.</param>
    /// <param name="path">Куда сохранить файл.</param>
    /// <param name="specification">
    /// Первоначальная спецификация, если она загружена. С ней в книге
    /// появляется четвёртый лист — свод «спецификация × чертежи».
    /// </param>
    /// <param name="options">
    /// Состав книги: какие листы, столбцы и строки выгружать.
    /// null — вся книга целиком.
    /// </param>
    public string Export(
        MeasurementJournal journal,
        string drawingFileName,
        string path,
        Specification? specification = null,
        SpecificationExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Не задан путь к файлу экспорта.", nameof(path));

        options ??= SpecificationExportOptions.Default;
        if (!options.HasAnySheet)
            throw new ArgumentException("Не выбрано ни одного листа выгрузки.", nameof(options));

        var rows = StatementBuilder.Build(journal, drawingFileName);
        var records = journal.GetRecordsForDrawing(drawingFileName ?? string.Empty);

        using var workbook = new XLWorkbook();

        if (options.IncludeStatement) WriteStatement(workbook.Worksheets.Add(SheetName), rows);
        if (options.IncludeLinearDetails) WriteLinearDetails(workbook.Worksheets.Add(LinearSheetName), records);
        if (options.IncludePieceDetails) WritePieceDetails(workbook.Worksheets.Add(PieceSheetName), records);

        // Свод по спецификации идёт последним листом: ведомость и детализация
        // существуют всегда, а спецификацию загружают не в каждом проекте.
        if (specification is not null && options.IncludeSpecification)
            WriteSpecification(workbook.Worksheets.Add(SpecificationSheetName), journal, specification, options);

        workbook.SaveAs(path);
        return path;
    }

    // ======================= Ведомость =======================

    private static void WriteStatement(IXLWorksheet sheet, IReadOnlyList<StatementRow> rows)
    {
        WriteTitle(sheet, StatementBuilder.Title, ColumnCount);
        WriteHeader(sheet, StatementBuilder.ColumnHeaders);

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

        FinishSheet(sheet, rows.Count, ColumnCount, hasTotals: false);

        sheet.Column(1).Width = 6;
        sheet.Column(2).Width = 60;
        sheet.Column(3).Width = 10;
        sheet.Column(4).Width = 12;
        sheet.Column(2).Style.Alignment.WrapText = true;

        CenterFilledRange(sheet, rows.Count + 2, ColumnCount);
        AlignNameColumnLeft(sheet, column: 2, rows.Count + 2);
    }

    // ======================= Линейные материалы =======================

    private static void WriteLinearDetails(IXLWorksheet sheet, IReadOnlyList<MeasurementRecord> records)
    {
        var linear = records
            .Where(r => !r.IsPiece)
            .OrderBy(r => StatementBuilder.GetClassOrder(r.MaterialClass))
            .ThenBy(r => r.MaterialName, StringComparer.CurrentCulture)
            .ThenBy(r => r.Section, StringComparer.CurrentCulture)
            .ToList();

        WriteTitle(sheet, "Детализация замеров: трубопроводы, воздуховоды, кабели", LinearHeaders.Length);
        WriteHeader(sheet, LinearHeaders);

        var row = 3;
        for (var i = 0; i < linear.Count; i++)
        {
            var record = linear[i];

            sheet.Cell(row, 1).Value = i + 1;
            sheet.Cell(row, 2).Value = record.MaterialClassRu;
            sheet.Cell(row, 3).Value = record.MaterialName;
            sheet.Cell(row, 4).Value = record.Characteristic;
            sheet.Cell(row, 5).Value = record.Unit;
            SetNumber(sheet.Cell(row, 6), MeasurementRounding.RoundLength(record.HorizontalLengthM), "0.00");
            SetNumber(sheet.Cell(row, 7), MeasurementRounding.RoundLength(record.VerticalLengthM), "0.00");
            SetNumber(sheet.Cell(row, 8), record.LengthM, "0.00");

            // Площадь считается только для воздуховодов: у труб и кабелей
            // ячейка остаётся пустой, а не нулевой — ноль читался бы как замер.
            if (record.AreaPerMeterM2 > 0) SetNumber(sheet.Cell(row, 9), record.AreaM2, "0.00");

            SetNumber(sheet.Cell(row, 10), record.PolylineCount, "0");
            sheet.Cell(row, 11).Value = record.Section;
            sheet.Cell(row, 12).Value = record.LayerName;
            sheet.Cell(row, 13).Value = record.DrawingFileName;
            sheet.Cell(row, 14).Value = record.HasManualValue ? ManualValueMark : MeasuredValueMark;

            row++;
        }

        WriteTotals(sheet, linear.Count, LinearHeaders.Length, firstNumericColumn: 6, lastNumericColumn: 10);
        FinishSheet(sheet, linear.Count, LinearHeaders.Length, hasTotals: true);

        sheet.Column(1).Width = 6;
        sheet.Column(2).Width = 16;
        sheet.Column(3).Width = 50;
        sheet.Column(4).Width = 20;
        sheet.Column(5).Width = 10;
        foreach (var column in new[] { 6, 7, 8, 9 }) sheet.Column(column).Width = 14;
        sheet.Column(10).Width = 11;
        sheet.Column(11).Width = 18;
        sheet.Column(12).Width = 28;
        sheet.Column(13).Width = 24;
        sheet.Column(14).Width = 12;
        sheet.Column(3).Style.Alignment.WrapText = true;

        var linearLastRow = LastFilledRow(linear.Count, hasTotals: true);
        CenterFilledRange(sheet, linearLastRow, LinearHeaders.Length);
        AlignNameColumnLeft(sheet, column: 3, linearLastRow);
    }

    // ======================= Штучные изделия =======================

    private static void WritePieceDetails(IXLWorksheet sheet, IReadOnlyList<MeasurementRecord> records)
    {
        // Группировка по виду изделия задана порядком строк: отдельных
        // заголовков-разделителей нет, иначе итоговые формулы пришлось бы
        // разрывать на куски.
        var pieces = records
            .Where(r => r.IsPiece)
            .OrderBy(r => r.PieceKind, StringComparer.CurrentCulture)
            .ThenBy(r => r.MaterialName, StringComparer.CurrentCulture)
            .ThenBy(r => r.Section, StringComparer.CurrentCulture)
            .ToList();

        WriteTitle(sheet, "Детализация замеров: штучные изделия", PieceHeaders.Length);
        WriteHeader(sheet, PieceHeaders);

        var row = 3;
        for (var i = 0; i < pieces.Count; i++)
        {
            var record = pieces[i];

            sheet.Cell(row, 1).Value = i + 1;
            sheet.Cell(row, 2).Value = record.PieceKind;
            sheet.Cell(row, 3).Value = record.MaterialName;
            sheet.Cell(row, 4).Value = record.Characteristic;
            sheet.Cell(row, 5).Value = record.Unit;
            SetNumber(sheet.Cell(row, 6), record.Quantity, "0");
            sheet.Cell(row, 7).Value = record.Section;
            sheet.Cell(row, 8).Value = record.LayerName;
            sheet.Cell(row, 9).Value = record.DrawingFileName;
            sheet.Cell(row, 10).Value = record.HasManualValue ? ManualValueMark : MeasuredValueMark;

            row++;
        }

        WriteTotals(sheet, pieces.Count, PieceHeaders.Length, firstNumericColumn: 6, lastNumericColumn: 6);
        FinishSheet(sheet, pieces.Count, PieceHeaders.Length, hasTotals: true);

        sheet.Column(1).Width = 6;
        sheet.Column(2).Width = 32;
        sheet.Column(3).Width = 50;
        sheet.Column(4).Width = 20;
        sheet.Column(5).Width = 10;
        sheet.Column(6).Width = 13;
        sheet.Column(7).Width = 18;
        sheet.Column(8).Width = 28;
        sheet.Column(9).Width = 24;
        sheet.Column(10).Width = 12;
        sheet.Column(3).Style.Alignment.WrapText = true;

        var pieceLastRow = LastFilledRow(pieces.Count, hasTotals: true);
        CenterFilledRange(sheet, pieceLastRow, PieceHeaders.Length);
        AlignNameColumnLeft(sheet, column: 3, pieceLastRow);
    }

    // ======================= Спецификация =======================

    /// <summary>
    /// Описание одного столбца листа «Спецификация».
    /// Столбцы собираются по настройкам выгрузки, поэтому и заголовок,
    /// и способ получения значения хранятся вместе — иначе при выключении
    /// столбца поехали бы индексы ячеек.
    /// </summary>
    /// <param name="Header">Заголовок.</param>
    /// <param name="NumberFormat">Формат числа; null — текстовый столбец.</param>
    /// <param name="Value">Значение для строки свода.</param>
    /// <param name="Width">Ширина столбца.</param>
    private sealed record SpecificationSheetColumn(
        string Header,
        string? NumberFormat,
        Func<SpecificationSummaryRow, object?> Value,
        double Width);

    private static List<SpecificationSheetColumn> BuildSpecificationColumns(
        IReadOnlyList<string> drawings,
        SpecificationExportOptions options)
    {
        var columns = new List<SpecificationSheetColumn>();

        if (options.Has(SpecificationColumn.Number))
            columns.Add(new("п/п", null, r => r.Item.Number, 6));
        if (options.Has(SpecificationColumn.Name))
            columns.Add(new("Наименование материала", null, r => r.Item.Name, 50));
        if (options.Has(SpecificationColumn.Mark))
            columns.Add(new("Марка", null, r => r.Item.Mark, 18));
        if (options.Has(SpecificationColumn.EquipmentCode))
            columns.Add(new("Код оборудования", null, r => r.Item.EquipmentCode, 18));
        if (options.Has(SpecificationColumn.Manufacturer))
            columns.Add(new("Изготовитель", null, r => r.Item.Manufacturer, 22));
        if (options.Has(SpecificationColumn.Unit))
            columns.Add(new("Ед. изм.", null, r => r.Item.Unit, 10));
        if (options.Has(SpecificationColumn.Quantity))
            columns.Add(new("Кол-во по спецификации", "0.00", r => r.Item.Quantity, 16));

        foreach (var drawing in drawings)
        {
            var key = drawing;
            columns.Add(new(
                SpecificationSummaryBuilder.BuildCountColumnHeader(key),
                "0.00",
                // Ноль, а не пустая ячейка: столбец подсчёта суммируется
                // и вычитается, и дырки в нём ломали бы формулы.
                r => r.ByDrawing.TryGetValue(key, out var counted) ? counted : 0d,
                16));
        }

        if (options.Has(SpecificationColumn.Total))
            columns.Add(new("Всего подсчитано", "0.00", r => r.Total, 16));
        if (options.Has(SpecificationColumn.Difference))
            columns.Add(new("Расхождение", "0.00", r => r.Difference, 16));

        return columns;
    }

    private static void WriteSpecification(
        IXLWorksheet sheet,
        MeasurementJournal journal,
        Specification specification,
        SpecificationExportOptions options)
    {
        var drawings = SpecificationSummaryBuilder.GetDrawingColumns(journal);
        if (options.Drawings is not null)
            drawings = drawings.Where(options.Drawings.Contains).ToList();

        var summary = SpecificationSummaryBuilder.Build(journal, specification);

        // «Только проверенные»: строки без единого замера в выгрузку не идут.
        if (options.OnlyMeasured)
            summary = summary.Where(r => r.IsMeasured && r.Total > 0).ToList();

        var columns = BuildSpecificationColumns(drawings, options);
        if (columns.Count == 0) return;

        var title = $"Спецификация {specification.FileName}: проект и подсчёт по чертежам" +
                    (options.OnlyMeasured ? " (только проверенные позиции)" : string.Empty);

        WriteTitle(sheet, title, columns.Count);
        WriteHeader(sheet, columns.Select(c => c.Header).ToList());

        var row = 3;
        foreach (var line in summary)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];
                var cell = sheet.Cell(row, i + 1);
                var value = column.Value(line);

                if (column.NumberFormat is null) cell.Value = value?.ToString() ?? string.Empty;
                else SetNumber(cell, Convert.ToDouble(value), column.NumberFormat);
            }

            // Позиция с нераспознанной единицей замеру не поддаётся —
            // её видно сразу, чтобы не искать причину пустого подсчёта.
            if (!line.Item.IsSupported)
                sheet.Range(row, 1, row, columns.Count).Style.Fill.BackgroundColor = XLColor.LightYellow;

            row++;
        }

        var firstNumeric = columns.FindIndex(c => c.NumberFormat is not null);
        var lastNumeric = columns.FindLastIndex(c => c.NumberFormat is not null);

        if (firstNumeric >= 0)
            WriteTotals(sheet, summary.Count, columns.Count, firstNumeric + 1, lastNumeric + 1);

        FinishSheet(sheet, summary.Count, columns.Count, hasTotals: firstNumeric >= 0);

        for (var i = 0; i < columns.Count; i++) sheet.Column(i + 1).Width = columns[i].Width;

        var nameColumn = columns.FindIndex(c => c.Header == "Наименование материала");
        if (nameColumn >= 0) sheet.Column(nameColumn + 1).Style.Alignment.WrapText = true;

        var specificationLastRow = LastFilledRow(summary.Count, hasTotals: firstNumeric >= 0);
        CenterFilledRange(sheet, specificationLastRow, columns.Count);
        if (nameColumn >= 0) AlignNameColumnLeft(sheet, nameColumn + 1, specificationLastRow);
    }

    // ======================= Общее оформление =======================

    private static void WriteTitle(IXLWorksheet sheet, string title, int columnCount)
    {
        sheet.Range(1, 1, 1, columnCount).Merge();

        var cell = sheet.Cell(1, 1);
        cell.Value = title;
        cell.Style.Font.Bold = true;
        cell.Style.Font.FontSize = 12;
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
    }

    private static void WriteHeader(IXLWorksheet sheet, IReadOnlyList<string> headers)
    {
        for (var i = 0; i < headers.Count; i++)
            sheet.Cell(2, i + 1).Value = headers[i];

        var header = sheet.Range(2, 1, 2, headers.Count);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.LightGray;
        header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        header.Style.Alignment.WrapText = true;
        header.Style.Border.BottomBorder = XLBorderStyleValues.Medium;
    }

    /// <summary>
    /// Строка «ИТОГО» под данными: формулы SUM, а не готовые числа, — так итог
    /// пересчитается сам, если строки правят прямо в Excel.
    /// </summary>
    private static void WriteTotals(
        IXLWorksheet sheet,
        int dataRowCount,
        int columnCount,
        int firstNumericColumn,
        int lastNumericColumn)
    {
        if (dataRowCount == 0) return;

        var firstDataRow = 3;
        var lastDataRow = dataRowCount + 2;
        var totalsRow = lastDataRow + 1;

        // Подпись «ИТОГО» ставится в текстовой части строки. Если текстовых
        // столбцов не выбрано вовсе, подписывать нечего — остаются только суммы.
        if (firstNumericColumn > 1)
        {
            sheet.Cell(totalsRow, 1).Value = "ИТОГО";
            sheet.Range(totalsRow, 1, totalsRow, firstNumericColumn - 1).Merge();
        }

        for (var column = firstNumericColumn; column <= lastNumericColumn; column++)
        {
            var letter = XLHelper.GetColumnLetterFromNumber(column);
            var cell = sheet.Cell(totalsRow, column);

            cell.FormulaA1 = $"SUM({letter}{firstDataRow}:{letter}{lastDataRow})";
            cell.Style.NumberFormat.Format = sheet.Cell(firstDataRow, column).Style.NumberFormat.Format;
        }

        var totals = sheet.Range(totalsRow, 1, totalsRow, columnCount);
        totals.Style.Font.Bold = true;
        totals.Style.Border.TopBorder = XLBorderStyleValues.Medium;
        // Выравнивание строки итогов задаётся общим центрированием листа.
    }

    private static void FinishSheet(IXLWorksheet sheet, int dataRowCount, int columnCount, bool hasTotals)
    {
        sheet.SheetView.FreezeRows(2);

        if (dataRowCount == 0) return;

        // Границы охватывают шапку, данные и — на подробных листах — итоги.
        var lastRow = dataRowCount + 2 + (hasTotals ? 1 : 0);
        var data = sheet.Range(2, 1, lastRow, columnCount);
        data.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        data.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
    }

    private static void SetNumber(IXLCell cell, double value, string format)
    {
        cell.Value = value;
        cell.Style.NumberFormat.Format = format;
    }

    /// <summary>Последняя занятая строка листа: шапка, данные и, если есть, «ИТОГО».</summary>
    private static int LastFilledRow(int dataRowCount, bool hasTotals) =>
        dataRowCount == 0 ? 2 : dataRowCount + 2 + (hasTotals ? 1 : 0);

    /// <summary>
    /// Центрировать текст по горизонтали и вертикали во всей заполненной части
    /// листа — заголовок, шапка, данные, «ИТОГО».
    ///
    /// Задаются ровно два свойства выравнивания, поэтому перенос текста,
    /// форматы чисел, формулы, границы, заливки, ширины и объединения ячеек
    /// остаются как были. Вызывается последним: ширины и перенос назначаются
    /// на столбцы целиком, и стиль столбца, применённый после, перекрыл бы
    /// выравнивание ячеек.
    /// </summary>
    private static void CenterFilledRange(IXLWorksheet sheet, int lastRow, int columnCount)
    {
        if (lastRow < 1 || columnCount < 1) return;

        var filled = sheet.Range(1, 1, lastRow, columnCount);
        filled.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        filled.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
    }

    /// <summary>
    /// Наименование материала — единственный столбец, выровненный по левому
    /// краю: это единственная колонка со связным текстом, и по центру длинные
    /// наименования читаются рвано, особенно когда переносятся на две строки.
    /// По вертикали остаётся по центру, как везде, перенос текста сохраняется.
    ///
    /// Применяется к строкам данных; шапка остаётся отцентрованной вместе
    /// с остальными заголовками.
    /// </summary>
    private static void AlignNameColumnLeft(IXLWorksheet sheet, int column, int lastRow)
    {
        if (column < 1 || lastRow < 3) return;

        var cells = sheet.Range(3, column, lastRow, column);
        cells.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        cells.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        cells.Style.Alignment.WrapText = true;
    }
}
