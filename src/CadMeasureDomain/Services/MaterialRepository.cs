using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CadMeasureDomain.Models;

namespace CadMeasureDomain.Services;

/// <summary>
/// Реестр материалов. Загружается один раз в начале сессии.
///
/// Стратегия поиска materials.json (сверху вниз, первый найденный побеждает):
///   1. Папка текущего DWG — реестр под конкретный объект;
///   2. Папка данных пользователя (%APPDATA%\PTOMeasurePro) — основной реестр.
///
/// Если пользовательского файла нет, он создаётся: копированием шаблона из
/// bundle, а если шаблона нет — из встроенного каталога
/// (см. <see cref="MaterialCatalog"/>).
///
/// Внутрь bundle репозиторий НИКОГДА не пишет: bundle целиком заменяется при
/// обновлении плагина, и всё пользовательское оттуда исчезло бы.
/// </summary>
public sealed class MaterialRepository
{
    public const string FileName = "materials.json";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // Без этого кириллица и символ ⌀ уедут в \uXXXX и файл станет нечитаемым руками.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly List<Material> _materials = new();

    /// <summary>Все загруженные материалы.</summary>
    public IReadOnlyList<Material> Materials => _materials;

    /// <summary>Путь к файлу, из которого реально загрузился реестр.</summary>
    public string? LoadedFrom { get; private set; }

    /// <summary>Откуда взялся реестр — для сообщения в командной строке AutoCAD.</summary>
    public MaterialRegistrySource Source { get; private set; } = MaterialRegistrySource.None;

    /// <summary>True, если файла не было и он был создан заново.</summary>
    public bool WasCreatedFromSample =>
        Source is MaterialRegistrySource.SeededFromTemplate or MaterialRegistrySource.SeededFromCatalog;

    /// <summary>Загружен ли реестр.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>
    /// Что было сделано с нечитаемым файлом реестра, либо null, если всё
    /// прочиталось штатно. Сообщение выводится пользователю: молча подменить
    /// реестр нельзя — человек должен знать, что его файл отложен в сторону.
    /// </summary>
    public string? RecoveryMessage { get; private set; }

    /// <summary>Файл реестра оказался испорченным и был отложен в резервную копию.</summary>
    public bool WasRecovered => RecoveryMessage is not null;

    /// <summary>Человекочитаемое описание источника реестра.</summary>
    public string SourceDescription => Source switch
    {
        MaterialRegistrySource.DrawingFolder => "реестр из папки чертежа",
        MaterialRegistrySource.UserData => "пользовательский реестр",
        MaterialRegistrySource.SeededFromTemplate => "создан из шаблона плагина",
        MaterialRegistrySource.SeededFromCatalog => "создан из встроенного каталога",
        _ => "не загружен"
    };

    /// <summary>
    /// Реестр изменился: добавлен или удалён материал, либо файл перечитан.
    /// Подписчики (палитра, журнал) обновляют по нему своё состояние.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Загрузить реестр по правилам <see cref="MaterialRegistryLocations"/>.
    /// </summary>
    public void Load(MaterialRegistryLocations locations)
    {
        ArgumentNullException.ThrowIfNull(locations);

        if (string.IsNullOrWhiteSpace(locations.UserDataDirectory))
            throw new ArgumentException("Не задана папка данных пользователя.", nameof(locations));

        _materials.Clear();
        LoadedFrom = null;
        Source = MaterialRegistrySource.None;
        RecoveryMessage = null;

        // 1. Реестр рядом с чертежом — под конкретный объект.
        if (!string.IsNullOrWhiteSpace(locations.DrawingDirectory))
        {
            var drawingRegistry = Path.Combine(locations.DrawingDirectory, FileName);
            if (File.Exists(drawingRegistry) && TryApply(drawingRegistry, MaterialRegistrySource.DrawingFolder))
                return;
        }

        // 2. Пользовательский реестр — основной. Он вне bundle и переживает обновление.
        var userRegistry = Path.Combine(locations.UserDataDirectory, FileName);
        if (File.Exists(userRegistry) && TryApply(userRegistry, MaterialRegistrySource.UserData))
            return;

        // 3. Первый запуск: разворачиваем пользовательский реестр.
        Directory.CreateDirectory(locations.UserDataDirectory);

        if (!string.IsNullOrWhiteSpace(locations.TemplatePath) && File.Exists(locations.TemplatePath))
        {
            // Шаблон копируем как есть, не пересобирая из каталога: у него мог
            // быть подправлен состав под отдел, и это ожидаемая точка настройки.
            File.Copy(locations.TemplatePath, userRegistry, overwrite: false);
            Apply(userRegistry, MaterialRegistrySource.SeededFromTemplate);
            return;
        }

        Save(MaterialCatalog.CreateDefault(), userRegistry);
        Apply(userRegistry, MaterialRegistrySource.SeededFromCatalog);
    }

    private void Apply(string path, MaterialRegistrySource source)
    {
        _materials.AddRange(ReadFile(path));
        LoadedFrom = path;
        Source = source;
        IsLoaded = true;

        OnChanged();
    }

