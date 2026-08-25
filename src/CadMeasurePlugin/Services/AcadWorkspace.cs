using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using CadMeasureDomain.Tools;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
// В AutoCAD есть свой тип Material (материал визуализации) — уточняем, что речь о нашем.
using Material = CadMeasureDomain.Models.Material;

namespace CadMeasurePlugin.Services;

/// <summary>Итог обмера одного слоя при пакетном сканировании.</summary>
/// <param name="PolylineCount">Полилиний на слое.</param>
/// <param name="LengthDrawingUnits">Их суммарная длина в единицах чертежа.</param>
/// <param name="MarkerCount">Кругов-маркеров штучных изделий на слое.</param>
public readonly record struct LayerScan(int PolylineCount, double LengthDrawingUnits, int MarkerCount);

/// <summary>Итог обмера одного слоя: количество полилиний и их суммарная длина.</summary>
public readonly record struct LayerMeasurement(int PolylineCount, double LengthDrawingUnits)
{
    public static LayerMeasurement Empty => new(0, 0d);
}

/// <summary>
/// Реализация <see cref="ICadWorkspace"/> поверх AutoCAD API.
/// Отвечает за обмер геометрии; работа со слоями делегируется
/// <see cref="LayerService"/>, чтобы правила создания слоя жили в одном месте.
/// </summary>
public sealed class AcadWorkspace : ICadWorkspace
{
    private readonly LayerService _layers;

    public AcadWorkspace(LayerService layers)
    {
        _layers = layers ?? throw new ArgumentNullException(nameof(layers));
    }

    public double DrawingUnitsPerMeter => PluginSettings.DrawingUnitsPerMeter;

    /// <summary>
    /// Выделить в чертеже всё, что лежит на слое записи, — чтобы по строке
    /// журнала можно было найти геометрию глазами.
    ///
    /// Здесь выборка уместна, в отличие от обмера: пользователь смотрит
    /// на видимую графику, и объекты выключенного или замороженного слоя
    /// выделять всё равно бессмысленно. Ноль в ответе означает ровно это —
    /// слой пуст либо не показан.
    /// </summary>
    /// <returns>Сколько объектов выделено.</returns>
    public int SelectLayerEntities(string layerName)
    {
        if (string.IsNullOrWhiteSpace(layerName)) return 0;

        var document = AcadApp.DocumentManager.MdiActiveDocument;
        if (document is null) return 0;

        try
        {
            var editor = document.Editor;
            var filter = new Autodesk.AutoCAD.EditorInput.SelectionFilter(
                new[] { new TypedValue((int)DxfCode.LayerName, layerName) });

            var selection = editor.SelectAll(filter);
            if (selection.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK || selection.Value is null)
                return 0;

            var ids = selection.Value.GetObjectIds();
            editor.SetImpliedSelection(ids);
            return ids.Length;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            // Выделение — вспомогательное действие: чертёж мог быть занят
            // командой, и ронять из-за этого палитру незачем.
            return 0;
        }
    }

    /// <summary>Имя активного DWG без пути. Для несохранённого чертежа — «Drawing1.dwg».</summary>
    public string CurrentDrawingFileName
    {
        get
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc is null) return string.Empty;

            try
            {
                var fileName = doc.Database.Filename;
                return string.IsNullOrWhiteSpace(fileName) ? doc.Name : Path.GetFileName(fileName);
            }
            catch
            {
                // У только что созданного чертежа путь может быть недоступен.
                return doc.Name;
            }
        }
    }

    /// <summary>Полный путь к активному DWG либо null, если чертёж ещё не сохранён.</summary>
    public static string? GetCurrentDrawingFullPath()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc is null) return null;

        try
        {
            var fileName = doc.Database.Filename;
            if (string.IsNullOrWhiteSpace(fileName)) return null;

            // У несохранённого чертежа Filename = «Drawing1.dwg» без каталога.
            return Path.IsPathRooted(fileName) ? fileName : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Папка активного DWG либо null.</summary>
    public static string? GetCurrentDrawingDirectory()
    {
        var full = GetCurrentDrawingFullPath();
        return string.IsNullOrEmpty(full) ? null : Path.GetDirectoryName(full);
    }

    /// <summary>Создать слой материала при необходимости и сделать его текущим.</summary>
    public string EnsureLayer(Material material, string layerName) => _layers.EnsureLayer(material, layerName);

    /// <summary>Суммарная длина полилиний слоя в единицах чертежа.</summary>
    public LayerMeasurement MeasureLayer(string layerName)
    {
        var scan = ScanLayers(new[] { layerName });
        return scan.TryGetValue(layerName, out var found)
            ? new LayerMeasurement(found.PolylineCount, found.LengthDrawingUnits)
            : LayerMeasurement.Empty;
    }

    /// <summary>Количество кругов-маркеров штучных изделий на слое.</summary>
    public int CountShapes(string layerName)
    {
        var scan = ScanLayers(new[] { layerName });
        return scan.TryGetValue(layerName, out var found) ? found.MarkerCount : 0;
    }

    /// <summary>
    /// Обмер сразу нескольких слоёв за ОДИН проход по пространству модели.
    ///
    /// Это единственный метод, который ходит по модели ради замеров: и разовый
    /// обмер слоя, и полное сканирование журнала сводятся к нему. При 30 записях
    /// поштучный обход означал бы 30 полных проходов после каждой команды AutoCAD.
    ///
    /// Выборкой (SelectAll с фильтром) намеренно не пользуемся — она игнорирует
    /// выключенные и замороженные слои, а замерные слои как раз бывают выключены
    /// режимом «показать только слои замеров».
    ///
    /// В результате есть запись для каждого запрошенного слоя, даже если на нём
    /// ничего не нашлось: по нулям вызывающий код понимает, что слой опустел.
    /// </summary>
    public IReadOnlyDictionary<string, LayerScan> ScanLayers(IReadOnlyCollection<string> layerNames)
    {
        var result = new Dictionary<string, LayerScan>(StringComparer.OrdinalIgnoreCase);
        if (layerNames is null) return result;

        foreach (var name in layerNames)
        {
            if (!string.IsNullOrWhiteSpace(name)) result[name] = default;
        }

        if (result.Count == 0) return result;

        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc is null) return result;

        var db = doc.Database;

        using (doc.LockDocument())
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in modelSpace)
            {
                if (id.IsErased || !id.IsValid) continue;

                var dxf = id.ObjectClass.DxfName;
                var isPolyline = MeasurementGeometry.IsPolylineClass(dxf);
                var isCircle = MeasurementGeometry.IsCircleClass(dxf);
                if (!isPolyline && !isCircle) continue;

                if (tr.GetObject(id, OpenMode.ForRead) is not Entity entity) continue;
                if (!result.TryGetValue(entity.Layer, out var scan)) continue;

                if (isPolyline && entity is Curve curve)
                {
                    var length = MeasurementGeometry.GetCurveLength(curve);
                    if (length <= 0) continue;

                    result[entity.Layer] = scan with
                    {
                        PolylineCount = scan.PolylineCount + 1,
                        LengthDrawingUnits = scan.LengthDrawingUnits + length
                    };
                }
                else if (isCircle && MeasurementGeometry.IsPieceMarker(entity))
                {
                    result[entity.Layer] = scan with { MarkerCount = scan.MarkerCount + 1 };
                }
            }

            tr.Commit();
        }

        return result;
    }
}
