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
