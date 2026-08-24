using CadMeasureDomain.Models;

namespace CadMeasureDomain.Services;

/// <summary>
/// Мэппинг «материал + участок ↔ имя слоя».
///
/// Формат: PIPE_&lt;код&gt; / DUCT_&lt;код&gt; / CABLE_&lt;код&gt; / PIECE_&lt;код&gt;,
/// плюс участок через подчёркивание, если он задан:
///   PIPE_D89x4                — материал без участка;
///   PIPE_D89x4_Этаж 1         — тот же материал на участке «Этаж 1»;
///   DUCT_1250x800_t0.9_Кровля;
///   CABLE_3x2.5_Этаж 1.
///
/// Участок входит в имя слоя намеренно. Журнал ведётся автоматически: плагин
/// сканирует чертёж и сам создаёт записи «материал + участок + DWG». Определить
/// участок он может только по слою — иначе полилинии двух участков лежали бы
/// вперемешку на одном слое и разделить их было бы нечем.
///
/// Гарантии:
///   • имя вычисляется для ЛЮБОГО материала — если характеристик нет или они
///     непригодны для имени слоя, включается запасной шаблон MEAS_&lt;хеш&gt;;
///   • два РАЗНЫХ материала никогда не получат одну основу имени;
///   • для одного материала основа постоянна в пределах сессии;
///   • имя слоя разбирается обратно в пару «материал + участок».
///
/// Слои здесь не создаются — только имена. Созданием и активацией слоя
/// занимается LayerService в проекте плагина.
/// </summary>
public sealed class LayerNameFactory
{
    private readonly Dictionary<string, string> _baseNameByMaterialKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Material> _materialByBaseName = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    /// <summary>Разделитель между основой имени и участком.</summary>
    public const char SectionSeparator = '_';

    /// <summary>Префикс слоя по классу материала.</summary>
    public static string GetPrefix(string? materialClass) => materialClass switch
    {
        MaterialClasses.Pipe => "PIPE",
        MaterialClasses.Duct => "DUCT",
        MaterialClasses.Cable => "CABLE",
        MaterialClasses.Piece => "PIECE",
        _ => "MEAS"
    };

    /// <summary>
    /// Собрать основу имени слоя, ничего не регистрируя.
    /// Используется для предпросмотра в окне добавления материала.
    /// Результат всегда валиден как имя слоя AutoCAD.
    /// </summary>
    public static string ComposeBaseName(Material material)
    {
        ArgumentNullException.ThrowIfNull(material);

        var prefix = GetPrefix(material.Class);
        var code = MaterialFormatter.BuildShortCode(material);
        var name = LayerNameSanitizer.Sanitize($"{prefix}_{code}");

        // Первый запасной вариант: префикс + хеш наименования.
        if (!LayerNameSanitizer.IsValidLayerName(name))
            name = LayerNameSanitizer.Sanitize($"{prefix}_{MaterialFormatter.BuildFallbackCode(material)}");

        // Второй запасной вариант: и префикс мог оказаться мусорным
        // (класс материала берётся из json и может быть любым).
        if (!LayerNameSanitizer.IsValidLayerName(name))
            name = $"MEAS_{MaterialFormatter.BuildFallbackCode(material)}";

        return name;
    }

    /// <summary>Привести участок к виду, пригодному для имени слоя.</summary>
    public static string NormalizeSection(string? section) =>
        LayerNameSanitizer.Sanitize((section ?? string.Empty).Trim());

    /// <summary>
    /// Пересобрать соответствие «материал ↔ основа имени» по всему реестру.
    ///
    /// Вызывается после загрузки и после каждого изменения реестра. Порядок
    /// обхода — по наименованию, чтобы при коллизии кодов индекс доставался
    /// одному и тому же материалу от запуска к запуску.
    /// </summary>
    public void SyncWithRegistry(IEnumerable<Material> materials)
    {
        ArgumentNullException.ThrowIfNull(materials);

        var ordered = materials
            .Where(m => !string.IsNullOrWhiteSpace(m.Name))
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToList();

        lock (_sync)
        {
            _baseNameByMaterialKey.Clear();
            _materialByBaseName.Clear();

            foreach (var material in ordered)
                AssignBaseName(material);
        }
    }

