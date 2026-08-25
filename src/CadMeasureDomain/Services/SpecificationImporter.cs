using CadMeasureDomain.Models;
using ClosedXML.Excel;

namespace CadMeasureDomain.Services;

/// <summary>
/// Чтение первоначальной спецификации из .xlsx.
///
/// Колонки заданы жёстко, по порядку из формы спецификации:
///   A — п/п (в файле может быть чем угодно, номер всё равно проставляется заново),
///   B — наименование материала,
///   C — марка,
///   D — код оборудования,
///   E — изготовитель,
///   F — единица измерения,
///   G — количество.
///
/// Заголовок не ищется по названиям: в реальных файлах шапка бывает
/// двухэтажной, с объединёнными ячейками и произвольными подписями.
/// Вместо этого пропускается всё, что не похоже на позицию: строка без
/// наименования либо с нечисловым количеством. Сколько строк пропущено —
/// видно в <see cref="Specification.SkippedRows"/>, чтобы потеря позиции
/// не осталась незамеченной.
/// </summary>
public static class SpecificationImporter
{
    private const int NameColumn = 2;
    private const int MarkColumn = 3;
    private const int EquipmentCodeColumn = 4;
    private const int ManufacturerColumn = 5;
    private const int UnitColumn = 6;
    private const int QuantityColumn = 7;

    /// <summary>Прочитать спецификацию из файла.</summary>
    /// <param name="path">Путь к .xlsx.</param>
    /// <param name="sheetName">Лист; если не задан — первый лист книги.</param>
    public static Specification Import(string path, string? sheetName = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Не задан путь к файлу спецификации.", nameof(path));

        if (!File.Exists(path))
            throw new FileNotFoundException($"Файл спецификации не найден: {path}", path);

        using var workbook = new XLWorkbook(path);

        var sheet = string.IsNullOrWhiteSpace(sheetName)
            ? workbook.Worksheets.First()
            : workbook.Worksheet(sheetName);

        var items = new List<SpecificationItem>();
        var skipped = 0;

        foreach (var row in sheet.RowsUsed())
        {
            var name = row.Cell(NameColumn).GetString().Trim();
            if (name.Length == 0)
            {
                skipped++;
                continue;
            }

            if (!TryReadQuantity(row.Cell(QuantityColumn), out var quantity))
            {
                // Шапка и подзаголовки разделов: наименование есть, количества нет.
                skipped++;
                continue;
            }

            items.Add(new SpecificationItem
            {
                Number = items.Count + 1,
                Name = name,
                Mark = row.Cell(MarkColumn).GetString().Trim(),
                EquipmentCode = row.Cell(EquipmentCodeColumn).GetString().Trim(),
                Manufacturer = row.Cell(ManufacturerColumn).GetString().Trim(),
                Unit = row.Cell(UnitColumn).GetString().Trim(),
                Quantity = quantity
            });
        }

        return new Specification
        {
            FileName = Path.GetFileName(path),
            Items = items,
            SkippedRows = skipped
        };
    }

    /// <summary>
    /// Прочитать количество. Число берётся как есть, текст разбирается
    /// тем же правилом, что и характеристики материалов: «3,5» и «3.5»
    /// одинаково допустимы, потому что файл делали руками в русской локали.
    /// </summary>
    private static bool TryReadQuantity(IXLCell cell, out double quantity)
    {
        quantity = 0;

        if (cell.IsEmpty()) return false;

        if (cell.DataType == XLDataType.Number)
        {
            quantity = cell.GetDouble();
            return true;
        }

        return FlexibleNullableDoubleConverter.TryParse(cell.GetString(), out quantity);
    }
}
