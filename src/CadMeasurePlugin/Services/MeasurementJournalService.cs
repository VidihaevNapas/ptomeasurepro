using CadMeasureDomain.Models;
using CadMeasureDomain.Services;

namespace CadMeasurePlugin.Services;

/// <summary>Итог пересканирования чертежа.</summary>
/// <param name="Created">Создано новых записей.</param>
/// <param name="Updated">Обновлено существующих.</param>
/// <param name="Removed">Удалено записей с опустевшими слоями.</param>
/// <param name="ScannedLayers">Сколько замерных слоёв найдено в чертеже.</param>
public readonly record struct JournalScanResult(int Created, int Updated, int Removed, int ScannedLayers)
{
    public bool HasChanges => Created > 0 || Updated > 0 || Removed > 0;

    public string ToRussian() =>
        $"слоёв замера: {ScannedLayers}, создано: {Created}, обновлено: {Updated}, удалено: {Removed}";
}

/// <summary>
/// Ведение журнала по фактической геометрии чертежа.
///
/// Журнал не заполняется руками — он ВЫВОДИТСЯ из чертежа. Источник истины:
/// слои вида «основа материала + участок». Сканирование разбирает имена слоёв
/// обратно в пары «материал + участок», обмеряет их за один проход по модели
/// и приводит журнал в соответствие: создаёт недостающие записи, обновляет
/// существующие, удаляет те, чьи слои опустели.
///
/// Отсюда следует правило целостности: запись живёт, пока на её слое есть
/// геометрия. Оно применяется одинаково при автообновлении, по кнопке
/// «Обновить журнал» и перед экспортом.
/// </summary>
public sealed class MeasurementJournalService
{
    private readonly MeasurementJournal _journal;
    private readonly AcadWorkspace _workspace;
    private readonly LayerService _layers;
    private readonly LayerNameFactory _layerNames;
    private readonly VerticalRunStore _verticalRuns;
    private readonly MaterialRepository _materials;

