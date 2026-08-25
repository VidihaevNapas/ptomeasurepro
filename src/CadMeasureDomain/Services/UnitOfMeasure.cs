using CadMeasureDomain.Models;

namespace CadMeasureDomain.Services;

/// <summary>
/// Разбор единицы измерения из спецификации в тип замера.
///
/// Единицу пишет человек в Excel, поэтому вариантов написания много:
/// «шт», «шт.», «штук», «м», «м.п.», «мп», «пог.м», «пог. м», «погонный метр».
/// Всё это сводится к двум способам замера — длина по полилиниям и счёт
/// кругов, — а незнакомые единицы честно помечаются неподдерживаемыми:
/// молча угадать способ замера для «компл.» или «кг» нельзя.
/// </summary>
public static class UnitOfMeasure
{
    private static readonly HashSet<string> Pieces = new(StringComparer.Ordinal)
    {
        "шт", "штук", "штука", "штуки", "штп"
    };

    private static readonly HashSet<string> Linear = new(StringComparer.Ordinal)
    {
        "м", "мп", "пм", "погм", "погонныйметр", "погонныеметры", "метр", "метры", "метрпогонный"
    };

    /// <summary>Определить тип замера по единице измерения.</summary>
    public static MeasurementType Parse(string? unit)
    {
        var key = Normalize(unit);
        if (key.Length == 0) return MeasurementType.Unsupported;

        if (Pieces.Contains(key)) return MeasurementType.Pieces;
        if (Linear.Contains(key)) return MeasurementType.Linear;

        return MeasurementType.Unsupported;
    }

    /// <summary>Единица распознана и позицию можно замерить.</summary>
    public static bool IsSupported(string? unit) => Parse(unit) != MeasurementType.Unsupported;

    /// <summary>
    /// Привести написание к сравнимому виду: нижний регистр, без точек,
    /// пробелов и «ё». Именно эти три вещи и различают «Пог. М», «пог.м»
    /// и «погонный метр».
    /// </summary>
    private static string Normalize(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit)) return string.Empty;

        var result = new System.Text.StringBuilder(unit.Length);
        foreach (var symbol in unit.ToLowerInvariant())
        {
            if (char.IsWhiteSpace(symbol) || symbol is '.' or ',' or '-') continue;
            result.Append(symbol == 'ё' ? 'е' : symbol);
        }

        return result.ToString();
    }
}
