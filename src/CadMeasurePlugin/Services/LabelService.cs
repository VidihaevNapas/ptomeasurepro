using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcadException = Autodesk.AutoCAD.Runtime.Exception;

namespace CadMeasurePlugin.Services;

/// <summary>
/// Подписи объектов замера: длина над полилинией и номер внутри круга-маркера.
///
/// Подписи — обычные DBText на том же слое, что и объект, с тем же цветом.
/// Замерами они не считаются: сканирование смотрит только на полилинии
/// и круги, а TEXT в отбор не попадает. Зато при очистке слоя подписи
/// удаляются вместе с геометрией, иначе остались бы висеть в пустоте.
/// </summary>
public sealed class LabelService
{
    private readonly TextStyleService _textStyles;

    public LabelService(TextStyleService textStyles)
    {
        _textStyles = textStyles ?? throw new ArgumentNullException(nameof(textStyles));
    }

    /// <summary>
    /// Подписать полилинию её собственной длиной.
    ///
    /// Текст ставится в точке половины длины (а не в центре габаритов —
    /// у изогнутой трассы это разные места), поворачивается вдоль полилинии
    /// и приподнимается над ней.
    /// </summary>
    /// <param name="polylineId">Только что созданная полилиния.</param>
    /// <param name="drawingUnitsPerMeter">Сколько единиц чертежа в метре.</param>
    public void LabelPolyline(ObjectId polylineId, double drawingUnitsPerMeter)
    {
        if (polylineId.IsNull || polylineId.IsErased) return;

        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc is null) return;

        var db = doc.Database;

        using var docLock = doc.LockDocument();
        using var tr = db.TransactionManager.StartTransaction();

        if (tr.GetObject(polylineId, OpenMode.ForRead) is not Curve curve)
        {
            tr.Commit();
            return;
        }

        var length = MeasurementGeometry.GetCurveLength(curve);
        if (length <= 0)
        {
            // Вырожденная полилиния: подписывать нечего.
            tr.Commit();
            return;
        }

        if (!TryGetLabelAnchor(curve, length, out var anchor, out var angle))
        {
            tr.Commit();
            return;
        }

        // Подпись поднимается перпендикулярно линии, а не строго вверх по Y:
        // иначе у вертикальной трассы текст лёг бы прямо на неё.
        var offset = new Vector3d(
            -Math.Sin(angle) * PluginSettings.PolylineLabelOffsetMm,
            Math.Cos(angle) * PluginSettings.PolylineLabelOffsetMm,
            0.0);

        var text = CreateText(
            db,
            tr,
            FormatLength(length, drawingUnitsPerMeter),
            anchor + offset,
            PluginSettings.PolylineLabelHeightMm,
            curve.LayerId,
            curve.Color,
            TextVerticalMode.TextBottom,
            angle);

