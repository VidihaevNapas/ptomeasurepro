using CadMeasureDomain.Models;

namespace CadMeasureDomain.Services;

/// <summary>
/// Площадь поверхности воздуховода — квадратные метры на метр длины.
///
/// Считается только для воздуховодов: у труб и кабелей площадь в ведомости
/// не нужна, и колонка у них остаётся пустой.
///
/// Возвращается именно удельная площадь, а не готовая: длина записи журнала
/// меняется и после замера — пересчётом по чертежу, вводом вертикальных
/// участков, ручной правкой в таблице. Храня коэффициент, запись получает
/// верную площадь при любом из этих изменений, не обращаясь к реестру
/// материалов повторно.
/// </summary>
public static class DuctAreaCalculator
{
    /// <summary>
    /// Удельная площадь, м² на метр длины:
    ///   прямоугольный воздуховод — (Ширина + Высота) * 2 / 1000;
    ///   круглый — π * Диаметр / 1000.
    /// Для остальных классов и для воздуховодов без размеров — 0.
    /// </summary>
    public static double GetAreaPerMeterM2(Material? material)
    {
        if (material is null || material.Class != MaterialClasses.Duct) return 0;

        if (material.IsRoundDuct)
            return material.DiameterMm is > 0 ? Math.PI * material.DiameterMm.Value / 1000.0 : 0;

        if (material.WidthMm is > 0 && material.HeightMm is > 0)
            return (material.WidthMm.Value + material.HeightMm.Value) * 2.0 / 1000.0;

        return 0;
    }
}
