using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using CadMeasureDomain.Models;
using CadMeasurePlugin.Commands;
using CadMeasurePlugin.Services;
using CadMeasurePlugin.UI;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcadException = Autodesk.AutoCAD.Runtime.Exception;

[assembly: CommandClass(typeof(MeasureCommands))]

namespace CadMeasurePlugin.Commands;

/// <summary>
/// Команды AutoCAD, которые регистрирует плагин.
/// </summary>
public sealed class MeasureCommands
{
    /// <summary>Команда рисования замерных полилиний (вызывается из палитры).</summary>
    public const string DrawCommandName = "CMPDRAW";

    /// <summary>Команда вставки таблиц журнала (вызывается из палитры).</summary>
    public const string TableCommandName = "CMPTABLE";

    /// <summary>Открыть палитру замеров.</summary>
    [CommandMethod("CMP", CommandFlags.Modal)]
    public void ShowPalette()
    {
        var editor = AcadApp.DocumentManager.MdiActiveDocument?.Editor;

        try
        {
            PaletteManager.Show();
            editor?.WriteMessage($"\nПалитра «{PluginSettings.PaletteTitle}» открыта.\n");
        }
        catch (System.Exception ex)
        {
            editor?.WriteMessage($"\nНе удалось открыть палитру: {ex.Message}\n");
        }
    }

    /// <summary>Русский синоним команды открытия палитры.</summary>
    [CommandMethod("ЗАМЕРЫ", CommandFlags.Modal)]
    public void ShowPaletteRu() => ShowPalette();

