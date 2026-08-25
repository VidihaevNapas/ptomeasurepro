namespace CadMeasureDomain.Services;

/// <summary>Столбцы листа «Спецификация», которые можно включать и выключать.</summary>
public enum SpecificationColumn
{
    Number,
    Name,
    Mark,
    EquipmentCode,
    Manufacturer,
    Unit,
    Quantity,
    Total,
    Difference
}

/// <summary>
/// Что попадёт в книгу Excel: какие листы, какие столбцы спецификации
/// и какие её строки.
///
/// Настройки экспорта намеренно отделены от того, что показано в палитре:
/// столбцы в таблице журнала пользователь скрывает, чтобы не мешали работать,
/// а в выгрузке состав столбцов определяется тем, кому эта выгрузка уходит.
/// Смешивать эти две вещи — верный способ однажды отдать заказчику
/// половину ведомости.
/// </summary>
public sealed class SpecificationExportOptions
{
    /// <summary>Полный набор столбцов — используется по умолчанию.</summary>
    public static readonly IReadOnlyList<SpecificationColumn> AllColumns =
        Enum.GetValues<SpecificationColumn>();

    /// <summary>Лист «Ведомость».</summary>
    public bool IncludeStatement { get; init; } = true;

    /// <summary>Лист «Линейные материалы».</summary>
    public bool IncludeLinearDetails { get; init; } = true;

    /// <summary>Лист «Штучные изделия».</summary>
    public bool IncludePieceDetails { get; init; } = true;

    /// <summary>Лист «Спецификация». Без загруженной спецификации не выводится в любом случае.</summary>
    public bool IncludeSpecification { get; init; } = true;

    /// <summary>Столбцы листа «Спецификация» кроме подсчётов по чертежам.</summary>
    public IReadOnlyCollection<SpecificationColumn> Columns { get; init; } = AllColumns;

    /// <summary>
    /// Чертежи, подсчёты по которым выводятся отдельными столбцами.
    /// null — все чертежи, по которым есть замеры.
    /// </summary>
    public IReadOnlyCollection<string>? Drawings { get; init; }

    /// <summary>
    /// Выводить только проверенные позиции — те, по которым что-то замерено.
    /// Незамеренные строки спецификации при этом не попадают в выгрузку вовсе.
    /// </summary>
    public bool OnlyMeasured { get; init; }

    /// <summary>Настройки по умолчанию: вся книга целиком.</summary>
    public static SpecificationExportOptions Default { get; } = new();

    /// <summary>Выбран ли хотя бы один лист.</summary>
    public bool HasAnySheet =>
        IncludeStatement || IncludeLinearDetails || IncludePieceDetails || IncludeSpecification;

    /// <summary>Столбец включён в выгрузку.</summary>
    public bool Has(SpecificationColumn column) => Columns.Contains(column);
}
