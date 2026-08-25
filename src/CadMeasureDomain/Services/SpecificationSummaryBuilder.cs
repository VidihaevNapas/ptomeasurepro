using CadMeasureDomain.Models;

namespace CadMeasureDomain.Services;

/// <summary>
/// Строка свода по спецификации: позиция проекта и подсчёт по каждому чертежу,
/// в котором её замеряли.
/// </summary>
/// <param name="Item">Позиция спецификации.</param>
/// <param name="ByDrawing">Подсчёт по каждому DWG: имя файла → количество.</param>
/// <param name="Total">Сумма подсчётов по всем чертежам.</param>
/// <param name="IsMeasured">По позиции есть хотя бы один замер.</param>
public sealed record SpecificationSummaryRow(
    SpecificationItem Item,
    IReadOnlyDictionary<string, double> ByDrawing,
    double Total,
    bool IsMeasured)
{
    /// <summary>Расхождение с проектом: подсчитано минус по спецификации.</summary>
    public double Difference => MeasurementRounding.RoundLength(Total - Item.Quantity);
}

/// <summary>
/// Свод «спецификация × чертежи».
///
/// Замеры по одной позиции копятся в разных DWG: этаж в одном файле, кровля
/// в другом. Столбец на чертёж появляется сам, как только в этом чертеже
/// что-то замерили по позиции спецификации, — переключение между чертежами
/// в одной сессии AutoCAD не требует никаких действий от пользователя.
///
/// Свод считается по журналу на лету и нигде не хранится: журнал и так знает
/// свой чертёж в каждой записи, а второй источник этих же чисел неизбежно
/// разошёлся бы с первым.
/// </summary>
public static class SpecificationSummaryBuilder
{
    /// <summary>Приставка заголовка столбца подсчёта.</summary>
    public const string CountColumnPrefix = "Подсчёт по ";

    /// <summary>Заголовок столбца подсчёта для чертежа.</summary>
    public static string BuildCountColumnHeader(string drawingFileName) => CountColumnPrefix + drawingFileName;

    /// <summary>
    /// Чертежи, по которым есть подсчёты, в порядке появления в журнале.
    /// Порядок именно такой: столбцы не должны прыгать между выгрузками.
    /// </summary>
    public static IReadOnlyList<string> GetDrawingColumns(MeasurementJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);

        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in journal.Records)
        {
            if (!record.IsFromSpecification) continue;
            if (string.IsNullOrWhiteSpace(record.DrawingFileName)) continue;

            if (seen.Add(record.DrawingFileName)) columns.Add(record.DrawingFileName);
        }

        return columns;
    }

    /// <summary>
    /// Собрать свод: строка на каждую позицию спецификации, в строке —
    /// подсчёт по каждому чертежу. Позиции без замеров остаются в своде
    /// с нулями: спецификация — план, и незамеренная позиция важна не меньше
    /// замеренной.
    /// </summary>
    public static IReadOnlyList<SpecificationSummaryRow> Build(
        MeasurementJournal journal,
        Specification specification)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(specification);

        var rows = new List<SpecificationSummaryRow>(specification.Items.Count);

        foreach (var item in specification.Items)
        {
            var byDrawing = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var measured = false;

            foreach (var record in journal.Records)
            {
                if (record.SpecificationItemId != item.Number) continue;

                measured = true;
                var drawing = record.DrawingFileName ?? string.Empty;

                byDrawing.TryGetValue(drawing, out var accumulated);
                byDrawing[drawing] = accumulated + record.MeasuredQuantity;
            }

            // Округляется итог по чертежу, а не каждая запись: правило то же,
            // что и для длин в ведомости.
            foreach (var drawing in byDrawing.Keys.ToList())
                byDrawing[drawing] = MeasurementRounding.RoundLength(byDrawing[drawing]);

            var total = MeasurementRounding.RoundLength(byDrawing.Values.Sum());
            rows.Add(new SpecificationSummaryRow(item, byDrawing, total, measured));
        }

        return rows;
    }
}
