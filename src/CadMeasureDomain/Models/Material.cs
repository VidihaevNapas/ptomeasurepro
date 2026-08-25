using System.Text.Json.Serialization;
using CadMeasureDomain.Services;

namespace CadMeasureDomain.Models;

/// <summary>
/// Классы материалов, которые понимает плагин.
/// Строковые константы, а не enum, — чтобы в materials.json можно было
/// добавлять новые классы без пересборки dll.
/// </summary>
public static class MaterialClasses
{
    public const string Pipe = "Pipe";
    public const string Duct = "Duct";
    public const string Cable = "Cable";
    public const string Piece = "Piece";

    /// <summary>Классы, которые меряются длиной по полилиниям.</summary>
    public static readonly string[] Linear = { Pipe, Duct, Cable };

    /// <summary>Все классы в порядке вкладок окна выбора материала.</summary>
    public static readonly string[] All = { Pipe, Duct, Cable, Piece };

    /// <summary>Меряется ли класс длиной (в отличие от штучного счёта).</summary>
    public static bool IsLinear(string? materialClass) =>
        materialClass is Pipe or Duct or Cable;

    /// <summary>Русское название класса для UI и Excel.</summary>
    public static string ToRussian(string? materialClass) => materialClass switch
    {
        Pipe => "Трубопровод",
        Duct => "Воздуховод",
        Cable => "Кабель",
        Piece => "Штучное изделие",
        _ => materialClass ?? string.Empty
    };

    /// <summary>Название вкладки в окне выбора материала.</summary>
    public static string ToTabTitle(string? materialClass) => materialClass switch
    {
        Pipe => "Трубопроводы",
        Duct => "Воздуховоды",
        Cable => "Кабельная продукция",
        Piece => "Штучные изделия",
        _ => materialClass ?? string.Empty
    };
}

/// <summary>
/// Позиция реестра материалов (materials.json).
/// Все числовые характеристики nullable — заполняются только те,
/// которые имеют смысл для конкретного класса материала.
///
/// Размеры, которым нужна точность (диаметр трубы 88,9; толщина стенки 3,5;
/// толщина листа 0,7; сечение жилы 2,5), хранятся как <see cref="double"/>.
/// Размеры, которые по определению целые (стороны прямоугольного воздуховода,
/// условный проход Dn, число жил), остались <see cref="int"/>.
/// </summary>
public sealed class Material
{
    /// <summary>"Pipe" | "Duct" | "Cable" | "Piece" (в будущем возможны другие значения).</summary>
    public string Class { get; set; } = MaterialClasses.Pipe;

    /// <summary>Полное наименование материала — ровно как в спецификации.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Единица измерения: «м.п.» или «шт.».</summary>
    public string Unit { get; set; } = "м.п.";

    /// <summary>
    /// Наружный диаметр трубы либо диаметр круглого воздуховода, мм.
    /// Дробный: у стальных труб встречается 88,9 и 114,3.
    /// </summary>
    [JsonConverter(typeof(FlexibleNullableDoubleConverter))]
    public double? DiameterMm { get; set; }

    /// <summary>Толщина стенки трубы, мм. Дробная: 2,8 / 3,5 / 4,5.</summary>
    [JsonConverter(typeof(FlexibleNullableDoubleConverter))]
    public double? WallThicknessMm { get; set; }

    /// <summary>Ширина прямоугольного воздуховода, мм.</summary>
    public int? WidthMm { get; set; }

    /// <summary>Высота прямоугольного воздуховода, мм.</summary>
    public int? HeightMm { get; set; }

    /// <summary>Толщина листа воздуховода, мм. Дробная: 0,5 / 0,7 / 0,9.</summary>
    [JsonConverter(typeof(FlexibleNullableDoubleConverter))]
    public double? SheetThicknessMm { get; set; }

    /// <summary>Условный проход штучного изделия (Dn), мм. По определению целый.</summary>
    public int? NominalDiameterMm { get; set; }

    /// <summary>Количество жил кабеля.</summary>
    public int? CoreCount { get; set; }

    /// <summary>Сечение жилы кабеля, мм². Дробное: 1,5 / 2,5 / 0,75.</summary>
    [JsonConverter(typeof(FlexibleNullableDoubleConverter))]
    public double? CrossSectionMm2 { get; set; }

    /// <summary>
    /// Марка изделия по спецификации проекта. Заполняется у позиций, заведённых
    /// из спецификации; на замер и на имя слоя не влияет.
    /// </summary>
    public string? Mark { get; set; }

    /// <summary>
    /// Изготовитель по спецификации проекта. Как и марка — справочное поле:
    /// два материала с одинаковым наименованием и разными изготовителями
    /// остаются одной позицией реестра, потому что ключ реестра — наименование.
    /// </summary>
    public string? Manufacturer { get; set; }

    /// <summary>
    /// Вид штучного изделия: «Фасонные изделия», «Запорная арматура»,
    /// «Фланцы и заглушки», «Фасонные изделия вентиляции», «Оборудование».
    /// Используется для группировки в Excel; на замер не влияет.
    /// </summary>
    public string? PieceKind { get; set; }

    /// <summary>Краткая характеристика для UI/Excel. Не сериализуется в json.</summary>
    [JsonIgnore]
    public string Characteristic => MaterialFormatter.BuildCharacteristic(this);

    /// <summary>Русское название класса. Не сериализуется в json.</summary>
    [JsonIgnore]
    public string ClassRu => MaterialClasses.ToRussian(Class);

    /// <summary>
    /// Круглый ли воздуховод: задан диаметр, а не пара «ширина+высота».
    /// От этого зависит и характеристика, и формула площади поверхности.
    /// </summary>
    [JsonIgnore]
    public bool IsRoundDuct =>
        Class == MaterialClasses.Duct && DiameterMm is > 0 && (WidthMm is null || HeightMm is null);

    /// <summary>Меряется ли материал длиной (труба, воздуховод, кабель).</summary>
    [JsonIgnore]
    public bool IsLinear => MaterialClasses.IsLinear(Class);

    /// <summary>
    /// Ключ материала для журнала и реестра слоёв.
    /// Наименование уникально в пределах реестра — поиск и навигация идут по Name.
    /// </summary>
    [JsonIgnore]
    public string Key => Name.Trim();

    public override string ToString() => Name;
}
