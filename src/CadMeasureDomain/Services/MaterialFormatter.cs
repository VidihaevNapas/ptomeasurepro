using System.Globalization;
using System.Text;
using CadMeasureDomain.Models;

namespace CadMeasureDomain.Services;

/// <summary>
/// Единое место, где из характеристик материала собираются строки:
/// «характеристика» для журнала/Excel и «краткое обозначение» для имени слоя.
/// Вынесено в один класс, чтобы форматы нигде не разъезжались.
///
/// Числа выводятся по-разному в зависимости от назначения:
///   • характеристика — в локали пользователя («3,5 мм»), её читает инженер;
///   • код слоя — инвариантно («3.5»), потому что запятая в имени слоя
///     запрещена AutoCAD, а имя должно быть одинаковым на любой машине.
/// </summary>
public static class MaterialFormatter
{
    /// <summary>Число для показа человеку: «3,5» в русской локали, без хвостовых нулей.</summary>
    public static string FormatNumber(double value) =>
        value.ToString("0.###", CultureInfo.CurrentCulture);

    /// <summary>Число для имени слоя: всегда «3.5», независимо от локали.</summary>
    public static string FormatNumberInvariant(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Число для наименований каталога: всегда «3,5».
    /// Наименование — ключ материала, и оно не должно зависеть от того,
    /// в какой локали была сгенерирована номенклатура.
    /// </summary>
    public static string FormatNumberRu(double value) =>
        value.ToString("0.###", RussianCulture);

    private static readonly CultureInfo RussianCulture = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>
    /// Характеристика для журнала и Excel:
    ///   труба                 — «⌀89x4», «⌀88,9x3,5» либо «⌀32» (без толщины стенки);
    ///   воздуховод прямоуг.   — «1250x800, 1 мм» либо «1250x800»;
    ///   воздуховод круглый    — «⌀200, 0,7 мм»;
    ///   штучка                — «Dn15».
    /// </summary>
    public static string BuildCharacteristic(Material material)
    {
        if (material is null) return string.Empty;

        switch (material.Class)
        {
            case MaterialClasses.Pipe:
            {
                if (material.DiameterMm is null) return string.Empty;
                return material.WallThicknessMm is null
                    ? $"⌀{FormatNumber(material.DiameterMm.Value)}"
                    : $"⌀{FormatNumber(material.DiameterMm.Value)}x{FormatNumber(material.WallThicknessMm.Value)}";
            }

            case MaterialClasses.Duct:
            {
                var thickness = material.SheetThicknessMm is null
                    ? string.Empty
                    : $", {FormatNumber(material.SheetThicknessMm.Value)} мм";

                if (material.IsRoundDuct)
                    return $"⌀{FormatNumber(material.DiameterMm!.Value)}{thickness}";

                if (material.WidthMm is null || material.HeightMm is null) return string.Empty;
                return $"{material.WidthMm}x{material.HeightMm}{thickness}";
            }

            case MaterialClasses.Cable:
            {
                if (material.CoreCount is null || material.CrossSectionMm2 is null) return string.Empty;
                return $"{material.CoreCount}х{FormatNumber(material.CrossSectionMm2.Value)}";
            }

            case MaterialClasses.Piece:
                return material.NominalDiameterMm is null ? string.Empty : $"Dn{material.NominalDiameterMm}";

            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// Краткое обозначение материала для имени слоя:
    /// «D89x4», «D88.9x3.5», «1250x800_t1», «D200_t0.7», «Dn15».
    ///
    /// Метод НИКОГДА не возвращает пустую строку: если характеристик нет или
    /// после чистки запрещённых символов ничего не осталось, берётся устойчивый
    /// хеш наименования. Благодаря этому слой создаётся для любого материала,
    /// даже заполненного в реестре как попало.
    /// </summary>
    public static string BuildShortCode(Material material)
    {
        if (material is null) return "UNKNOWN";

        string code = material.Class switch
        {
            MaterialClasses.Pipe => BuildPipeCode(material),
            MaterialClasses.Duct => BuildDuctCode(material),
            MaterialClasses.Cable => BuildCableCode(material),
            MaterialClasses.Piece => material.NominalDiameterMm is null ? string.Empty : $"Dn{material.NominalDiameterMm}",
            _ => string.Empty
        };

        // Чистим ДО проверки на пустоту: строка вида «???» после чистки
        // превратится в «___», а вида «\\\» — может схлопнуться в пустую.
        code = LayerNameSanitizer.Sanitize(code);

        if (string.IsNullOrWhiteSpace(code))
            code = BuildFallbackCode(material);

        return code;
    }

    /// <summary>
    /// Запасное обозначение — только цифры и латиница, всегда пригодно для имени слоя.
    /// Считается от наименования, поэтому у одного материала оно постоянно,
    /// а у разных материалов различается.
    /// </summary>
    public static string BuildFallbackCode(Material material) =>
        "N" + StableHash(material?.Key ?? string.Empty).ToString("X6", CultureInfo.InvariantCulture);

    private static string BuildPipeCode(Material m)
    {
        if (m.DiameterMm is null) return string.Empty;

        return m.WallThicknessMm is null
            ? $"D{FormatNumberInvariant(m.DiameterMm.Value)}"
            : $"D{FormatNumberInvariant(m.DiameterMm.Value)}x{FormatNumberInvariant(m.WallThicknessMm.Value)}";
    }

    private static string BuildCableCode(Material m)
    {
        if (m.CoreCount is null || m.CrossSectionMm2 is null) return string.Empty;
        return $"{m.CoreCount}x{FormatNumberInvariant(m.CrossSectionMm2.Value)}";
    }

    private static string BuildDuctCode(Material m)
    {
        // Толщина листа входит в код: 1250x800 из стали 0,7 мм и 1,0 мм —
        // это разные позиции спецификации, и слои у них тоже должны быть разные.
        var thickness = m.SheetThicknessMm is null
            ? string.Empty
            : $"_t{FormatNumberInvariant(m.SheetThicknessMm.Value)}";

        if (m.IsRoundDuct)
            return $"D{FormatNumberInvariant(m.DiameterMm!.Value)}{thickness}";

        if (m.WidthMm is null || m.HeightMm is null) return string.Empty;
        return $"{m.WidthMm}x{m.HeightMm}{thickness}";
    }

    /// <summary>
    /// Детерминированный хеш строки (String.GetHashCode рандомизирован между запусками
    /// процесса, поэтому для имён слоёв и цветов он не годится).
    /// </summary>
    public static int StableHash(string? value)
    {
        if (string.IsNullOrEmpty(value)) return 0;

        unchecked
        {
            // FNV-1a 32 бита.
            const int offsetBasis = unchecked((int)2166136261);
            const int prime = 16777619;

            int hash = offsetBasis;
            var bytes = Encoding.UTF8.GetBytes(value);
            foreach (var b in bytes)
            {
                hash ^= b;
                hash *= prime;
            }

            return hash & 0x7FFFFFFF;
        }
    }
}
