using Autodesk.AutoCAD.DatabaseServices;

namespace CadMeasurePlugin.Services;

/// <summary>
/// Что считать замерной геометрией.
///
/// Отбор идёт в два шага:
///   1. по ObjectId.ObjectClass.DxfName — БЕЗ открытия объекта в транзакции;
///      при обходе модели с сотнями объектов это основная экономия;
///   2. открытый объект дополнительно проверяется на соответствие роли
///      (например, круг-маркер должен быть нужного диаметра).
/// </summary>
internal static class MeasurementGeometry
{
    // Имена с суффиксом Dxf, чтобы константы не перекрывали одноимённые типы
    // AutoCAD (Polyline, Circle) — иначе «entity is Circle» молча превращается
    // в сравнение с константной строкой и никогда не срабатывает.
    private const string LwPolylineDxf = "LWPOLYLINE";
    private const string PolylineDxf = "POLYLINE";   // 2D/3D-полилинии старого формата
    private const string CircleDxf = "CIRCLE";
    private const string TextDxf = "TEXT";

    /// <summary>Полилиния — геометрия линейных замеров (трубы, воздуховоды, кабели).</summary>
    public static bool IsPolylineClass(string? dxfName) => dxfName is LwPolylineDxf or PolylineDxf;

    /// <summary>Круг — потенциальный маркер штучного изделия.</summary>
    public static bool IsCircleClass(string? dxfName) => dxfName is CircleDxf;

    /// <summary>
    /// Однострочный текст — подпись замера (длина полилинии либо номер штучки).
    /// В обмер не входит: сканирование считает только полилинии и круги.
    /// </summary>
    public static bool IsTextClass(string? dxfName) => dxfName is TextDxf;

    /// <summary>
    /// Класс, который надо удалить при очистке замерного слоя.
    ///
    /// Помимо самой геометрии сюда входят подписи: слой принадлежит плагину,
    /// и оставлять на нём текст без объекта, который он подписывал, нельзя.
    /// </summary>
    public static bool IsCandidateClass(string? dxfName, bool pieceMode) =>
        IsTextClass(dxfName) || (pieceMode ? IsCircleClass(dxfName) : IsPolylineClass(dxfName));

    /// <summary>
    /// Маркер штучного изделия — круг заданного диаметра
    /// (<see cref="PluginSettings.PieceMarkerDiameterMm"/>).
    ///
    /// Проверка по диаметру, а не «любой замкнутый контур»: на слое материала
    /// могут оказаться и обводки узлов, и выноски, а маркер должен быть
    /// однозначным, иначе количество поедет.
    /// </summary>
    public static bool IsPieceMarker(Entity entity) =>
        entity is Circle circle &&
        Math.Abs(circle.Diameter - PluginSettings.PieceMarkerDiameterMm) <= PluginSettings.PieceMarkerToleranceMm;

    /// <summary>
    /// Подлежит ли открытый объект удалению при очистке замерного слоя:
    /// либо это геометрия нужного режима, либо подпись к ней.
    /// </summary>
    public static bool IsMeasurementEntity(Entity entity, bool pieceMode)
    {
        if (entity is DBText) return true;

        return pieceMode
            ? IsPieceMarker(entity)
            : entity is Curve && IsPolylineClass(entity.GetRXClass().DxfName);
    }

    /// <summary>
    /// Длина кривой в единицах чертежа. Для Polyline совпадает с Polyline.Length,
    /// но так же работает для 2D/3D-полилиний старого формата.
    /// </summary>
    public static double GetCurveLength(Curve curve)
    {
        try
        {
            return curve.GetDistanceAtParameter(curve.EndParam) -
                   curve.GetDistanceAtParameter(curve.StartParam);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            // Вырожденная геометрия (нулевая длина, битый объект) — просто пропускаем.
            return 0.0;
        }
    }
}
