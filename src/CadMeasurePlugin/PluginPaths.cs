using System.IO;
using System.Reflection;

namespace CadMeasurePlugin;

/// <summary>
/// Пути установки и пользовательских данных.
///
/// Плагин ставится как bundle:
///   %PROGRAMDATA%\Autodesk\ApplicationPlugins\PTOMeasurePro.bundle\
///       PackageContents.xml
///       Contents\CadMeasurePlugin.dll, CadMeasureDomain.dll, materials.json, ...
///
/// Папка bundle принадлежит установщику: при обновлении она заменяется целиком,
/// при удалении — стирается. Поэтому плагин НИЧЕГО туда не пишет.
/// Всё пользовательское живёт в <see cref="UserDataDirectory"/>.
/// </summary>
public static class PluginPaths
{
    /// <summary>Имя папки bundle — то же, что видит AutoCAD в ApplicationPlugins.</summary>
    public const string BundleFolderName = "PTOMeasurePro.bundle";

    /// <summary>Имя папки с пользовательскими данными.</summary>
    public const string UserDataFolderName = "PTOMeasurePro";

    /// <summary>
    /// Папка, где лежит dll плагина — это ...\PTOMeasurePro.bundle\Contents.
    /// Только для чтения: шрифты, шаблоны, ресурсы.
    /// </summary>
    public static string PluginDirectory =>
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Environment.CurrentDirectory;

    /// <summary>
    /// Папка пользовательских данных: %APPDATA%\PTOMeasurePro.
    /// Переживает обновление и удаление bundle — именно поэтому реестр
    /// материалов хранится здесь, а не рядом с dll.
    /// </summary>
    public static string UserDataDirectory
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            // На всякий случай: если %APPDATA% недоступен (нестандартный профиль),
            // откатываемся на временную папку, чтобы плагин не падал на старте.
            if (string.IsNullOrWhiteSpace(appData)) appData = Path.GetTempPath();

            return Path.Combine(appData, UserDataFolderName);
        }
    }

    /// <summary>Рабочий реестр материалов пользователя.</summary>
    public static string UserRegistryPath =>
        Path.Combine(UserDataDirectory, CadMeasureDomain.Services.MaterialRepository.FileName);

    /// <summary>
    /// Стартовый реестр внутри bundle. Используется только как шаблон
    /// при первом запуске; плагин его не изменяет.
    /// </summary>
    public static string TemplateRegistryPath =>
        Path.Combine(PluginDirectory, CadMeasureDomain.Services.MaterialRepository.FileName);

    /// <summary>
    /// Куда класть выгрузки Excel, если чертёж ещё ни разу не сохранён
    /// и папки рядом с DWG попросту нет.
    /// </summary>
    public static string ExportFallbackDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "PTO Measure Pro");
}
