namespace CadMeasureDomain.Services;

/// <summary>
/// Единое правило округления длин.
///
/// Округляется ИТОГОВОЕ значение — уже после суммирования всех полилиний слоя
/// и вертикальных участков. Округлять каждую полилинию отдельно нельзя:
/// на сотне отрезков ошибка округления накапливается и итог в ведомости
/// перестаёт сходиться с суммой по чертежу.
///
/// Сами длины хранятся с полной точностью; округление применяется только
/// к тому, что попадает в журнал, таблицу и Excel.
/// </summary>
public static class MeasurementRounding
{
    /// <summary>Знаков после запятой в длине: 0,01 м.</summary>
    public const int LengthDecimals = 2;

    /// <summary>Формат длины для текстовых ячеек и подписей.</summary>
    public const string LengthFormat = "N2";

    /// <summary>
    /// Округлить длину в метрах до 0,01.
    /// MidpointRounding.AwayFromZero, а не банковское округление по умолчанию:
    /// в спецификации 12,345 должно давать 12,35, а не 12,34.
    /// </summary>
    public static double RoundLength(double meters) =>
        Math.Round(meters, LengthDecimals, MidpointRounding.AwayFromZero);
}
