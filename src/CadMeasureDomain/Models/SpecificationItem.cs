using CadMeasureDomain.Services;

namespace CadMeasureDomain.Models;

/// <summary>Как замеряется позиция: длиной по полилиниям либо счётом кругов.</summary>
public enum MeasurementType
{
    /// <summary>Единица измерения не распознана — замерять нечем.</summary>
    Unsupported,

    /// <summary>Погонные метры: замер полилиниями.</summary>
    Linear,

    /// <summary>Штуки: замер кругами-маркерами.</summary>
    Pieces
}

/// <summary>
/// Позиция первоначальной спецификации — той, что приходит от проектировщика
/// до начала замеров.
///
/// Спецификация здесь первоисточник: она задаёт состав позиций, единицы
/// и проектное количество, а замер по чертежу отвечает на вопрос, сколько
/// смонтировано фактически.
///
/// Позиция не привязана к реестру материалов: наименование в спецификации
/// может не совпасть ни с одной позицией реестра, и это нормальная ситуация,
/// которую разбирает человек.
/// </summary>
public sealed class SpecificationItem
{
    /// <summary>Порядковый номер. Проставляется при импорте, а не берётся из файла.</summary>
    public int Number { get; set; }

    /// <summary>Наименование материала — по нему идёт сопоставление с реестром.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Марка.</summary>
    public string Mark { get; set; } = string.Empty;

    /// <summary>Код оборудования.</summary>
    public string EquipmentCode { get; set; } = string.Empty;

    /// <summary>Изготовитель.</summary>
    public string Manufacturer { get; set; } = string.Empty;

    /// <summary>Единица измерения ровно как в файле спецификации.</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>Проектное количество.</summary>
    public double Quantity { get; set; }

    /// <summary>
    /// Наименование материала реестра, которым замеряется позиция.
    ///
    /// Отдельного идентификатора у материала нет: ключ реестра — наименование
    /// (<see cref="Material.Key"/>), по нему идут и поиск, и журнал, и слои.
    /// Обычно совпадает с <see cref="Name"/>; отличается, если позицию привязали
    /// к материалу реестра с другим написанием наименования.
    /// Пусто, пока сопоставление не выполнялось.
    /// </summary>
    public string MaterialName { get; set; } = string.Empty;

    /// <summary>Позиции сопоставлен материал реестра.</summary>
    public bool HasMaterial => !string.IsNullOrWhiteSpace(MaterialName);

    /// <summary>Тип замера, выведенный из единицы измерения.</summary>
    public MeasurementType MeasurementType => UnitOfMeasure.Parse(Unit);

    /// <summary>Позицию можно замерить: единица измерения распознана.</summary>
    public bool IsSupported => MeasurementType != MeasurementType.Unsupported;

    /// <summary>Замеряется длиной.</summary>
    public bool IsLinear => MeasurementType == MeasurementType.Linear;

    public override string ToString() => $"{Number}. {Name}";
}

/// <summary>
/// Импортированная спецификация целиком: позиции и имя файла, из которого
/// они пришли. Имя файла попадает в записи журнала — по нему видно,
/// какой спецификацией подтверждён замер.
/// </summary>
public sealed class Specification
{
    /// <summary>Имя файла спецификации без пути.</summary>
    public required string FileName { get; init; }

    /// <summary>Позиции в порядке следования в файле.</summary>
    public required IReadOnlyList<SpecificationItem> Items { get; init; }

    /// <summary>
    /// Сколько строк файла пропущено: пустые, служебные и заголовочные.
    /// Показывается пользователю — иначе молча потерянные позиции
    /// обнаружились бы только при сверке итогов.
    /// </summary>
    public int SkippedRows { get; init; }

    /// <summary>Позиции с нераспознанной единицей измерения: замерять их нечем.</summary>
    public IReadOnlyList<SpecificationItem> UnsupportedItems =>
        Items.Where(i => !i.IsSupported).ToList();

    /// <summary>Найти позицию по номеру.</summary>
    public SpecificationItem? FindByNumber(int number) => Items.FirstOrDefault(i => i.Number == number);

    /// <summary>
    /// Найти позицию по наименованию материала — так замер связывается
    /// со строкой спецификации. Сравнение без учёта регистра и внешних
    /// пробелов: в спецификациях они встречаются постоянно.
    /// </summary>
    public SpecificationItem? FindByName(string? materialName)
    {
        if (string.IsNullOrWhiteSpace(materialName)) return null;

        var key = materialName.Trim();
        return Items.FirstOrDefault(i => string.Equals(i.Name, key, StringComparison.OrdinalIgnoreCase));
    }
}
