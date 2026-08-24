using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMeasureDomain.Services;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcadException = Autodesk.AutoCAD.Runtime.Exception;
// В AutoCAD есть свой тип Material (материал визуализации) — уточняем, что речь о нашем.
using Material = CadMeasureDomain.Models.Material;

namespace CadMeasurePlugin.Services;

/// <summary>Результат удаления замерной геометрии со слоя.</summary>
/// <param name="Erased">Сколько объектов удалено.</param>
/// <param name="Remaining">Сколько объектов удалить не удалось.</param>
/// <param name="Message">Пояснение к неудаче, либо null.</param>
public readonly record struct EraseGeometryResult(int Erased, int Remaining, string? Message)
{
    public bool FullyErased => Remaining == 0;
}

/// <summary>
/// Работа со слоями замеров в AutoCAD: создание, активация, видимость,
/// удаление замерной геометрии.
///
/// Имена слоёв сервис не выдумывает — он берёт их у доменного
/// <see cref="LayerNameFactory"/>, чтобы плагин и домен всегда сходились
/// в том, какой слой принадлежит какому материалу.
/// </summary>
public sealed class LayerService
{
    private readonly LayerNameFactory _layerNames;
    private readonly LabelService _labels;

    public LayerService(LayerNameFactory layerNames, LabelService labels)
    {
        _layerNames = layerNames ?? throw new ArgumentNullException(nameof(layerNames));
        _labels = labels ?? throw new ArgumentNullException(nameof(labels));
    }

    /// <summary>
    /// Имя слоя материала на участке. Слой при этом не создаётся.
    /// Работает для любого материала: если характеристик нет, включается
    /// запасной шаблон (см. <see cref="LayerNameFactory.ComposeBaseName"/>).
    /// </summary>
    public string GetLayerName(Material material, string? section = null)
    {
        ArgumentNullException.ThrowIfNull(material);
        return _layerNames.GetLayerName(material, section);
    }

    /// <summary>
    /// Найти слой материала для участка, создать при отсутствии и сделать текущим.
    /// Возвращает имя слоя.
    /// </summary>
    public string EnsureLayerForSection(Material material, string? section) =>
        EnsureLayer(material, GetLayerName(material, section));

