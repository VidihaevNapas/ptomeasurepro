using CadMeasureDomain.Models;

namespace CadMeasureDomain.Services;

/// <summary>
/// Сборка ведомости из журнала замеров.
///
/// Единственное место, где заданы состав, группировка и порядок строк.
/// И таблица AutoCAD, и выгрузка в Excel берут строки отсюда — иначе два
/// представления одной ведомости неизбежно разошлись бы: где-то поправили
/// сортировку, где-то забыли.
/// </summary>
public static class StatementBuilder
{
    /// <summary>Заголовок ведомости — общий для чертежа и Excel.</summary>
    public const string Title = "Ведомость смонтированного оборудования и материалов";

    /// <summary>Заголовки столбцов ведомости.</summary>
    public static readonly string[] ColumnHeaders =
    {
        "п/п",
        "Наименование материала",
        "Ед. изм.",
        "Кол-во"
    };

    /// <summary>Единица линейных материалов: труб, воздуховодов, кабелей.</summary>
    public const string LinearUnit = "м";

    /// <summary>Единица штучных изделий.</summary>
    public const string PieceUnit = "шт";

    /// <summary>
    /// Собрать ведомость по записям одного чертежа.
    ///
    /// Группировка: «наименование + единица + участок» в пределах указанного
    /// DWG. Участок в ведомость не выводится, но входит в ключ — значит один
    /// материал, замеренный на двух участках, даёт две строки.
    ///
    /// Порядок: сначала линейные материалы (трубы, воздуховоды, кабели),
    /// затем штучные; внутри — по наименованию, при совпадении — по участку.
    /// </summary>
    public static IReadOnlyList<StatementRow> Build(MeasurementJournal journal, string drawingFileName)
    {
        ArgumentNullException.ThrowIfNull(journal);

        // DWG в ключе группировки задан самой выборкой: берём записи одного чертежа.
        var source = journal.GetRecordsForDrawing(drawingFileName ?? string.Empty);

        return source
            .Select(record => new
            {
                Record = record,
                Unit = GetUnit(record),
                Order = GetClassOrder(record.MaterialClass),
                Value = GetQuantity(record)
            })
            .GroupBy(item => (
                Name: item.Record.MaterialName.Trim().ToUpperInvariant(),
                item.Unit,
                Section: item.Record.Section.Trim().ToUpperInvariant()))
            .Select(group => new
            {
                Sample = group.First(),
                Quantity = group.Sum(item => item.Value)
            })
            .OrderBy(row => row.Sample.Order)
            .ThenBy(row => row.Sample.Record.MaterialName, StringComparer.CurrentCulture)
            .ThenBy(row => row.Sample.Record.Section, StringComparer.CurrentCulture)
            .Select((row, index) => new StatementRow(
                index + 1,
                row.Sample.Record.MaterialName,
                row.Sample.Unit,
                // Округляем сумму по группе, а не каждое слагаемое.
                row.Sample.Record.IsPiece ? row.Quantity : MeasurementRounding.RoundLength(row.Quantity),
                row.Sample.Record.IsPiece))
            .ToList();
    }

    /// <summary>
    /// Единица измерения ведомости.
    /// Берётся по классу материала, а не из реестра: в ведомости нужны
    /// ровно «м» и «шт», тогда как в реестре встречаются «м.п.» и «шт.».
    /// </summary>
    private static string GetUnit(MeasurementRecord record) =>
        record.IsPiece ? PieceUnit : LinearUnit;

    private static double GetQuantity(MeasurementRecord record) =>
        record.IsPiece ? record.Quantity : record.LengthM;

    /// <summary>Порядок групп: трубы, воздуховоды, кабели, штучные изделия.</summary>
    private static int GetClassOrder(string? materialClass) => materialClass switch
    {
        MaterialClasses.Pipe => 0,
        MaterialClasses.Duct => 1,
        MaterialClasses.Cable => 2,
        MaterialClasses.Piece => 3,
        _ => 4
    };
}
