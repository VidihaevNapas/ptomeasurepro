using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMeasureDomain.Models;
using CadMeasureDomain.Services;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcadException = Autodesk.AutoCAD.Runtime.Exception;

namespace CadMeasurePlugin.Services;

/// <summary>Итог вставки или обновления таблицы.</summary>
/// <param name="Success">Операция выполнена.</param>
/// <param name="Message">Что показать пользователю.</param>
public readonly record struct TableOperationResult(bool Success, string Message)
{
    public static TableOperationResult Ok(string message) => new(true, message);

    public static TableOperationResult Fail(string message) => new(false, message);
}

/// <summary>
/// Ведомость на чертеже — нативная таблица AutoCAD (<see cref="Table"/>).
///
/// Таблица здесь ПРОИЗВОДНОЕ отображение журнала: собственного скана
/// пространства модели сервис не делает и расчётов не ведёт, он только
/// перерисовывает содержимое по строкам <see cref="StatementBuilder"/> —
/// тем же самым, что уходят в Excel. Поэтому обновление стоит ровно ничего
/// сверх обычного автообновления журнала, а два представления ведомости
/// не могут разойтись.
///
/// Ссылка на таблицу хранится в словаре именованных объектов чертежа
/// (NamedObjectsDictionary) в виде Xrecord с мягким указателем: так она
/// переживает сохранение и переоткрытие DWG, а сама таблица остаётся обычным
/// объектом, который пользователь волен двигать и удалять.
/// </summary>
public sealed class AcadMeasurementTableService
{
    /// <summary>Ключ словаря, под которым хранится ссылка на таблицу ведомости.</summary>
    public const string TableDictionaryKey = "PTO_MEASURE_PRO_TABLE_ID";

    /// <summary>
    /// Ключи прежней версии, когда таблиц было две.
    /// При вставке новой ведомости эти записи убираются из словаря, чтобы
    /// плагин не считал старые таблицы своими. Сами объекты не стираются —
    /// удалять чужую графику без спроса нельзя.
    /// </summary>
    private static readonly string[] LegacyDictionaryKeys =
    {
        "PTO_MEASURE_PRO_LINEAR_TABLE_ID",
        "PTO_MEASURE_PRO_PIECE_TABLE_ID"
    };

    private const string TableDxfName = "ACAD_TABLE";

    /// <summary>п/п, наименование, ед. изм., кол-во.</summary>
    private const int ColumnCount = 4;

    /// <summary>Заголовок ведомости и шапка занимают две строки сверху.</summary>
    private const int HeaderRowCount = 2;

    private readonly MeasurementJournal _journal;
    private readonly AcadWorkspace _workspace;
    private readonly TextStyleService _textStyles;

