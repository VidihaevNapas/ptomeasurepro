using Autodesk.AutoCAD.DatabaseServices;
using AcadException = Autodesk.AutoCAD.Runtime.Exception;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace CadMeasurePlugin.Services;

/// <summary>
/// Исполнитель планов видимости слоёв: читает состояние чертежа и включает
/// или выключает ровно то, что перечислено в плане.
///
/// Что менять, решает домен (<see cref="CadMeasureDomain.Services.MeasurementLayerVisibility"/>);
/// здесь только работа с базой чертежа.
///
/// Гасим слои через IsOff, а не заморозкой: заморозка текущего слоя запрещена
/// и вызывает исключение, а выключение — нет. Возврат делается не по снимку
/// всего чертежа, а по списку слоёв, которые погасил сам режим: слой,
/// выключенный пользователем до включения режима, обязан остаться выключенным.
/// </summary>
public sealed class LayerVisibilityService
{
    /// <summary>Итог изменения видимости замерных слоёв.</summary>
    /// <param name="TurnedOn">Сколько слоёв включено.</param>
    /// <param name="TurnedOff">Сколько выключено.</param>
    /// <param name="Log">Что не получилось и почему — для командной строки.</param>
    public readonly record struct VisibilityResult(int TurnedOn, int TurnedOff, IReadOnlyList<string> Log);

    /// <summary>Все слои текущего чертежа — сырые имена, без разбора.</summary>
    public IReadOnlyList<string> GetLayerNames() => ReadLayers().Select(l => l.Name).ToList();

    /// <summary>
    /// Слои, выключенные прямо сейчас. Нужны, чтобы режим видимости запоминал
    /// только то, что погасил он сам: слой, выключенный пользователем до
    /// включения режима, должен остаться выключенным и после выхода из него.
    /// </summary>
    public IReadOnlyList<string> GetHiddenLayerNames() =>
        ReadLayers().Where(l => l.IsOff).Select(l => l.Name).ToList();

    private List<(string Name, bool IsOff)> ReadLayers()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument
                  ?? throw new InvalidOperationException("Нет активного чертежа.");

        var layers = new List<(string, bool)>();

        using (doc.LockDocument())
        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            var layerTable = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId layerId in layerTable)
            {
                var layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead);
                layers.Add((layer.Name, layer.IsOff));
            }

            tr.Commit();
        }

        return layers;
    }

    /// <summary>
    /// Исполнить план видимости: включить и выключить перечисленные слои.
    ///
    /// Слои, которых в плане нет, не трогаются вовсе — план составляет домен
    /// (<see cref="CadMeasureDomain.Services.MeasurementLayerVisibility"/>),
    /// и в нём только те слои, которые режим намерен изменить.
    ///
    /// Каждый слой обрабатывается отдельно и в своём try: чертежи бывают
    /// с внешними ссылками и заблокированными слоями, и отказ на одном слое
    /// не повод бросать остальные.
    /// </summary>
    public VisibilityResult Apply(CadMeasureDomain.Services.LayerVisibilityPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var doc = AcadApp.DocumentManager.MdiActiveDocument
                  ?? throw new InvalidOperationException("Нет активного чертежа.");
        var db = doc.Database;

        var turnOn = new HashSet<string>(plan.TurnOn, StringComparer.OrdinalIgnoreCase);
        var turnOff = new HashSet<string>(plan.TurnOff, StringComparer.OrdinalIgnoreCase);

        var log = new List<string>();
        var turnedOn = 0;
        var turnedOff = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (doc.LockDocument())
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

            foreach (ObjectId layerId in layerTable)
            {
                var layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead);
                seen.Add(layer.Name);

                var shouldBeOn = turnOn.Contains(layer.Name);
                var shouldBeOff = turnOff.Contains(layer.Name);
                if (!shouldBeOn && !shouldBeOff) continue;

                // Слой внешней ссылки принадлежит другому чертежу: менять его
                // видимость отсюда нельзя.
                if (layer.IsDependent)
                {
                    log.Add($"Слой «{layer.Name}» пришёл из внешней ссылки — видимость не менялась");
                    continue;
                }

                try
                {
                    if (shouldBeOn)
                    {
                        if (!layer.IsOff && !layer.IsFrozen) continue;

                        layer.UpgradeOpen();
                        layer.IsOff = false;

                        // Замороженный слой не показать одним IsOff; текущий
                        // слой размораживать не нужно — он и так не заморожен.
                        if (layer.IsFrozen && layerId != db.Clayer) layer.IsFrozen = false;

                        turnedOn++;
                        continue;
                    }

                    if (layer.IsOff) continue;

                    layer.UpgradeOpen();
                    layer.IsOff = true;
                    turnedOff++;
                }
                catch (AcadException ex)
                {
                    // Чаще всего это текущий слой либо слой, защищённый чертежом.
                    var reason = layerId == db.Clayer ? "это текущий слой чертежа" : ex.Message;
                    log.Add($"Слой «{layer.Name}» оставлен без изменений: {reason}");
                }
            }

            tr.Commit();
        }

        foreach (var missing in turnOn.Concat(turnOff).Where(name => !seen.Contains(name)))
            log.Add($"Слоя «{missing}» в чертеже нет — пропущен");

        doc.Editor.Regen();
        return new VisibilityResult(turnedOn, turnedOff, log);
    }
}
