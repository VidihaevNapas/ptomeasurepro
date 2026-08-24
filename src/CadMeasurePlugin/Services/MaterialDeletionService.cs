using CadMeasureDomain.Models;
using CadMeasureDomain.Services;

namespace CadMeasurePlugin.Services;

/// <summary>Что будет затронуто удалением материала — для диалога подтверждения.</summary>
/// <param name="LayerName">Слой материала.</param>
/// <param name="ObjectsInCurrentDrawing">Объектов на слое в активном чертеже.</param>
/// <param name="RecordsInCurrentDrawing">Записей журнала по активному чертежу.</param>
/// <param name="OtherDrawingRecords">Записи журнала по другим чертежам.</param>
public readonly record struct MaterialUsage(
    string LayerName,
    int ObjectsInCurrentDrawing,
    int RecordsInCurrentDrawing,
    IReadOnlyList<MeasurementRecord> OtherDrawingRecords)
{
    public int RecordsInOtherDrawings => OtherDrawingRecords.Count;

    /// <summary>Используется ли материал хоть где-то.</summary>
    public bool IsUsed => ObjectsInCurrentDrawing > 0 || RecordsInCurrentDrawing > 0 || RecordsInOtherDrawings > 0;
}

/// <summary>Итог удаления материала.</summary>
public readonly record struct MaterialDeletionResult(bool Success, int ErasedObjects, int RemovedRecords, string Message);

/// <summary>
/// Каскадное удаление материала из реестра.
///
/// Порядок строгий: полилинии → записи журнала → позиция реестра → слой.
/// Если геометрию стереть не удалось, материал НЕ удаляется: иначе в чертеже
/// остались бы линии на слое, которому больше не соответствует ни одна позиция
/// реестра, и восстановить связь было бы нечем.
/// </summary>
public sealed class MaterialDeletionService
{
    private readonly MaterialRepository _materials;
    private readonly MeasurementJournalService _journalService;
    private readonly MeasurementJournal _journal;
    private readonly LayerService _layers;
    private readonly LayerNameFactory _layerNames;
    private readonly AcadWorkspace _workspace;

    public MaterialDeletionService(
        MaterialRepository materials,
        MeasurementJournalService journalService,
        MeasurementJournal journal,
        LayerService layers,
        LayerNameFactory layerNames,
        AcadWorkspace workspace)
    {
        _materials = materials ?? throw new ArgumentNullException(nameof(materials));
        _journalService = journalService ?? throw new ArgumentNullException(nameof(journalService));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _layers = layers ?? throw new ArgumentNullException(nameof(layers));
        _layerNames = layerNames ?? throw new ArgumentNullException(nameof(layerNames));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    /// <summary>
    /// Узнать, что зацепит удаление материала. Ничего не меняет —
    /// нужно, чтобы диалог подтверждения показал реальные последствия.
    /// </summary>
    public MaterialUsage GetUsage(Material material)
    {
        ArgumentNullException.ThrowIfNull(material);

        var currentDrawing = _workspace.CurrentDrawingFileName;
        var pieceMode = material.Class == MaterialClasses.Piece;

        // У материала столько слоёв, на скольких участках его замеряли.
        var layers = FindMaterialLayers(material);
        var layerName = layers.Count > 0 ? string.Join(", ", layers) : _layers.GetLayerName(material);

        var objects = 0;
        foreach (var layer in layers)
        {
            objects += pieceMode
                ? _workspace.CountShapes(layer)
                : _workspace.MeasureLayer(layer).PolylineCount;
        }

        var records = _journalService.FindRecordsByMaterial(material);
        var inCurrent = records.Count(r =>
            string.Equals(r.DrawingFileName, currentDrawing, StringComparison.OrdinalIgnoreCase));
        var inOthers = records.Where(r =>
            !string.Equals(r.DrawingFileName, currentDrawing, StringComparison.OrdinalIgnoreCase)).ToList();

        return new MaterialUsage(layerName, objects, inCurrent, inOthers);
    }

    /// <summary>
    /// Удалить материал каскадом: геометрия активного чертежа → записи журнала
    /// (по всем чертежам) → позиция реестра → пустой слой.
    ///
    /// Записи из других чертежей удаляются из журнала, но их полилинии остаются:
    /// плагин не может править чертёж, который не открыт. UI обязан предупредить
    /// об этом заранее — <see cref="GetUsage"/> отдаёт нужные цифры.
    /// </summary>
    public MaterialDeletionResult Delete(Material material)
    {
        ArgumentNullException.ThrowIfNull(material);

        var pieceMode = material.Class == MaterialClasses.Piece;
        var layers = FindMaterialLayers(material);

        // 1. Геометрия активного чертежа — на всех слоях материала
        //    (по одному на каждый участок, где он замерялся).
        var erasedTotal = 0;
        foreach (var layer in layers)
        {
            var layerPurge = _journalService.PurgeLayer(layer, pieceMode);
            erasedTotal += layerPurge.Erased;

            if (!layerPurge.Success)
            {
                return new MaterialDeletionResult(false, erasedTotal, 0,
                    $"Не удалось очистить слой «{layer}»: удалено {layerPurge.Erased}, " +
                    $"осталось {layerPurge.Remaining}. Материал НЕ удалён. {layerPurge.Message}");
            }
        }

        var purge = (Erased: erasedTotal, Message: string.Empty);

        // 2. Записи журнала по всем чертежам.
        var removedRecords = _journalService.RemoveRecordsByMaterial(material);

        // 3. Позиция реестра и materials.json.
        try
        {
            if (!_materials.Remove(material))
            {
                return new MaterialDeletionResult(false, purge.Erased, removedRecords,
                    $"Материал «{material.Name}» не найден в реестре. " +
                    $"Полилинии удалены ({purge.Erased}), записей журнала удалено: {removedRecords}.");
            }
        }
        catch (Exception ex)
        {
            return new MaterialDeletionResult(false, purge.Erased, removedRecords,
                $"Не удалось записать materials.json: {ex.Message}\n" +
                $"Материал остался в реестре. Полилинии удалены ({purge.Erased}), " +
                $"записей журнала удалено: {removedRecords}.");
        }

        // 4. Пустые слои и резервирование имени. Обе операции необязательные:
        //    если слой не удалится, он просто останется пустым.
        var deletedLayers = layers.Count(layer => _layers.TryDeleteLayer(layer));
        _layerNames.Unregister(material.Key);

        var layerNote = layers.Count == 0
            ? string.Empty
            : $" Слоёв материала: {layers.Count}, удалено из чертежа: {deletedLayers}.";

        return new MaterialDeletionResult(true, purge.Erased, removedRecords,
            $"Материал «{material.Name}» удалён из реестра. " +
            $"Удалено объектов: {purge.Erased}, записей журнала: {removedRecords}.{layerNote}");
    }

    /// <summary>
    /// Все слои активного чертежа, принадлежащие материалу.
    /// Их столько, на скольких участках материал замеряли: имя слоя —
    /// это «основа материала + участок».
    /// </summary>
    private List<string> FindMaterialLayers(Material material)
    {
        var result = new List<string>();

        foreach (var layerName in _layers.GetAllLayerNames())
        {
            if (!_layerNames.TryResolveLayer(layerName, out var resolved, out _)) continue;
            if (resolved is null) continue;
            if (!string.Equals(resolved.Name, material.Name, StringComparison.OrdinalIgnoreCase)) continue;

            result.Add(layerName);
        }

        return result;
    }
}