    public AcadMeasurementTableService(
        MeasurementJournal journal,
        AcadWorkspace workspace,
        TextStyleService textStyles)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _textStyles = textStyles ?? throw new ArgumentNullException(nameof(textStyles));
    }

    /// <summary>Есть ли в активном чертеже живая таблица ведомости.</summary>
    public bool HasTable()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc is null) return false;

        try
        {
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var found = !ResolveTableId(tr, doc.Database).IsNull;
                tr.Commit();
                return found;
            }
        }
        catch (AcadException)
        {
            return false;
        }
    }

    // ======================= Вставка =======================

    /// <summary>
    /// Создать ведомость в текущем пространстве (модель или лист) в указанной точке.
    ///
    /// Дубль не создаётся: если таблица уже есть, вызывающий код спрашивает
    /// пользователя и передаёт нужный режим.
    /// </summary>
    /// <param name="insertionPoint">Левый верхний угол таблицы.</param>
    /// <param name="replaceExisting">
    /// true — удалить прежнюю таблицу и создать новую на указанном месте;
    /// false — существующую перенести в указанную точку.
    /// </param>
    public TableOperationResult Insert(Point3d insertionPoint, bool replaceExisting)
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc is null) return TableOperationResult.Fail("Нет активного чертежа.");

        var db = doc.Database;
        int rowCount;
        int legacyDropped;

        try
        {
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                legacyDropped = ForgetLegacyTables(tr, db);

                var layerId = EnsureTableLayer(tr, db);
                var table = PrepareTable(tr, db, layerId, replaceExisting);

                rowCount = FillTable(table, _textStyles.EnsureTableStyle(tr, db));
                table.Position = insertionPoint;

                tr.Commit();
            }

            RegenSafely(doc);
        }
        catch (AcadException ex)
        {
            return TableOperationResult.Fail($"AutoCAD отказал во вставке таблицы: {ex.Message}");
        }
        catch (Exception ex)
        {
            return TableOperationResult.Fail($"Не удалось вставить таблицу: {ex.Message}");
        }

        var legacyNote = legacyDropped == 0
            ? string.Empty
            : $" Таблицы прежнего формата ({legacyDropped}) больше не обновляются — удали их вручную.";

        return TableOperationResult.Ok(
            $"Ведомость вставлена, строк: {rowCount}. Обновляется автоматически.{legacyNote}");
    }

    // ======================= Автообновление =======================

    /// <summary>
    /// Перезаполнить существующую таблицу по текущему состоянию журнала.
    ///
    /// Вызывается после автообновления журнала и после ручных правок в палитре.
    /// Своего скана чертежа не делает. Если таблицы нет или ссылка битая —
    /// молча ничего не делает: это штатная ситуация, пока пользователь
    /// не вставил ведомость или уже удалил её.
    ///
    /// Исключения наружу не выпускает: метод дёргается из обработчиков
    /// событий AutoCAD, и падение там уронило бы приложение.
    /// </summary>
    /// <returns>true, если таблица нашлась и была перезаполнена.</returns>
    public bool Refresh()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc is null) return false;

        try
        {
            var updated = false;

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var id = ResolveTableId(tr, doc.Database);
                if (!id.IsNull && tr.GetObject(id, OpenMode.ForWrite) is Table table)
                {
                    FillTable(table, _textStyles.EnsureTableStyle(tr, doc.Database));
                    updated = true;
                }

                tr.Commit();
            }

            if (updated) RegenSafely(doc);
            return updated;
        }
        catch (Exception)
        {
            // Чертёж могли закрыть, заблокировать или он занят другой операцией.
            // Следующее автообновление всё исправит.
            return false;
        }
    }

    // ======================= Ссылка в словаре чертежа =======================

    /// <summary>
    /// Достать из словаря ObjectId таблицы и убедиться, что он ещё живой.
    ///
    /// Проверяется всё, что может испортиться между сеансами: запись в словаре,
    /// содержимое Xrecord, существование объекта, признак стирания и то, что
    /// это действительно Table. Битая ссылка тут же вычищается — иначе она
    /// копилась бы в чертеже и мешала вставить таблицу заново.
    /// </summary>
    private static ObjectId ResolveTableId(Transaction tr, Database db)
    {
        var dictionary = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (!dictionary.Contains(TableDictionaryKey)) return ObjectId.Null;

        ObjectId stored;
        try
        {
            var xrecordId = dictionary.GetAt(TableDictionaryKey);
            if (tr.GetObject(xrecordId, OpenMode.ForRead) is not Xrecord xrecord)
            {
                RemoveDictionaryEntry(tr, db, TableDictionaryKey);
                return ObjectId.Null;
            }

            stored = ReadObjectId(xrecord);
        }
        catch (AcadException)
        {
            RemoveDictionaryEntry(tr, db, TableDictionaryKey);
            return ObjectId.Null;
        }

        if (stored.IsNull || stored.IsErased || !stored.IsValid ||
            stored.ObjectClass.DxfName != TableDxfName)
        {
            RemoveDictionaryEntry(tr, db, TableDictionaryKey);
            return ObjectId.Null;
        }

        return stored;
    }

    private static ObjectId ReadObjectId(Xrecord xrecord)
    {
        var data = xrecord.Data;
        if (data is null) return ObjectId.Null;

        foreach (TypedValue value in data)
        {
            if (value.Value is ObjectId id) return id;
        }

        return ObjectId.Null;
    }

    private static void StoreObjectId(Transaction tr, Database db, ObjectId tableId)
    {
        var dictionary = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForWrite);

        var xrecord = new Xrecord
        {
            // SoftPointerId сохраняется как дескриптор объекта, поэтому ссылка
            // переживает сохранение и переоткрытие чертежа.
            Data = new ResultBuffer(new TypedValue((int)DxfCode.SoftPointerId, tableId))
        };

        dictionary.SetAt(TableDictionaryKey, xrecord);
        tr.AddNewlyCreatedDBObject(xrecord, true);
    }

    private static void RemoveDictionaryEntry(Transaction tr, Database db, string key)
    {
        try
        {
            var dictionary = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
            if (!dictionary.Contains(key)) return;

            dictionary.UpgradeOpen();
            dictionary.Remove(key);
        }
        catch (AcadException)
        {
            // Словарь занят — оставим как есть, вычистим при следующем заходе.
        }
    }

    /// <summary>Перестать считать своими таблицы прежнего двухтабличного формата.</summary>
    private static int ForgetLegacyTables(Transaction tr, Database db)
    {
        var dropped = 0;

        foreach (var key in LegacyDictionaryKeys)
        {
            var dictionary = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
            if (!dictionary.Contains(key)) continue;

            RemoveDictionaryEntry(tr, db, key);
            dropped++;
        }

        return dropped;
    }

    // ======================= Создание объекта таблицы =======================

    /// <summary>
    /// Вернуть таблицу, готовую к заполнению: существующую (при необходимости
    /// удалив и создав заново) либо новую, уже добавленную в чертёж и словарь.
    /// </summary>
    private static Table PrepareTable(Transaction tr, Database db, ObjectId layerId, bool replaceExisting)
    {
        var existingId = ResolveTableId(tr, db);

        if (!existingId.IsNull)
        {
            if (!replaceExisting && tr.GetObject(existingId, OpenMode.ForWrite) is Table existing)
                return existing;

            // Пересоздание: старую таблицу стираем и забываем ссылку.
            try
            {
                if (tr.GetObject(existingId, OpenMode.ForWrite) is Table old) old.Erase();
            }
            catch (AcadException)
            {
                // Не стёрлась — не страшно, ссылку всё равно перезапишем.
            }

            RemoveDictionaryEntry(tr, db, TableDictionaryKey);
        }

        var table = new Table
        {
            TableStyle = db.Tablestyle,
            LayerId = layerId
        };

        // Таблица должна попасть в то пространство, где сейчас работает
        // пользователь: модель или лист.
        var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
        space.AppendEntity(table);
        tr.AddNewlyCreatedDBObject(table, true);

        StoreObjectId(tr, db, table.ObjectId);
        return table;
    }

    /// <summary>Слой таблицы: создаётся при первой вставке.</summary>
    private static ObjectId EnsureTableLayer(Transaction tr, Database db)
    {
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

        if (layers.Has(PluginSettings.TableLayerName))
            return layers[PluginSettings.TableLayerName];

        layers.UpgradeOpen();

        var layer = new LayerTableRecord
        {
            Name = PluginSettings.TableLayerName,
            Color = Color.FromColorIndex(ColorMethod.ByAci, PluginSettings.TableLayerColorIndex)
        };

        var id = layers.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
        return id;
    }

    // ======================= Заполнение =======================

    /// <summary>
    /// Перерисовать содержимое таблицы по журналу.
    ///
    /// Структура: объединённый заголовок, шапка, строки материалов.
    /// Ни итогов, ни примечаний, ни лишних колонок.
    /// Возвращает количество строк материалов.
    /// </summary>
    private int FillTable(Table table, ObjectId textStyleId)
    {
        var rows = StatementBuilder.Build(_journal, _workspace.CurrentDrawingFileName);

        // Пустую ведомость показываем строкой-заглушкой: таблица без строк
        // выглядит как сломанная.
        var dataRows = Math.Max(rows.Count, 1);
        ResetLayout(table, dataRows + HeaderRowCount);

        // --- Заголовок ведомости ---
        MergeCells(table, 0, 0, 0, ColumnCount - 1);
        SetCell(table, 0, 0, StatementBuilder.Title,
            PluginSettings.TableTitleTextHeightMm, CellAlignment.MiddleCenter, textStyleId);

        // --- Шапка ---
        for (var column = 0; column < ColumnCount; column++)
        {
            SetCell(table, 1, column, StatementBuilder.ColumnHeaders[column],
                PluginSettings.TableTextHeightMm, CellAlignment.MiddleCenter, textStyleId);
        }

        // --- Строки материалов ---
        if (rows.Count == 0)
        {
            MergeCells(table, HeaderRowCount, 0, HeaderRowCount, ColumnCount - 1);
            SetCell(table, HeaderRowCount, 0, "Нет записей журнала по этому чертежу",
                PluginSettings.TableTextHeightMm, CellAlignment.MiddleCenter, textStyleId);
        }
        else
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var item = rows[i];
                var row = i + HeaderRowCount;

                SetCell(table, row, 0, item.Number.ToString(),
                    PluginSettings.TableTextHeightMm, CellAlignment.MiddleCenter, textStyleId);
                SetCell(table, row, 1, item.MaterialName,
                    PluginSettings.TableTextHeightMm, CellAlignment.MiddleLeft, textStyleId);
                SetLeftMargin(table, row, 1, PluginSettings.TableMaterialLeftMarginMm);
                SetCell(table, row, 2, item.Unit,
                    PluginSettings.TableTextHeightMm, CellAlignment.MiddleCenter, textStyleId);
                SetCell(table, row, 3, item.QuantityText,
                    PluginSettings.TableTextHeightMm, CellAlignment.MiddleCenter, textStyleId);
            }
        }

        table.GenerateLayout();
        return rows.Count;
    }

    /// <summary>
    /// Привести таблицу к нужному размеру и снять прежние объединения ячеек.
    ///
    /// Объединения обязательно снимать до смены размера: строк в журнале
    /// становится то больше, то меньше, и оставшееся объединение от прошлого
    /// заполнения «съело» бы соседние ячейки новой раскладки.
    /// </summary>
    private static void ResetLayout(Table table, int rows)
    {
        for (var row = 0; row < table.Rows.Count; row++)
        {
            try
            {
                table.UnmergeCells(CellRange.Create(table, row, 0, row, ColumnCount - 1));
            }
            catch (AcadException)
            {
                // Строка не была объединена — это норма.
            }
        }

        table.SetSize(rows, ColumnCount);

        table.Columns[0].Width = PluginSettings.TableNumberColumnWidthMm;
        table.Columns[1].Width = PluginSettings.TableMaterialColumnWidthMm;
        table.Columns[2].Width = PluginSettings.TableUnitColumnWidthMm;
        table.Columns[3].Width = PluginSettings.TableValueColumnWidthMm;

        for (var row = 0; row < rows; row++)
            table.Rows[row].Height = PluginSettings.TableRowHeightMm;
    }

    private static void MergeCells(Table table, int topRow, int leftColumn, int bottomRow, int rightColumn)
    {
        try
        {
            table.MergeCells(CellRange.Create(table, topRow, leftColumn, bottomRow, rightColumn));
        }
        catch (AcadException)
        {
            // Объединить не удалось — таблица останется читаемой и без него.
        }
    }

    private static void SetCell(
        Table table,
        int row,
        int column,
        string text,
        double textHeight,
        CellAlignment alignment,
        ObjectId textStyleId)
    {
        var cell = table.Cells[row, column];

        cell.TextString = text ?? string.Empty;

        try
        {
            // Стиль назначается ячейкам, а не общему TableStyle: тот может быть
            // общим для других таблиц чертежа, и правка задела бы их тоже.
            if (!textStyleId.IsNull) cell.TextStyleId = textStyleId;

            cell.TextHeight = textHeight;
            cell.Alignment = alignment;
        }
        catch (AcadException)
        {
            // Стиль таблицы может запрещать переопределение — текст важнее оформления.
        }
    }

    /// <summary>
    /// Отступ текста от левой линии ячейки.
    ///
    /// Задаётся поячеечно: табличные <c>HorizontalCellMargin</c> и стиль таблицы
    /// действуют сразу на все колонки, а отступ нужен только под наименование —
    /// в отцентрованных колонках он бы просто съедал ширину.
    /// </summary>
    private static void SetLeftMargin(Table table, int row, int column, double margin)
    {
        try
        {
            table.Cells[row, column].Borders.Left.Margin = margin;
        }
        catch (AcadException)
        {
            // Стиль таблицы может запрещать переопределение полей —
            // без отступа таблица останется читаемой.
        }
    }

    /// <summary>
    /// Обновить графику. Regen вызывается только при живом документе и гасит
    /// собственные ошибки: метод зовут из обработчиков событий, где исключение
    /// уронило бы AutoCAD.
    /// </summary>
    private static void RegenSafely(Autodesk.AutoCAD.ApplicationServices.Document doc)
    {
        try
        {
            doc.Editor.Regen();
        }
        catch (AcadException)
        {
            try
            {
                doc.Editor.UpdateScreen();
            }
            catch (AcadException)
            {
                // Обновление экрана не критично: содержимое таблицы уже записано.
            }
        }
    }
}
