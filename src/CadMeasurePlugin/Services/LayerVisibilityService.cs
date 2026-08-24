using Autodesk.AutoCAD.DatabaseServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace CadMeasurePlugin.Services;

/// <summary>
/// Режим «показать только слои замеров» и возврат исходной видимости.
///
/// Гасим слои через IsOff, а не заморозкой: заморозка текущего слоя запрещена
/// и вызывает исключение, а выключение — нет. Исходное состояние (вкл/выкл,
/// заморожен/разморожен) запоминается на конкретную базу чертежа, поэтому
/// возврат работает даже после переключения между DWG.
/// </summary>
public sealed class LayerVisibilityService
{
    private readonly record struct LayerState(bool IsOff, bool IsFrozen);

    private readonly Dictionary<Database, Dictionary<string, LayerState>> _snapshots = new();

    /// <summary>
    /// Оставить видимыми только перечисленные слои замеров.
    /// Возвращает количество выключенных слоёв.
    /// </summary>
    public int ShowOnlyMeasurementLayers(IEnumerable<string> measurementLayers)
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument
                  ?? throw new InvalidOperationException("Нет активного чертежа.");
        var db = doc.Database;

        var keep = new HashSet<string>(measurementLayers ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (keep.Count == 0)
            throw new InvalidOperationException("В журнале нет ни одного слоя замеров — нечего оставлять видимым.");

        var turnedOff = 0;

        using (doc.LockDocument())
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

            // Снимок делаем один раз: повторное нажатие не должно затирать
            // исходное состояние уже выключенными слоями.
            if (!_snapshots.TryGetValue(db, out var snapshot))
            {
                snapshot = new Dictionary<string, LayerState>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId layerId in layerTable)
                {
                    var layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead);
                    snapshot[layer.Name] = new LayerState(layer.IsOff, layer.IsFrozen);
                }

                _snapshots[db] = snapshot;
            }

            foreach (ObjectId layerId in layerTable)
            {
                var layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead);

                if (keep.Contains(layer.Name))
                {
                    // Слой замеров должен быть виден.
                    if (layer.IsOff || layer.IsFrozen)
                    {
                        layer.UpgradeOpen();
                        layer.IsOff = false;
                        layer.IsFrozen = false;
                    }

                    continue;
                }

                if (layer.IsOff) continue;

                layer.UpgradeOpen();
                layer.IsOff = true;
                turnedOff++;
            }

            tr.Commit();
        }

        doc.Editor.Regen();
        return turnedOff;
    }

    /// <summary>
    /// Вернуть исходную видимость слоёв. Если снимка нет (режим не включался),
    /// просто включает все слои.
    /// </summary>
    public void RestoreAllLayers()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument
                  ?? throw new InvalidOperationException("Нет активного чертежа.");
        var db = doc.Database;

        _snapshots.TryGetValue(db, out var snapshot);

        using (doc.LockDocument())
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

            foreach (ObjectId layerId in layerTable)
            {
                var layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead);

                bool targetOff = false;
                bool targetFrozen = layer.IsFrozen;

                if (snapshot is not null && snapshot.TryGetValue(layer.Name, out var state))
                {
                    targetOff = state.IsOff;
                    targetFrozen = state.IsFrozen;
                }

                // Текущий слой заморозить нельзя — AutoCAD бросит eCannotFreezeCurrentLayer.
                if (targetFrozen && layerId == db.Clayer) targetFrozen = layer.IsFrozen;

                if (layer.IsOff == targetOff && layer.IsFrozen == targetFrozen) continue;

                layer.UpgradeOpen();
                layer.IsOff = targetOff;
                layer.IsFrozen = targetFrozen;
            }

            tr.Commit();
        }

        _snapshots.Remove(db);
        doc.Editor.Regen();
    }
}