    /// <summary>
    /// Прочитать реестр и, если файл испорчен, отложить его в резервную копию.
    ///
    /// Возвращает false, когда файл не удалось разобрать: вызывающий переходит
    /// к следующему варианту (пользовательский реестр, затем шаблон). Молча
    /// перезаписать испорченный файл нельзя — в нём могут быть позиции,
    /// которых больше нигде нет, и человек должен иметь возможность вытащить
    /// их руками.
    ///
    /// Ловится только ошибка разбора. Занятый или недоступный файл — это не
    /// повреждение: подменять его резервной копией значило бы терять рабочий
    /// реестр из-за временной блокировки, поэтому такая ошибка идёт наверх.
    /// </summary>
    private bool TryApply(string path, MaterialRegistrySource source)
    {
        try
        {
            Apply(path, source);
            return true;
        }
        catch (JsonException ex)
        {
            _materials.Clear();

            var backup = CreateBackup(path);
            RecoveryMessage =
                $"Файл реестра «{path}» не читается ({ex.Message.Trim()}). " +
                $"Он сохранён как «{backup}» и заменён новым — исправь его в текстовом редакторе " +
                "и верни на место, если там были нужные позиции.";

            return false;
        }
    }

    /// <summary>
    /// Отложить файл в резервную копию рядом с оригиналом:
    /// materials.json → materials.broken-20260825-231500.json.
    ///
    /// Именно перемещение, а не копирование: испорченный файл должен уйти
    /// с рабочего пути, иначе следующая попытка загрузки снова упрётся в него.
    /// Уже существующие копии не перезаписываются.
    /// </summary>
    public static string CreateBackup(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        var backup = Path.Combine(directory, $"{name}.broken-{stamp}{extension}");

        var index = 2;
        while (File.Exists(backup))
            backup = Path.Combine(directory, $"{name}.broken-{stamp}_{index++}{extension}");

        File.Move(path, backup);
        return backup;
    }

    /// <summary>Перезагрузить реестр из того же файла.</summary>
    public void Reload()
    {
        if (string.IsNullOrEmpty(LoadedFrom) || !File.Exists(LoadedFrom)) return;

        List<Material> items;
        try
        {
            items = ReadFile(LoadedFrom);
        }
        catch (JsonException ex)
        {
            // Здесь файл НЕ откладывается в резервную копию, в отличие от Load:
            // рабочий реестр уже есть в памяти, а файл может быть открыт
            // в редакторе на середине правки. Отобрать его у человека —
            // худшее, что можно сделать в этот момент.
            RecoveryMessage =
                $"Файл реестра «{LoadedFrom}» не читается ({ex.Message.Trim()}). " +
                "Реестр оставлен прежним — исправь файл и повтори обновление.";
            return;
        }

        RecoveryMessage = null;
        _materials.Clear();
        _materials.AddRange(items);

        OnChanged();
    }

