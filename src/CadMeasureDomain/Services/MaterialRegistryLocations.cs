namespace CadMeasureDomain.Services;

/// <summary>Откуда взялся загруженный реестр материалов.</summary>
public enum MaterialRegistrySource
{
    /// <summary>Реестр не загружался.</summary>
    None,

    /// <summary>Файл из папки текущего DWG — реестр под конкретный объект.</summary>
    DrawingFolder,

    /// <summary>Пользовательский реестр (папка данных пользователя).</summary>
    UserData,

    /// <summary>Пользовательского файла не было — создан копированием шаблона из bundle.</summary>
    SeededFromTemplate,

    /// <summary>Не было ни файла, ни шаблона — создан из встроенного каталога.</summary>
    SeededFromCatalog
}

/// <summary>
/// Где искать и куда писать реестр материалов.
///
/// Разделение важно для обновления плагина: bundle целиком заменяется новой
/// версией, поэтому ничего пользовательского внутри него храниться не должно.
/// Файл в bundle — только шаблон для первого запуска.
/// </summary>
public sealed class MaterialRegistryLocations
{
    /// <summary>
    /// Папка активного DWG. Если там лежит materials.json, он побеждает —
    /// это реестр под конкретный объект.
    /// </summary>
    public string? DrawingDirectory { get; init; }

    /// <summary>
    /// Папка данных пользователя (%APPDATA%\PTOMeasurePro). Сюда пишутся
    /// все изменения реестра, и она переживает обновление и удаление bundle.
    /// </summary>
    public required string UserDataDirectory { get; init; }

    /// <summary>
    /// Стартовый materials.json внутри bundle. Используется ТОЛЬКО как шаблон
    /// при первом запуске и никогда не перезаписывается.
    /// </summary>
    public string? TemplatePath { get; init; }
}
