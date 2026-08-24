using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CadMeasurePlugin;

/// <summary>Настройки, которые пользователь переключает в палитре.</summary>
public sealed class UserSettings
{
    /// <summary>Создавать ли подпись длины для новых замерных полилиний.</summary>
    public bool ShowPolylineLengthLabels { get; set; } = true;
}

/// <summary>
/// Хранилище пользовательских настроек: %APPDATA%\PTOMeasurePro\settings.json.
///
/// Файл лежит рядом с реестром материалов и вне bundle — значит переживает
/// обновление и удаление плагина. Значения читаются один раз за сессию
/// и пишутся сразу при изменении: настроек мало, а потерять переключатель
/// из-за аварийного закрытия AutoCAD неприятно.
///
/// Ошибки чтения и записи гасятся: настройка оформления не повод ломать работу.
/// </summary>
public static class UserSettingsStore
{
    private const string FileName = "settings.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly object Sync = new();
    private static UserSettings? _current;

    /// <summary>Путь к файлу настроек.</summary>
    public static string FilePath => Path.Combine(PluginPaths.UserDataDirectory, FileName);

    /// <summary>Текущие настройки. Читаются с диска при первом обращении.</summary>
    public static UserSettings Current
    {
        get
        {
            lock (Sync)
            {
                return _current ??= Load();
            }
        }
    }

    /// <summary>Записать настройки на диск.</summary>
    public static void Save()
    {
        lock (Sync)
        {
            if (_current is null) return;

            try
            {
                Directory.CreateDirectory(PluginPaths.UserDataDirectory);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_current, Options));
            }
            catch (Exception)
            {
                // Папка недоступна или файл занят — работаем со значением в памяти.
            }
        }
    }

    private static UserSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new UserSettings();

            return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath)) ?? new UserSettings();
        }
        catch (Exception)
        {
            // Битый или недоступный файл — берём значения по умолчанию.
            return new UserSettings();
        }
    }
}
