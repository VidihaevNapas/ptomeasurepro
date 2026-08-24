using System.Globalization;
using CadMeasureDomain.Models;

namespace CadMeasureDomain.Tests;

/// <summary>
/// Заготовки материалов для тестов.
///
/// Значения взяты настоящие (труба ⌀88,9x3,5, воздуховод 1250x800 из стали
/// 0,9 мм, кабель ВВГнг 3х2,5): дробные характеристики и разные классы —
/// как раз те места, где ломались форматирование и имена слоёв.
/// </summary>
internal static class TestData
{
    public static Material Pipe(string name = "Труба стальная ⌀89x4", double? diameter = 89, double? wall = 4) =>
        new()
        {
            Class = MaterialClasses.Pipe,
            Name = name,
            Unit = "м.п.",
            DiameterMm = diameter,
            WallThicknessMm = wall
        };

    public static Material RectDuct(
        string name = "Воздуховод 1250x800, сталь 0,9 мм",
        int? width = 1250,
        int? height = 800,
        double? sheet = 0.9) =>
        new()
        {
            Class = MaterialClasses.Duct,
            Name = name,
            Unit = "м.п.",
            WidthMm = width,
            HeightMm = height,
            SheetThicknessMm = sheet
        };

    public static Material RoundDuct(
        string name = "Воздуховод ⌀200, сталь 0,7 мм",
        double? diameter = 200,
        double? sheet = 0.7) =>
        new()
        {
            Class = MaterialClasses.Duct,
            Name = name,
            Unit = "м.п.",
            DiameterMm = diameter,
            SheetThicknessMm = sheet
        };

    public static Material Cable(string name = "Кабель ВВГнг(А)-LS 3х2,5", int? cores = 3, double? section = 2.5) =>
        new()
        {
            Class = MaterialClasses.Cable,
            Name = name,
            Unit = "м.п.",
            CoreCount = cores,
            CrossSectionMm2 = section
        };

    public static Material Piece(
        string name = "Отвод стальной Dn15",
        int? nominalDiameter = 15,
        string kind = "Фасонные изделия трубопроводов") =>
        new()
        {
            Class = MaterialClasses.Piece,
            Name = name,
            Unit = "шт.",
            NominalDiameterMm = nominalDiameter,
            PieceKind = kind
        };
}

/// <summary>
/// Временная подмена культуры потока.
///
/// Часть форматов домена (характеристика материала, колонка «Кол-во»)
/// намеренно зависит от локали пользователя. Без фиксации культуры такие
/// тесты проходили бы на русской машине и падали на английской — то есть
/// в CI, — поэтому культура задаётся явно.
/// </summary>
internal sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _previous;

    public CultureScope(string culture)
    {
        _previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
    }

    public void Dispose() => CultureInfo.CurrentCulture = _previous;
}

/// <summary>
/// Временная папка, которая удаляется по завершении теста.
/// Нужна тестам реестра материалов и выгрузки в Excel: они работают
/// с настоящими файлами, а не с моками файловой системы.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "CadMeasureTests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) =>
        System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Файл мог остаться заблокированным — на результат теста это не влияет.
        }
    }
}