    public MeasurementJournalService(
        MeasurementJournal journal,
        AcadWorkspace workspace,
        LayerService layers,
        LayerNameFactory layerNames,
        VerticalRunStore verticalRuns,
        MaterialRepository materials)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _layers = layers ?? throw new ArgumentNullException(nameof(layers));
        _layerNames = layerNames ?? throw new ArgumentNullException(nameof(layerNames));
        _verticalRuns = verticalRuns ?? throw new ArgumentNullException(nameof(verticalRuns));
        _materials = materials ?? throw new ArgumentNullException(nameof(materials));
    }

    /// <summary>Журнал изменился в результате сканирования или удаления.</summary>
    public event EventHandler? JournalChanged;

    // ======================= Сканирование чертежа =======================

    /// <summary>
    /// Пересобрать записи журнала по фактическому содержимому активного чертежа.
    ///
    /// Порядок:
    ///   1. взять все слои чертежа и оставить те, что разбираются в
    ///      «материал + участок» (то есть принадлежат реестру);
    ///   2. обмерить их ЗА ОДИН ПРОХОД по пространству модели;
    ///   3. создать/обновить записи по слоям с геометрией;
    ///   4. удалить записи текущего DWG, чьи слои опустели или исчезли.
    ///
    /// Один проход принципиален: при 30 записях поштучный обмер означал бы
    /// 30 полных обходов чертежа после каждой команды AutoCAD.
    /// </summary>
    public JournalScanResult ScanDrawing()
    {
        var drawing = _workspace.CurrentDrawingFileName;
        if (string.IsNullOrEmpty(drawing)) return default;

        // 1. Слои чертежа → пары «материал + участок».
        var resolved = new List<(string Layer, Material Material, string Section)>();
        foreach (var layerName in _layers.GetAllLayerNames())
        {
            if (!_layerNames.TryResolveLayer(layerName, out var material, out var section)) continue;
            if (material is null) continue;

            resolved.Add((layerName, material, section));
        }

        if (resolved.Count == 0)
        {
            var removedOnly = RemoveRecordsWithoutGeometry(drawing, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (removedOnly > 0) OnJournalChanged();
            return new JournalScanResult(0, 0, removedOnly, 0);
        }

        // 2. Обмер всех слоёв за один проход.
        var scans = _workspace.ScanLayers(resolved.Select(r => r.Layer).ToArray());

        var created = 0;
        var updated = 0;
        var layersWithGeometry = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 3. Создание и обновление записей.
        foreach (var (layer, material, section) in resolved)
        {
            var scan = scans.TryGetValue(layer, out var found) ? found : default;
            var verticalM = _verticalRuns.GetVerticalLengthM(layer, section);

            var hasGeometry = material.Class == MaterialClasses.Piece
                ? scan.MarkerCount > 0
                : scan.PolylineCount > 0 || verticalM > 0;

            if (!hasGeometry) continue;

            layersWithGeometry.Add(layer);

            var existing = _journal.Find(material.Name, section, drawing);
            if (existing is null) created++;
            else updated++;

            if (material.Class == MaterialClasses.Piece)
            {
                _journal.AddOrUpdatePiece(material, section, layer, scan.MarkerCount, drawing);
            }
            else
            {
                _journal.AddOrUpdateLinear(
                    material,
                    section,
                    layer,
                    scan.LengthDrawingUnits / _workspace.DrawingUnitsPerMeter,
                    verticalM,
                    scan.PolylineCount,
                    drawing);
            }
        }

        // 4. Записи без геометрии.
        var removed = RemoveRecordsWithoutGeometry(drawing, layersWithGeometry);

        var result = new JournalScanResult(created, updated, removed, resolved.Count);
        if (result.HasChanges) OnJournalChanged();
        return result;
    }

    /// <summary>
    /// Удалить записи текущего чертежа, под которыми не осталось геометрии.
    /// Слой мог опустеть, быть удалён или переименован — во всех случаях
    /// замер больше ничем не подтверждён.
    /// </summary>
    private int RemoveRecordsWithoutGeometry(string drawing, HashSet<string> layersWithGeometry)
    {
        var doomed = _journal.GetRecordsForDrawing(drawing)
            .Where(r => !layersWithGeometry.Contains(r.LayerName))
            .ToList();

        foreach (var record in doomed)
            RemoveRecordAndVerticals(record);

        return doomed.Count;
    }

    // ======================= Удаление =======================

    /// <summary>Все записи журнала по материалу — по всем чертежам.</summary>
    public IReadOnlyList<MeasurementRecord> FindRecordsByMaterial(Material material)
    {
        ArgumentNullException.ThrowIfNull(material);

        return _journal.Records
            .Where(r => string.Equals(r.MaterialName, material.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Стереть замерную геометрию слоя и убедиться, что на нём ничего не осталось.
    /// Используется и при удалении записи, и при удалении материала.
    /// </summary>
    public (bool Success, int Erased, int Remaining, string Message) PurgeLayer(string layerName, bool pieceMode)
    {
        var erase = _layers.EraseMeasurementGeometry(layerName, pieceMode);

        // Проверяем по факту, а не по возвращённому счётчику: объект могли
        // держать открытым, и Erase прошёл бы вхолостую.
        var remaining = pieceMode
            ? _workspace.CountShapes(layerName)
            : _workspace.MeasureLayer(layerName).PolylineCount;

        if (remaining > 0 || !erase.FullyErased)
        {
            var reason = erase.Message
                         ?? "Проверь, не заблокирован ли слой и не открыт ли чертёж только для чтения.";
            return (false, erase.Erased, remaining, reason);
        }

        return (true, erase.Erased, 0, string.Empty);
    }

    /// <summary>
    /// Удалить записи журнала по материалу. Геометрия текущего чертежа не трогается —
    /// её удаляет MaterialDeletionService до вызова этого метода.
    /// </summary>
    public int RemoveRecordsByMaterial(Material material)
    {
        var records = FindRecordsByMaterial(material);
        foreach (var record in records)
            RemoveRecordAndVerticals(record);

        if (records.Count > 0) OnJournalChanged();
        return records.Count;
    }

    /// <summary>
    /// Убрать запись и накопленные для неё вертикальные участки.
    /// Без сброса вертикальных при повторном замере того же материала
    /// в журнал вернулась бы длина, которой на чертеже нет.
    /// </summary>
    private void RemoveRecordAndVerticals(MeasurementRecord record)
    {
        _verticalRuns.Reset(record.LayerName, record.Section);
        _journal.Remove(record);
    }

    private void OnJournalChanged() => JournalChanged?.Invoke(this, EventArgs.Empty);
}