    /// <summary>Основа имени слоя для материала (без участка).</summary>
    public string GetBaseName(Material material)
    {
        ArgumentNullException.ThrowIfNull(material);

        lock (_sync)
        {
            if (_baseNameByMaterialKey.TryGetValue(material.Key, out var cached))
                return cached;

            return AssignBaseName(material);
        }
    }

    /// <summary>Имя слоя для материала на указанном участке.</summary>
    public string GetLayerName(Material material, string? section)
    {
        var baseName = GetBaseName(material);
        var normalizedSection = NormalizeSection(section);

        if (normalizedSection.Length == 0) return baseName;

        var full = $"{baseName}{SectionSeparator}{normalizedSection}";
        return LayerNameSanitizer.IsValidLayerName(full) ? full : baseName;
    }

    /// <summary>Имя слоя без участка — для операций, где участок не важен.</summary>
    public string GetLayerName(Material material) => GetLayerName(material, null);

    /// <summary>
    /// Разобрать имя слоя обратно в пару «материал + участок».
    ///
    /// Сначала ищется точное совпадение с основой (участок пустой), затем —
    /// самая длинная основа, за которой идёт разделитель. Самая длинная нужна,
    /// чтобы «PIPE_D89x4_Этаж 1» не разобралось по основе «PIPE_D89».
    /// </summary>
    public bool TryResolveLayer(string? layerName, out Material? material, out string section)
    {
        material = null;
        section = string.Empty;

        if (string.IsNullOrWhiteSpace(layerName)) return false;

        lock (_sync)
        {
            if (_materialByBaseName.TryGetValue(layerName, out var exact))
            {
                material = exact;
                return true;
            }

            string? bestBase = null;
            foreach (var candidate in _materialByBaseName.Keys)
            {
                if (layerName.Length <= candidate.Length + 1) continue;
                if (!layerName.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)) continue;
                if (layerName[candidate.Length] != SectionSeparator) continue;

                if (bestBase is null || candidate.Length > bestBase.Length) bestBase = candidate;
            }

            if (bestBase is null) return false;

            material = _materialByBaseName[bestBase];
            section = layerName.Substring(bestBase.Length + 1);
            return true;
        }
    }

    /// <summary>
    /// Забыть материал и освободить основу его имени.
    /// Вызывается при удалении материала из реестра: иначе имя останется
    /// занятым до конца сессии, и новая позиция с теми же характеристиками
    /// получила бы слой с индексом (PIPE_D89x4_2) вместо чистого.
    /// </summary>
    public void Unregister(string materialKey)
    {
        if (string.IsNullOrWhiteSpace(materialKey)) return;

        lock (_sync)
        {
            if (!_baseNameByMaterialKey.TryGetValue(materialKey, out var baseName)) return;

            _baseNameByMaterialKey.Remove(materialKey);
            _materialByBaseName.Remove(baseName);
        }
    }

    /// <summary>Выдать основу имени, разведя коллизию с другим материалом.</summary>
    private string AssignBaseName(Material material)
    {
        var baseName = ComposeBaseName(material);

        var name = baseName;
        var index = 2;
        while (_materialByBaseName.TryGetValue(name, out var owner) &&
               !string.Equals(owner.Key, material.Key, StringComparison.OrdinalIgnoreCase))
        {
            name = $"{baseName}#{index++}";
        }

        if (!LayerNameSanitizer.IsValidLayerName(name))
            throw new InvalidOperationException(
                $"Не удалось построить имя слоя для материала «{material.Name}». " +
                "Проверь позицию в materials.json.");

        _baseNameByMaterialKey[material.Key] = name;
        _materialByBaseName[name] = material;
        return name;
    }
}
