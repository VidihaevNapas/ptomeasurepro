using System.Collections.ObjectModel;

namespace CadMeasureDomain.Models;

/// <summary>
/// Журнал замеров: одна строка на сочетание «материал + участок + DWG».
///
/// Журнал живёт на уровне сессии AutoCAD и накапливает замеры по всем открытым
/// чертежам: каждая запись помнит свой DWG (<see cref="MeasurementRecord.DrawingFileName"/>),
/// а <see cref="CurrentDrawingFileName"/> показывает, к какому чертежу будут
/// привязаны новые записи.
///
/// Коллекция — ObservableCollection, чтобы таблица в палитре обновлялась сама.
/// Все операции выполняются в UI-потоке палитры.
/// </summary>
public sealed class MeasurementJournal
{
    private readonly Dictionary<string, MeasurementRecord> _index = new(StringComparer.Ordinal);

    /// <summary>Все записи журнала — по всем чертежам.</summary>
    public ObservableCollection<MeasurementRecord> Records { get; } = new();

    /// <summary>Имя активного DWG (без пути). Новые записи привязываются к нему.</summary>
    public string CurrentDrawingFileName { get; set; } = string.Empty;

    /// <summary>
    /// Создать или обновить запись линейного материала (труба/воздуховод).
    /// Если запись с таким ключом уже есть — перезаписываются длины и площадь.
    /// </summary>
    public MeasurementRecord AddOrUpdateLinear(
        Material material,
        string section,
        string layerName,
        double horizontalLengthM,
        double verticalLengthM,
        int polylineCount,
        string drawingFileName)
    {
        ArgumentNullException.ThrowIfNull(material);

        var key = MeasurementRecord.BuildKey(material.Name, section, drawingFileName);
        if (!_index.TryGetValue(key, out var record))
        {
            record = new MeasurementRecord
            {
                MaterialClass = material.Class,
                MaterialName = material.Name,
                Section = (section ?? string.Empty).Trim(),
                DrawingFileName = drawingFileName ?? string.Empty
            };
            _index[key] = record;
            Records.Add(record);
        }

        record.Characteristic = material.Characteristic;
        record.Unit = material.Unit;
        record.LayerName = layerName;
        // Слагаемые хранятся с полной точностью: округляется только итог
        // (см. MeasurementRounding), иначе ошибка копилась бы на каждой полилинии.
        record.HorizontalLengthM = horizontalLengthM;
        record.VerticalLengthM = verticalLengthM;
        // Удельная площадь берётся из материала при каждом пересчёте: размеры
        // позиции могли поменяться в реестре между замерами.
        record.AreaPerMeterM2 = Services.DuctAreaCalculator.GetAreaPerMeterM2(material);
        record.PolylineCount = polylineCount;
        record.UpdatedAt = DateTime.Now;

        return record;
    }

    /// <summary>
    /// Создать или обновить запись штучного изделия.
    /// Используется будущим инструментом «Посчитать штучки».
    /// </summary>
    public MeasurementRecord AddOrUpdatePiece(
        Material material,
        string section,
        string layerName,
        int quantity,
        string drawingFileName)
    {
        ArgumentNullException.ThrowIfNull(material);

        var key = MeasurementRecord.BuildKey(material.Name, section, drawingFileName);
        if (!_index.TryGetValue(key, out var record))
        {
            record = new MeasurementRecord
            {
                MaterialClass = MaterialClasses.Piece,
                MaterialName = material.Name,
                Section = (section ?? string.Empty).Trim(),
                DrawingFileName = drawingFileName ?? string.Empty
            };
            _index[key] = record;
            Records.Add(record);
        }

        record.Characteristic = material.Characteristic;
        record.Unit = string.IsNullOrWhiteSpace(material.Unit) ? "шт." : material.Unit;
        record.PieceKind = material.PieceKind ?? string.Empty;
        record.LayerName = layerName;
        record.ScannedQuantity = quantity;
        record.UpdatedAt = DateTime.Now;

        return record;
    }

    /// <summary>
    /// Завести запись по позиции спецификации — до всякого замера.
    ///
    /// Такая запись существует, даже когда в чертеже ещё ничего не начерчено:
    /// это план, по которому предстоит работать. Класс материала берётся
    /// из реестра, если позиция там нашлась; если нет — выводится из единицы
    /// измерения спецификации, чтобы строка хотя бы знала, чем её мерить.
    /// </summary>
    /// <param name="item">Позиция спецификации.</param>
    /// <param name="specificationFileName">Файл спецификации.</param>
    /// <param name="drawingFileName">Чертёж, к которому привязывается запись.</param>
    /// <param name="material">Материал реестра, если наименование совпало.</param>
    public MeasurementRecord AddFromSpecification(
        SpecificationItem item,
        string specificationFileName,
        string drawingFileName,
        Material? material)
    {
        ArgumentNullException.ThrowIfNull(item);

        var key = MeasurementRecord.BuildKey(item.Name, string.Empty, drawingFileName);
        if (!_index.TryGetValue(key, out var record))
        {
            record = new MeasurementRecord
            {
                MaterialName = item.Name,
                DrawingFileName = drawingFileName ?? string.Empty
            };
            _index[key] = record;
            Records.Add(record);
        }

        record.MaterialClass = material?.Class
            ?? (item.MeasurementType == MeasurementType.Pieces ? MaterialClasses.Piece : MaterialClasses.Pipe);
        record.Characteristic = material?.Characteristic ?? string.Empty;
        record.PieceKind = material?.PieceKind ?? string.Empty;
        record.Unit = item.Unit;
        record.MaterialMissing = material is null;
        record.UpdatedAt = DateTime.Now;

        BindToSpecification(record, item, specificationFileName);
        return record;
    }

