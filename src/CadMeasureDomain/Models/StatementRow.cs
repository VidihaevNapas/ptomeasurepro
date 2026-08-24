using System.Globalization;
using CadMeasureDomain.Services;

namespace CadMeasureDomain.Models;

/// <summary>
/// Строка ведомости смонтированного оборудования и материалов.
///
/// Ведомость — единственная форма вывода: одинаковая в таблице AutoCAD
/// и в Excel. Поэтому строка собирается один раз
/// (<see cref="StatementBuilder"/>), а получатели только раскладывают её
/// по ячейкам.
/// </summary>
/// <param name="Number">Порядковый номер, с 1.</param>
/// <param name="MaterialName">Наименование материала.</param>
/// <param name="Unit">Единица измерения: «м» либо «шт».</param>
/// <param name="Quantity">Длина в метрах либо количество штук.</param>
/// <param name="IsPiece">Штучное изделие — количество целое.</param>
public sealed record StatementRow(
    int Number,
    string MaterialName,
    string Unit,
    double Quantity,
    bool IsPiece)
{
    /// <summary>
    /// Количество для текстовой ячейки: у штучных целое, у линейных — с тремя
    /// знаками. В Excel вместо этого пишется число, чтобы с ним можно было
    /// продолжить считать.
    /// </summary>
    public string QuantityText => IsPiece
        ? ((int)Math.Round(Quantity)).ToString(CultureInfo.CurrentCulture)
        : Quantity.ToString(MeasurementRounding.LengthFormat, CultureInfo.CurrentCulture);
}