    /// <summary>
    /// Режим рисования замерных полилиний.
    ///
    /// Делает слой выбранного материала текущим и в цикле запускает штатную
    /// команду ПЛИНИЯ: одна полилиния — сразу следующая, без возврата в палитру.
    /// Выход — Enter или Esc на запросе начальной точки.
    ///
    /// Команда запускается из палитры через SendStringToExecute: интерактивный
    /// ввод точек возможен только в контексте команды.
    /// </summary>
    [CommandMethod(DrawCommandName, CommandFlags.Modal)]
    public void DrawMeasurement()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc is null) return;

        var editor = doc.Editor;
        var session = MeasureSession.Instance;
        var tool = session.ActiveTool;

        if (tool.CurrentMaterial is null)
        {
            editor.WriteMessage(
                $"\nДля инструмента «{tool.ToolName}» не выбран материал. " +
                $"Открой палитру командой CMP и нажми «Выбрать материал».\n");
            return;
        }

        var db = doc.Database;
        var previousLayerId = db.Clayer;

        try
        {
            // Слой создаётся здесь — только при первом реальном замере материала.
            // Участок входит в имя слоя, поэтому передаём его явно.
            var layerName = tool.PrepareLayerOrSelection(session.Section);
            var pieceMode = tool.CurrentMaterial.Class == MaterialClasses.Piece;

            editor.WriteMessage(
                $"\n{(pieceMode ? "Подсчёт" : "Замер")}: {tool.CurrentMaterial.Name}\n" +
                $"Слой: {layerName}. Участок: " +
                $"{(string.IsNullOrWhiteSpace(session.Section) ? "не задан" : session.Section)}\n" +
                (pieceMode
                    ? $"Указывай центр каждого изделия — плагин ставит круг ⌀{PluginSettings.PieceMarkerDiameterMm:0}.\n"
                    : "Рисуй полилинии одну за другой.\n") +
                "Enter или Esc — выход.\n");

            var drawn = pieceMode
                ? PlacePieceMarkers(editor, session, layerName)
                : DrawPolylines(editor, session);

            ReportResult(editor, session, layerName, pieceMode, drawn);
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage($"\nОшибка режима замера: {ex.Message}\n");
        }
        finally
        {
            RestoreCurrentLayer(db, previousLayerId);
        }
    }

    /// <summary>
    /// Рисование замерных полилиний штатной командой ПЛИНИЯ.
    /// Привязки, дуги, отмена шага работают как обычно.
    /// Каждая законченная полилиния сразу подписывается своей длиной.
    /// Возвращает количество нарисованных полилиний.
    /// </summary>
    private static int DrawPolylines(Editor editor, MeasureSession session)
    {
        var drawn = 0;

        while (true)
        {
            var options = new PromptPointOptions("\nНачальная точка полилинии (Enter — завершить): ")
            {
                AllowNone = true
            };

            var point = editor.GetPoint(options);
            if (point.Status != PromptStatus.OK) break;

            try
            {
                editor.Command("_.PLINE", point.Value);
                drawn++;
            }
            catch (AcadException ex) when (ex.ErrorStatus == ErrorStatus.UserBreak)
            {
                break;
            }

            LabelLastPolyline(editor, session);
        }

        return drawn;
    }

    /// <summary>
    /// Подписать полилинию, созданную последней командой ПЛИНИЯ.
    ///
    /// Объект берётся через SelectLast: ПЛИНИЯ отработала только что, и её
    /// результат — единственное, что попало в «последнюю» выборку. Отдельно
    /// отслеживать добавление объектов в базу ради этого не нужно.
    ///
    /// Ошибка подписи не должна прерывать замер: геометрия важнее оформления.
    /// </summary>
    private static void LabelLastPolyline(Editor editor, MeasureSession session)
    {
        // Флажок «Показывать длину участков» управляет только новыми подписями:
        // уже созданные при переключении не трогаются.
        if (!PluginSettings.ShowPolylineLengthLabels) return;

        try
        {
            var selection = editor.SelectLast();
            if (selection.Status != PromptStatus.OK || selection.Value is null) return;

            foreach (var id in selection.Value.GetObjectIds())
            {
                if (id.IsNull || id.IsErased) continue;
                if (!MeasurementGeometry.IsPolylineClass(id.ObjectClass.DxfName)) continue;

                session.Labels.LabelPolyline(id, session.Workspace.DrawingUnitsPerMeter);
            }
        }
        catch (AcadException ex)
        {
            editor.WriteMessage($"\nНе удалось подписать полилинию: {ex.Message}\n");
        }
    }

    /// <summary>
    /// Расстановка кругов-маркеров штучных изделий с нумерацией.
    ///
    /// Пользователь указывает только центр — диаметр фиксирован, потому что
    /// именно по нему сканирование отличает маркеры от прочей графики слоя.
    ///
    /// Номер продолжает уже существующую нумерацию слоя: стартовое значение
    /// читается из чертежа один раз, дальше увеличивается в памяти. Иначе
    /// каждый маркер стоил бы отдельного прохода по модели.
    /// </summary>
    private static int PlacePieceMarkers(Editor editor, MeasureSession session, string layerName)
    {
        var placed = 0;
        var number = session.Labels.GetNextPieceNumber(layerName);

        while (true)
        {
            var options = new PromptPointOptions(
                $"\nЦентр изделия №{number} (Enter — завершить, поставлено: {placed}): ")
            {
                AllowNone = true
            };

            var point = editor.GetPoint(options);
            if (point.Status != PromptStatus.OK) break;

            session.LayerService.AddPieceMarker(point.Value, number);
            number++;
            placed++;
        }

        return placed;
    }

    /// <summary>Итог сеанса в командной строке.</summary>
    private static void ReportResult(Editor editor, MeasureSession session, string layerName, bool pieceMode, int drawn)
    {
        if (pieceMode)
        {
            var count = session.Workspace.CountShapes(layerName);
            editor.WriteMessage(
                $"\nПодсчёт завершён. Поставлено маркеров за сеанс: {drawn}.\n" +
                $"Всего на слое «{layerName}»: {count} шт.\n" +
                "Журнал обновится автоматически.\n");
            return;
        }

        var measurement = session.Workspace.MeasureLayer(layerName);
        var lengthM = measurement.LengthDrawingUnits / session.Workspace.DrawingUnitsPerMeter;

        editor.WriteMessage(
            $"\nЗамер завершён. Нарисовано полилиний за сеанс: {drawn}.\n" +
            $"Всего на слое «{layerName}»: {measurement.PolylineCount} шт., {lengthM:N3} м.\n" +
            "Журнал обновится автоматически.\n");
    }

    /// <summary>
    /// Вставить таблицы журнала в текущее пространство чертежа.
    ///
    /// Дубли не создаются: если таблицы уже есть, пользователь выбирает —
    /// перенести их в указанную точку или создать заново на месте старых.
    /// Дальше таблицы обновляются сами вместе с журналом.
    /// </summary>
    [CommandMethod(TableCommandName, CommandFlags.Modal)]
    public void InsertJournalTable()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc is null) return;

        var editor = doc.Editor;
        var session = MeasureSession.Instance;

        try
        {
            session.SyncCurrentDrawing();

            var replaceExisting = false;

            if (session.Tables.HasTable())
            {
                var options = new PromptKeywordOptions(
                    "\nВедомость уже есть в чертеже. Что сделать?")
                {
                    AllowNone = false
                };

                options.Keywords.Add("Переместить");
                options.Keywords.Add("Создать");
                options.Keywords.Add("Отмена");
                options.Keywords.Default = "Переместить";

                var answer = editor.GetKeywords(options);
                if (answer.Status != PromptStatus.OK || answer.StringResult == "Отмена")
                {
                    editor.WriteMessage("\nВставка таблиц отменена.\n");
                    return;
                }

                replaceExisting = answer.StringResult == "Создать";
            }

            var pointOptions = new PromptPointOptions(
                "\nТочка вставки таблиц (левый верхний угол): ")
            {
                AllowNone = false
            };

            var point = editor.GetPoint(pointOptions);
            if (point.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\nВставка таблиц отменена.\n");
                return;
            }

            var result = session.Tables.Insert(point.Value, replaceExisting);
            editor.WriteMessage($"\n{result.Message}\n");
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage($"\nНе удалось вставить таблицы журнала: {ex.Message}\n");
        }
    }

    /// <summary>Перечитать materials.json с диска.</summary>
    [CommandMethod("CMPRELOAD", CommandFlags.Modal)]
    public void ReloadMaterials()
    {
        var editor = AcadApp.DocumentManager.MdiActiveDocument?.Editor;
        var session = MeasureSession.Instance;

        try
        {
            session.ReloadMaterials();
            editor?.WriteMessage(
                $"\nРеестр материалов перечитан: {session.Materials.LoadedFrom}\n" +
                $"Позиций: {session.Materials.Materials.Count}\n");
        }
        catch (System.Exception ex)
        {
            editor?.WriteMessage($"\nНе удалось перечитать реестр материалов: {ex.Message}\n");
        }
    }

    /// <summary>Краткая справка по командам плагина.</summary>
    [CommandMethod("CMPHELP", CommandFlags.Modal)]
    public void ShowHelp()
    {
        var editor = AcadApp.DocumentManager.MdiActiveDocument?.Editor;
        editor?.WriteMessage(
            "\n=== Замеры ПТО ===\n" +
            "CMP (или ЗАМЕРЫ) — открыть палитру замеров.\n" +
            $"{DrawCommandName} — режим рисования замерных полилиний (обычно запускается кнопкой «Начать замер»).\n" +
            "CMPRELOAD — перечитать materials.json.\n" +
            "CMPHELP — эта справка.\n");
    }

    /// <summary>
    /// Вернуть слой, который был текущим до замера.
    /// Если слой успели удалить или заморозить — молча остаёмся на замерном.
    /// </summary>
    private static void RestoreCurrentLayer(Database db, ObjectId previousLayerId)
    {
        try
        {
            if (previousLayerId.IsNull || previousLayerId.IsErased) return;

            using var tr = db.TransactionManager.StartTransaction();
            if (tr.GetObject(previousLayerId, OpenMode.ForRead) is LayerTableRecord { IsFrozen: false })
                db.Clayer = previousLayerId;

            tr.Commit();
        }
        catch (AcadException)
        {
            // Не критично: текущим останется слой замера.
        }
    }
}