    /// <summary>
    /// Привязать существующую запись к позиции спецификации.
    ///
    /// Вызывается и при импорте, и после замера: если замеренный материал
    /// нашёлся в спецификации, строка журнала должна знать свою позицию,
    /// иначе подсчёт некуда положить.
    /// </summary>
    public static void BindToSpecification(
        MeasurementRecord record,
        SpecificationItem item,
        string specificationFileName)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(item);

        record.SpecificationItemId = item.Number;
        record.SpecificationFileName = specificationFileName ?? string.Empty;
        record.SpecificationQuantity = item.Quantity;
        record.Mark = item.Mark;
        record.EquipmentCode = item.EquipmentCode;
        record.Manufacturer = item.Manufacturer;
    }

    /// <summary>
    /// Перепривязать журнал к другой спецификации — при её перезагрузке.
    ///
    /// Выбранная стратегия: СОХРАНЯТЬ ЗАПИСИ И ПЕРЕПРИВЯЗЫВАТЬ ПО НАИМЕНОВАНИЮ
    /// МАТЕРИАЛА, а если позиции с таким наименованием в новом файле нет —
    /// снимать привязку, оставляя запись в журнале.
    ///
    /// Почему так, а не удаление записей: замер сделан по чертежу и от смены
    /// файла спецификации не перестаёт быть верным. Потерять его из-за того,
    /// что проектировщик прислал новую редакцию, было бы худшим из возможных
    /// поведений. Номера позиций при этом не переносятся: в новой редакции
    /// нумерация другая, и старый номер указывал бы не на ту строку.
    ///
    /// Записи, заведённые из спецификации и не имеющие геометрии, тоже
    /// остаются — просто уже без привязки; лишние строки пользователь удалит
    /// сам, а восстановить случайно стёртые нечем.
    /// </summary>
    /// <param name="specification">Новая спецификация; null — снять все привязки.</param>
    /// <returns>Сколько записей перепривязано и сколько потеряло привязку.</returns>
    public (int Rebound, int Unbound) RebindToSpecification(Specification? specification)
    {
        var rebound = 0;
        var unbound = 0;

        foreach (var record in Records)
        {
            if (!record.IsFromSpecification) continue;

            var item = specification?.FindByName(record.MaterialName);
            if (item is null)
            {
                record.ClearSpecificationBinding();
                unbound++;
                continue;
            }

            BindToSpecification(record, item, specification!.FileName);
            rebound++;
        }

        return (rebound, unbound);
    }

    /// <summary>Записи, привязанные к позиции спецификации, по всем чертежам.</summary>
    public IReadOnlyList<MeasurementRecord> FindBySpecificationItem(int itemNumber) =>
        Records.Where(r => r.SpecificationItemId == itemNumber).ToList();

    /// <summary>Найти запись по «материал + участок + DWG».</summary>
    public MeasurementRecord? Find(string materialName, string section, string drawingFileName) =>
        _index.TryGetValue(MeasurementRecord.BuildKey(materialName, section, drawingFileName), out var r) ? r : null;

    /// <summary>Все записи, привязанные к конкретному слою (в пределах указанного DWG).</summary>
    public IReadOnlyList<MeasurementRecord> FindByLayer(string layerName, string? drawingFileName = null) =>
        Records.Where(r =>
                string.Equals(r.LayerName, layerName, StringComparison.OrdinalIgnoreCase) &&
                (drawingFileName is null ||
                 string.Equals(r.DrawingFileName, drawingFileName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    /// <summary>Записи конкретного чертежа.</summary>
    public IReadOnlyList<MeasurementRecord> GetRecordsForDrawing(string drawingFileName) =>
        Records.Where(r => string.Equals(r.DrawingFileName, drawingFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>Слои, задействованные в журнале (для режима «показать только слои замеров»).</summary>
    public IReadOnlyCollection<string> GetUsedLayerNames(string? drawingFileName = null)
    {
        var query = Records.AsEnumerable();
        if (drawingFileName is not null)
            query = query.Where(r => string.Equals(r.DrawingFileName, drawingFileName, StringComparison.OrdinalIgnoreCase));

        return query.Select(r => r.LayerName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool Remove(MeasurementRecord record)
    {
        if (record is null) return false;
        _index.Remove(record.Key);
        return Records.Remove(record);
    }

    /// <summary>
    /// Есть ли в журнале другая запись с таким ключом.
    /// Нужно при правке материала или участка в таблице: две строки
    /// с одним ключом «материал + участок + DWG» недопустимы.
    /// </summary>
    public bool HasConflict(string materialName, string section, string drawingFileName, MeasurementRecord except)
    {
        var found = Find(materialName, section, drawingFileName);
        return found is not null && !ReferenceEquals(found, except);
    }

    /// <summary>
    /// Перепривязать запись к новому ключу после правки материала или участка.
    /// Индекс строится по ключу, поэтому без перепривязки запись «потерялась» бы
    /// для последующих пересчётов и удалений.
    /// </summary>
    public void Rekey(MeasurementRecord record, string previousKey)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!string.IsNullOrEmpty(previousKey)) _index.Remove(previousKey);
        _index[record.Key] = record;
    }

    /// <summary>
    /// Очистить журнал текущей сессии.
    /// Геометрию, слои, подписи и реестр материалов не трогает: журнал
    /// выводится из чертежа, поэтому при следующем пересчёте записи
    /// соберутся по нему заново.
    /// </summary>
    public void Clear()
    {
        _index.Clear();
        Records.Clear();
    }
}