    /// <summary>Материалы одного класса, отсортированные по наименованию.</summary>
    public IReadOnlyList<Material> GetByClass(string materialClass) =>
        _materials.Where(m => string.Equals(m.Class, materialClass, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Name, StringComparer.CurrentCulture)
            .ToList();

    /// <summary>Поиск материала по точному наименованию.</summary>
    public Material? FindByName(string name) =>
        _materials.FirstOrDefault(m => string.Equals(m.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Есть ли уже материал с таким наименованием (без учёта регистра).</summary>
    public bool NameExists(string? name) => FindByName(name ?? string.Empty) is not null;

    /// <summary>
    /// Есть ли материал с таким наименованием, не считая указанной позиции.
    /// Нужно при редактировании: материал не должен конфликтовать сам с собой.
    /// </summary>
    public bool NameExistsExcept(string? name, Material? except)
    {
        var found = FindByName(name ?? string.Empty);
        return found is not null && !ReferenceEquals(found, except);
    }

    /// <summary>
    /// Добавить материал в реестр и сразу записать materials.json.
    ///
    /// Наименование — ключ материала (по нему идут поиск, журнал и слои),
    /// поэтому дубликаты запрещены: иначе два разных материала делили бы
    /// одну строку журнала.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Реестр не загружен, наименование пустое либо уже занято.
    /// </exception>
    public Material Add(Material material)
    {
        ArgumentNullException.ThrowIfNull(material);
        EnsureLoaded();

        Normalize(material);
        Validate(material, except: null);

        _materials.Add(material);

        try
        {
            Save(_materials, LoadedFrom!);
        }
        catch
        {
            // Файл не записался — откатываем добавление, чтобы список в памяти
            // не разошёлся с materials.json.
            _materials.Remove(material);
            throw;
        }

        OnChanged();
        return material;
    }

    /// <summary>
    /// Добавить сразу несколько материалов и записать файл ОДИН раз.
    ///
    /// Нужно при заведении позиций из спецификации: там их бывают сотни,
    /// а поштучный <see cref="Add"/> переписывал бы materials.json на каждой.
    /// Проверки те же, что при одиночном добавлении, и выполняются до записи:
    /// если хотя бы одна позиция не проходит, файл не трогается вовсе.
    /// </summary>
    /// <returns>Добавленные материалы в порядке следования.</returns>
    public IReadOnlyList<Material> AddRange(IEnumerable<Material> materials)
    {
        ArgumentNullException.ThrowIfNull(materials);
        EnsureLoaded();

        var incoming = materials.ToList();
        if (incoming.Count == 0) return Array.Empty<Material>();

        foreach (var material in incoming)
        {
            Normalize(material);
            Validate(material, except: null);

            // Дубликаты внутри самой пачки: FindByName их ещё не видит,
            // потому что в списке реестра они появятся только ниже.
            if (incoming.Count(m => string.Equals(m.Name, material.Name, StringComparison.OrdinalIgnoreCase)) > 1)
                throw new InvalidOperationException(
                    $"В добавляемом наборе наименование «{material.Name}» встречается больше одного раза.");
        }

        var restorePoint = _materials.Count;
        _materials.AddRange(incoming);

        try
        {
            Save(_materials, LoadedFrom!);
        }
        catch
        {
            // Файл не записался — откатываем всю пачку, чтобы список
            // в памяти не разошёлся с materials.json.
            _materials.RemoveRange(restorePoint, _materials.Count - restorePoint);
            throw;
        }

        OnChanged();
        return incoming;
    }

    /// <summary>
    /// Удалить материал из реестра и записать materials.json.
    ///
    /// Связанные записи журнала и полилинии здесь НЕ трогаются: реестр про
    /// чертёж ничего не знает. Каскад выполняет MaterialDeletionService
    /// в проекте плагина — он сначала чистит чертёж и журнал и только потом
    /// зовёт этот метод.
    /// </summary>
    public bool Remove(Material material)
    {
        ArgumentNullException.ThrowIfNull(material);
        EnsureLoaded();

        var index = _materials.FindIndex(m =>
            ReferenceEquals(m, material) ||
            string.Equals(m.Name, material.Name, StringComparison.OrdinalIgnoreCase));

        if (index < 0) return false;

        var removed = _materials[index];
        _materials.RemoveAt(index);

        try
        {
            Save(_materials, LoadedFrom!);
        }
        catch
        {
            // Возвращаем позицию на место: список в памяти должен совпадать с файлом.
            _materials.Insert(index, removed);
            throw;
        }

        OnChanged();
        return true;
    }

    /// <summary>
    /// Полная копия материала — заготовка для окна «Копировать выбранный».
    /// Копируются ВСЕ поля, включая наименование: пользователь правит копию
    /// на месте, а уникальность наименования проверяется при сохранении.
    /// </summary>
    public static Material Duplicate(Material source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new Material
        {
            Class = source.Class,
            Name = source.Name,
            Unit = source.Unit,
            DiameterMm = source.DiameterMm,
            WallThicknessMm = source.WallThicknessMm,
            WidthMm = source.WidthMm,
            HeightMm = source.HeightMm,
            SheetThicknessMm = source.SheetThicknessMm,
            NominalDiameterMm = source.NominalDiameterMm,
            // Характеристики кабеля и вид штучного изделия копируются наравне
            // с остальными: без них копия кабеля теряла бы сечение и получала
            // другое имя слоя, а копия фасонной части — чужой вид изделия.
            CoreCount = source.CoreCount,
            CrossSectionMm2 = source.CrossSectionMm2,
            PieceKind = source.PieceKind,
            Mark = source.Mark,
            Manufacturer = source.Manufacturer
        };
    }

    /// <summary>Записать реестр в файл.</summary>
    public static void Save(IEnumerable<Material> materials, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(materials.ToList(), WriteOptions);
        File.WriteAllText(path, json);
    }

    // ======================= Служебное =======================

    private void EnsureLoaded()
    {
        if (!IsLoaded || string.IsNullOrEmpty(LoadedFrom))
            throw new InvalidOperationException("Реестр материалов не загружен.");
    }

    private static void Normalize(Material material)
    {
        material.Name = (material.Name ?? string.Empty).Trim();
        material.Class = (material.Class ?? string.Empty).Trim();
        material.Unit = (material.Unit ?? string.Empty).Trim();
    }

    private void Validate(Material material, Material? except)
    {
        if (string.IsNullOrWhiteSpace(material.Name))
            throw new InvalidOperationException("Не заполнено наименование материала.");

        if (string.IsNullOrWhiteSpace(material.Unit))
            throw new InvalidOperationException("Не заполнена единица измерения.");

        if (NameExistsExcept(material.Name, except))
            throw new InvalidOperationException(
                $"Материал с наименованием «{material.Name}» уже есть в реестре.\n" +
                "Наименование должно быть уникальным — измени его или выбери существующую позицию.");
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static List<Material> ReadFile(string path)
    {
        var json = File.ReadAllText(path);
        var items = JsonSerializer.Deserialize<List<Material>>(json, ReadOptions) ?? new List<Material>();

        // Отбрасываем позиции без наименования — по нему строится вся навигация.
        return items.Where(m => !string.IsNullOrWhiteSpace(m.Name)).ToList();
    }
}