    /// <summary>
    /// Все имена слоёв активного чертежа.
    /// Нужно для автосканирования: журнал строится по тем слоям, которые
    /// реально есть в чертеже и разбираются в пару «материал + участок».
    /// </summary>
    public IReadOnlyList<string> GetAllLayerNames()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc is null) return Array.Empty<string>();

        var names = new List<string>();

        using var tr = doc.Database.TransactionManager.StartTransaction();
        var layerTable = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);

        foreach (ObjectId layerId in layerTable)
        {
            if (layerId.IsErased) continue;
            var layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead);
            names.Add(layer.Name);
        }

        tr.Commit();
        return names;
    }

    /// <summary>
    /// Найти слой по имени, создать при отсутствии и сделать текущим.
    ///
    /// Цвет и вес линии назначаются только при создании: если инженер потом
    /// перекрасил слой руками, плагин его настройки не затирает.
    /// Существующий слой размораживается, включается и разблокируется —
    /// иначе на нём нельзя ни рисовать, ни сделать его текущим.
    /// </summary>
    public string EnsureLayer(Material material, string layerName)
    {
        ArgumentNullException.ThrowIfNull(material);

        if (!LayerNameSanitizer.IsValidLayerName(layerName))
            throw new InvalidOperationException(
                $"Имя слоя «{layerName}» недопустимо в AutoCAD. " +
                $"Проверь позицию «{material.Name}» в materials.json.");

        var doc = AcadApp.DocumentManager.MdiActiveDocument
                  ?? throw new InvalidOperationException("Нет активного чертежа — слой создать негде.");
        var db = doc.Database;

        using (doc.LockDocument())
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

            ObjectId layerId;
            if (layerTable.Has(layerName))
            {
                layerId = layerTable[layerName];
                var existing = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead);

                if (existing.IsFrozen || existing.IsOff || existing.IsLocked)
                {
                    existing.UpgradeOpen();
                    existing.IsFrozen = false;
                    existing.IsOff = false;
                    existing.IsLocked = false;
                }
            }
            else
            {
                layerTable.UpgradeOpen();

                var layer = new LayerTableRecord
                {
                    Name = layerName,
                    Color = Color.FromColorIndex(ColorMethod.ByAci, LayerColorService.GetColorIndex(material)),
                    LineWeight = PluginSettings.MeasureLineWeight
                };

                layerId = layerTable.Add(layer);
                tr.AddNewlyCreatedDBObject(layer, true);
            }

            db.Clayer = layerId;

            // Без этого назначенный вес линии на экране не виден.
            db.LineWeightDisplay = true;

            tr.Commit();
        }

        return layerName;
    }

    /// <summary>Имя текущего слоя чертежа, либо null.</summary>
    public string? GetCurrentLayerName()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc is null) return null;

        try
        {
            using var tr = doc.Database.TransactionManager.StartTransaction();
            var layer = (LayerTableRecord)tr.GetObject(doc.Database.Clayer, OpenMode.ForRead);
            var name = layer.Name;
            tr.Commit();
            return name;
        }
        catch (AcadException)
        {
            return null;
        }
    }

    /// <summary>Является ли слой текущим в активном чертеже.</summary>
    public bool IsCurrentLayer(string layerName) =>
        !string.IsNullOrWhiteSpace(layerName) &&
        string.Equals(GetCurrentLayerName(), layerName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Существует ли слой в активном чертеже.</summary>
    public bool LayerExists(string layerName)
    {
        if (string.IsNullOrWhiteSpace(layerName)) return false;

        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc is null) return false;

        using var tr = doc.Database.TransactionManager.StartTransaction();
        var layerTable = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
        var exists = layerTable.Has(layerName);
        tr.Commit();
        return exists;
    }

    /// <summary>
    /// Поставить круг-маркер штучного изделия на текущем слое и подписать
    /// его порядковым номером.
    ///
    /// Диаметр фиксирован (<see cref="PluginSettings.PieceMarkerDiameterMm"/>):
    /// именно по нему сканирование потом отличает маркеры от прочей графики
    /// на слое, поэтому рисовать их вручную не следует.
    ///
    /// Круг и номер создаются в одной транзакции — маркера без номера
    /// в чертеже не появится.
    /// </summary>
    /// <param name="center">Центр изделия, указанный пользователем.</param>
    /// <param name="number">Порядковый номер штучки на слое.</param>
    public void AddPieceMarker(Point3d center, int number)
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument
                  ?? throw new InvalidOperationException("Нет активного чертежа.");
        var db = doc.Database;

        using (doc.LockDocument())
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            // Слой и цвет — ByLayer: слой уже сделан текущим EnsureLayer.
            var marker = new Circle(center, Vector3d.ZAxis, PluginSettings.PieceMarkerDiameterMm / 2.0)
            {
                LayerId = db.Clayer
            };

            modelSpace.AppendEntity(marker);
            tr.AddNewlyCreatedDBObject(marker, true);

            var label = _labels.CreatePieceLabel(db, tr, marker, number);
            modelSpace.AppendEntity(label);
            tr.AddNewlyCreatedDBObject(label, true);

            tr.Commit();
        }
    }

    /// <summary>
    /// Перенести замерную геометрию и подписи с одного слоя на другой.
    ///
    /// Используется при правке материала или участка в таблице журнала:
    /// слой кодирует «материал + участок», поэтому смена любого из них —
    /// это перенос объектов на другой слой. Геометрия при этом не удаляется
    /// и не перерисовывается, меняется только принадлежность слою.
    ///
    /// Возвращает количество перенесённых объектов.
    /// </summary>
    public int MoveMeasurementGeometry(string fromLayer, string toLayer, bool pieceMode)
    {
        if (string.IsNullOrWhiteSpace(fromLayer) || string.IsNullOrWhiteSpace(toLayer)) return 0;
        if (string.Equals(fromLayer, toLayer, StringComparison.OrdinalIgnoreCase)) return 0;

        var doc = AcadApp.DocumentManager.MdiActiveDocument
                  ?? throw new InvalidOperationException("Нет активного чертежа.");
        var db = doc.Database;
        var moved = 0;

        using (doc.LockDocument())
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!layerTable.Has(fromLayer) || !layerTable.Has(toLayer))
            {
                tr.Commit();
                return 0;
            }

            var targetId = layerTable[toLayer];

            // Исходный слой мог остаться заблокированным — иначе объект
            // не отдадут на запись и перенос молча провалится.
            var source = (LayerTableRecord)tr.GetObject(layerTable[fromLayer], OpenMode.ForRead);
            var wasLocked = source.IsLocked;
            var wasFrozen = source.IsFrozen;

            if (wasLocked || wasFrozen)
            {
                source.UpgradeOpen();
                source.IsLocked = false;
                source.IsFrozen = false;
            }

            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in modelSpace)
            {
                if (id.IsErased || !id.IsValid) continue;
                if (!MeasurementGeometry.IsCandidateClass(id.ObjectClass.DxfName, pieceMode)) continue;

                if (tr.GetObject(id, OpenMode.ForRead) is not Entity entity) continue;
                if (!string.Equals(entity.Layer, fromLayer, StringComparison.OrdinalIgnoreCase)) continue;
                if (!MeasurementGeometry.IsMeasurementEntity(entity, pieceMode)) continue;

                try
                {
                    entity.UpgradeOpen();
                    entity.LayerId = targetId;
                    moved++;
                }
                catch (AcadException)
                {
                    // Объект держат открытым или он на защищённом слое —
                    // остальные всё равно переносим.
                }
            }

            if (wasLocked || wasFrozen)
            {
                source.IsLocked = wasLocked;
                source.IsFrozen = wasFrozen;
            }

            tr.Commit();
        }

        return moved;
    }

    /// <summary>
    /// Удалить пустой слой из чертежа.
    ///
    /// Вызывается после удаления материала, когда вся его геометрия уже стёрта.
    /// AutoCAD не даёт удалить слой, который остаётся текущим или на который
    /// что-то ссылается, поэтому неудача здесь — норма, а не ошибка:
    /// метод возвращает false, и вызывающий код просто идёт дальше.
    /// </summary>
    public bool TryDeleteLayer(string layerName)
    {
        if (string.IsNullOrWhiteSpace(layerName)) return false;

        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc is null) return false;

        var db = doc.Database;

        try
        {
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (!layerTable.Has(layerName))
                {
                    tr.Commit();
                    return false;
                }

                var layerId = layerTable[layerName];

                // Слой «0» и текущий слой удалить нельзя — уводим текущий на «0».
                if (layerId == db.Clayer)
                {
                    if (!layerTable.Has("0"))
                    {
                        tr.Commit();
                        return false;
                    }

                    db.Clayer = layerTable["0"];
                }

                // Purge спрашивает у AutoCAD, действительно ли на слой никто не ссылается.
                var ids = new ObjectIdCollection { layerId };
                db.Purge(ids);

                if (ids.Count == 0)
                {
                    tr.Commit();
                    return false;
                }

                var layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForWrite);
                layer.Erase(true);

                tr.Commit();
                return true;
            }
        }
        catch (AcadException)
        {
            // Слой чем-то занят — не беда, останется пустым.
            return false;
        }
    }

    /// <summary>
    /// Удалить со слоя всю замерную геометрию.
    ///
    /// Слой на время удаления разблокируется и размораживается: на заблокированном
    /// слое AutoCAD не отдаёт объект на запись (eOnLockedLayer), и удаление
    /// молча провалилось бы. Исходное состояние слоя возвращается на место.
    ///
    /// Объекты, которые удалить не удалось, попадают в Remaining — вызывающий код
    /// по этому признаку решает, можно ли удалять запись журнала.
    /// </summary>
    /// <param name="layerName">Слой замера.</param>
    /// <param name="pieceMode">true — удалять обводки штучных изделий, false — полилинии.</param>
    public EraseGeometryResult EraseMeasurementGeometry(string layerName, bool pieceMode)
    {
        if (string.IsNullOrWhiteSpace(layerName))
            return new EraseGeometryResult(0, 0, "Не задано имя слоя.");

        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return new EraseGeometryResult(0, 0, "Нет активного чертежа.");

        var db = doc.Database;
        var erased = 0;
        var failed = 0;
        string? firstError = null;

        using (doc.LockDocument())
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!layerTable.Has(layerName))
            {
                tr.Commit();
                return new EraseGeometryResult(0, 0, $"Слоя «{layerName}» нет в чертеже — удалять нечего.");
            }

            var layer = (LayerTableRecord)tr.GetObject(layerTable[layerName], OpenMode.ForRead);
            var wasLocked = layer.IsLocked;
            var wasFrozen = layer.IsFrozen;

            if (wasLocked || wasFrozen)
            {
                layer.UpgradeOpen();
                layer.IsLocked = false;
                layer.IsFrozen = false;
            }

            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in modelSpace)
            {
                if (id.IsErased || !id.IsValid) continue;
                if (!MeasurementGeometry.IsCandidateClass(id.ObjectClass.DxfName, pieceMode)) continue;

                if (tr.GetObject(id, OpenMode.ForRead) is not Entity entity) continue;
                if (!string.Equals(entity.Layer, layerName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!MeasurementGeometry.IsMeasurementEntity(entity, pieceMode)) continue;

                try
                {
                    entity.UpgradeOpen();
                    entity.Erase();
                    erased++;
                }
                catch (AcadException ex)
                {
                    failed++;
                    firstError ??= ex.Message;
                }
            }

            // Возвращаем слою исходные блокировку и заморозку.
            if (wasLocked || wasFrozen)
            {
                layer.IsLocked = wasLocked;
                layer.IsFrozen = wasFrozen;
            }

            tr.Commit();
        }

        var message = failed > 0
            ? $"Не удалось удалить объектов: {failed}. Первая ошибка: {firstError}"
            : null;

        return new EraseGeometryResult(erased, failed, message);
    }
}