        AppendToModelSpace(tr, db, text);
        tr.Commit();
    }

    /// <summary>
    /// Подписать круг-маркер порядковым номером.
    /// Вызывается внутри транзакции, в которой создаётся сам круг, — так номер
    /// и маркер появляются вместе либо не появляются вовсе.
    /// </summary>
    public DBText CreatePieceLabel(Database db, Transaction tr, Circle marker, int number)
    {
        ArgumentNullException.ThrowIfNull(marker);

        return CreateText(
            db,
            tr,
            number.ToString(CultureInfo.InvariantCulture),
            marker.Center,
            PluginSettings.PieceLabelHeightMm,
            marker.LayerId,
            marker.Color,
            TextVerticalMode.TextVerticalMid,
            rotation: 0.0);
    }

    /// <summary>
    /// Следующий номер штучного изделия на слое.
    ///
    /// Правило: нумерация сквозная в пределах слоя, а слой — это «материал +
    /// участок». То есть у каждого материала на каждом участке своя нумерация
    /// с единицы, и номера в спецификации соотносятся со строкой журнала.
    ///
    /// Берётся максимум существующих номеров плюс один, а не количество кругов:
    /// после удаления маркера из середины номера не съезжают и не начинают
    /// повторяться.
    /// </summary>
    public int GetNextPieceNumber(string layerName)
    {
        if (string.IsNullOrWhiteSpace(layerName)) return 1;

        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc is null) return 1;

        var db = doc.Database;
        var max = 0;

        using (doc.LockDocument())
        using (var tr = db.TransactionManager.StartTransaction())
        {
            foreach (var id in EnumerateModelSpace(tr, db))
            {
                if (!MeasurementGeometry.IsTextClass(id.ObjectClass.DxfName)) continue;
                if (tr.GetObject(id, OpenMode.ForRead) is not DBText text) continue;
                if (!string.Equals(text.Layer, layerName, StringComparison.OrdinalIgnoreCase)) continue;

                if (int.TryParse(text.TextString?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                    && value > max)
                {
                    max = value;
                }
            }

            tr.Commit();
        }

        return max + 1;
    }

    // ======================= Служебное =======================

    /// <summary>Длина в метрах: «12,345 м».</summary>
    private static string FormatLength(double lengthDrawingUnits, double drawingUnitsPerMeter)
    {
        var meters = drawingUnitsPerMeter > 0 ? lengthDrawingUnits / drawingUnitsPerMeter : lengthDrawingUnits;
        return $"{meters.ToString("0.###", CultureInfo.CurrentCulture)} м";
    }

    /// <summary>
    /// Точка половины длины и направление трассы в ней.
    /// Угол берётся по касательной, чтобы подпись легла параллельно линии.
    /// </summary>
    private static bool TryGetLabelAnchor(Curve curve, double length, out Point3d anchor, out double angle)
    {
        anchor = Point3d.Origin;
        angle = 0.0;

        try
        {
            anchor = curve.GetPointAtDist(length / 2.0);

            var direction = curve.GetFirstDerivative(anchor);
            if (direction.Length > Tolerance.Global.EqualPoint)
                angle = Math.Atan2(direction.Y, direction.X);

            // Текст не должен читаться вверх ногами: разворачиваем на 180°,
            // если трасса идёт справа налево.
            if (angle > Math.PI / 2) angle -= Math.PI;
            else if (angle < -Math.PI / 2) angle += Math.PI;

            return true;
        }
        catch (AcadException)
        {
            return false;
        }
    }

    /// <summary>Собрать DBText с нужным выравниванием.</summary>
    private DBText CreateText(
        Database db,
        Transaction tr,
        string content,
        Point3d position,
        double height,
        ObjectId layerId,
        Autodesk.AutoCAD.Colors.Color color,
        TextVerticalMode verticalMode,
        double rotation)
    {
        var text = new DBText
        {
            TextString = content,
            Height = height,
            TextStyleId = _textStyles.EnsureLabelStyle(tr, db),
            LayerId = layerId,
            Color = color,
            Rotation = rotation,

            // Position задаётся до режимов выравнивания, AlignmentPoint — после:
            // при непустом выравнивании AutoCAD считает положение именно по нему.
            Position = position
        };

        text.HorizontalMode = TextHorizontalMode.TextCenter;
        text.VerticalMode = verticalMode;
        text.AlignmentPoint = position;

        // Без пересчёта выравнивания AutoCAD в части случаев продолжает
        // отображать текст по Position, игнорируя AlignmentPoint.
        text.AdjustAlignment(db);

        return text;
    }

    private static void AppendToModelSpace(Transaction tr, Database db, Entity entity)
    {
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

        modelSpace.AppendEntity(entity);
        tr.AddNewlyCreatedDBObject(entity, true);
    }

    private static IEnumerable<ObjectId> EnumerateModelSpace(Transaction tr, Database db)
    {
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);

        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased || !id.IsValid) continue;
            yield return id;
        }
    }
}
