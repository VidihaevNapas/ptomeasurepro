using CadMeasureDomain.Models;
using CadMeasureDomain.Services;

namespace CadMeasurePlugin.Services;

/// <summary>Итог правки ячейки журнала.</summary>
/// <param name="Success">Применена ли правка.</param>
/// <param name="Message">Что показать пользователю.</param>
public readonly record struct JournalEditResult(bool Success, string Message)
{
    public static JournalEditResult Ok(string message) => new(true, message);

    public static JournalEditResult Fail(string message) => new(false, message);

    /// <summary>Значение не изменилось — делать и сообщать нечего.</summary>
    public static JournalEditResult NoChange() => new(true, string.Empty);
}

/// <summary>
/// Правка журнала прямо в таблице палитры.
///
/// Здесь собрана вся проверка ввода, потому что «поменять материал» или
/// «поменять участок» — это не присваивание поля: слой кодирует пару
/// «материал + участок», поэтому такая правка означает перенос геометрии
/// на другой слой, перепривязку ключа записи и перенос вертикальных участков.
/// Если сделать это в code-behind, части операции неизбежно разъедутся.
///
/// Геометрия при этом не удаляется и не перерисовывается — меняется только
/// принадлежность объектов слою.
/// </summary>
public sealed class JournalEditService
{
    private readonly MeasurementJournal _journal;
    private readonly MaterialRepository _materials;
    private readonly LayerService _layers;
    private readonly LayerNameFactory _layerNames;
    private readonly VerticalRunStore _verticalRuns;
    private readonly AcadWorkspace _workspace;

    public JournalEditService(
        MeasurementJournal journal,
        MaterialRepository materials,
        LayerService layers,
        LayerNameFactory layerNames,
        VerticalRunStore verticalRuns,
        AcadWorkspace workspace)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _materials = materials ?? throw new ArgumentNullException(nameof(materials));
        _layers = layers ?? throw new ArgumentNullException(nameof(layers));
        _layerNames = layerNames ?? throw new ArgumentNullException(nameof(layerNames));
        _verticalRuns = verticalRuns ?? throw new ArgumentNullException(nameof(verticalRuns));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    /// <summary>Все наименования реестра — источник для выпадающего списка в колонке «Материал».</summary>
    public IReadOnlyList<string> GetMaterialNames() =>
        _materials.Materials
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.CurrentCulture)
            .ToList();

    // ======================= Материал =======================

    /// <summary>
    /// Сменить материал записи.
    ///
    /// Геометрия переезжает на слой нового материала, запись перепривязывается
    /// к новому ключу. Смена между линейным материалом и штучным запрещена:
    /// на слое лежат полилинии либо круги, и после такого переезда запись
    /// осталась бы без «своей» геометрии и была бы удалена ближайшим пересчётом.
    /// </summary>
    public JournalEditResult ChangeMaterial(MeasurementRecord record, string? newMaterialName)
    {
        ArgumentNullException.ThrowIfNull(record);

        var name = (newMaterialName ?? string.Empty).Trim();
        if (name.Length == 0) return JournalEditResult.Fail("Наименование материала не может быть пустым.");
        if (string.Equals(name, record.MaterialName, StringComparison.OrdinalIgnoreCase))
            return JournalEditResult.NoChange();

        var material = _materials.FindByName(name);
        if (material is null)
            return JournalEditResult.Fail(
                $"Материала «{name}» нет в реестре.\n" +
                "Выбери позицию из списка или сначала добавь её через «Выбрать материал».");

        if (material.Class == MaterialClasses.Piece != record.IsPiece)
        {
            return JournalEditResult.Fail(
                "Нельзя заменить линейный материал штучным изделием и наоборот.\n" +
                "На слое лежат полилинии либо круги-маркеры — после такой замены\n" +
                "запись осталась бы без своей геометрии.");
        }

        if (!IsCurrentDrawing(record, out var drawingError)) return JournalEditResult.Fail(drawingError);

        var newLayer = _layerNames.GetLayerName(material, record.Section);

        if (_journal.HasConflict(material.Name, record.Section, record.DrawingFileName, record))
        {
            return JournalEditResult.Fail(
                $"В журнале уже есть запись «{material.Name}» на участке " +
                $"«{DescribeSection(record.Section)}» в этом чертеже.\n" +
                "Две строки с одним материалом и участком недопустимы.");
        }

        var moved = Relayer(record, newLayer, material, record.Section);

        var previousKey = record.Key;

        record.MaterialName = material.Name;
        record.MaterialClass = material.Class;
        record.Unit = material.Unit;
        record.Characteristic = material.Characteristic;
        record.PieceKind = material.PieceKind ?? string.Empty;
        record.LayerName = newLayer;
        record.UpdatedAt = DateTime.Now;

        _journal.Rekey(record, previousKey);

        return JournalEditResult.Ok(
            $"Материал изменён на «{material.Name}». Слой: {newLayer}. Перенесено объектов: {moved}.");
    }

    // ======================= Участок =======================

    /// <summary>
    /// Сменить участок записи. Участок входит в имя слоя, поэтому геометрия
    /// так же переезжает на другой слой.
    /// </summary>
    public JournalEditResult ChangeSection(MeasurementRecord record, string? newSection)
    {
        ArgumentNullException.ThrowIfNull(record);

        var section = (newSection ?? string.Empty).Trim();
        if (string.Equals(section, record.Section, StringComparison.Ordinal))
            return JournalEditResult.NoChange();

        var material = _materials.FindByName(record.MaterialName);
        if (material is null)
            return JournalEditResult.Fail(
                $"Материала «{record.MaterialName}» больше нет в реестре — сначала верни позицию.");

        if (!IsCurrentDrawing(record, out var drawingError)) return JournalEditResult.Fail(drawingError);

        if (_journal.HasConflict(record.MaterialName, section, record.DrawingFileName, record))
        {
            return JournalEditResult.Fail(
                $"В журнале уже есть «{record.MaterialName}» на участке «{DescribeSection(section)}».\n" +
                "Две строки с одним материалом и участком недопустимы.");
        }

        var newLayer = _layerNames.GetLayerName(material, section);
        var moved = Relayer(record, newLayer, material, section);

        var previousKey = record.Key;

        record.Section = section;
        record.LayerName = newLayer;
        record.UpdatedAt = DateTime.Now;

        _journal.Rekey(record, previousKey);

        return JournalEditResult.Ok(
            $"Участок изменён на «{DescribeSection(section)}». Слой: {newLayer}. Перенесено объектов: {moved}.");
    }

    // ======================= Длина и количество =======================

    /// <summary>
    /// Задать длину вручную. Ручное значение переживает пересчёты: иначе
    /// правка жила бы до ближайшего скана чертежа.
    /// Пустая строка снимает ручное значение и возвращает измеренную длину.
    /// </summary>
    public JournalEditResult SetLength(MeasurementRecord record, string? text)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.IsPiece)
            return JournalEditResult.Fail("У штучных изделий нет длины — правь колонку «Количество».");

        var input = (text ?? string.Empty).Trim();

        if (input.Length == 0)
        {
            if (record.ManualLengthM is null) return JournalEditResult.NoChange();

            record.ManualLengthM = null;
            record.UpdatedAt = DateTime.Now;
            return JournalEditResult.Ok(
                $"Ручная длина снята, вернулась измеренная: {record.LengthM:N3} м.");
        }

        if (!FlexibleNullableDoubleConverter.TryParse(input, out var value))
            return JournalEditResult.Fail($"«{input}» — не число. Пример: 12,5 или 12.5");

        if (value < 0) return JournalEditResult.Fail("Длина не может быть отрицательной.");

        record.ManualLengthM = Math.Round(value, 3);
        record.UpdatedAt = DateTime.Now;

        return JournalEditResult.Ok(
            $"Длина задана вручную: {record.LengthM:N3} м. Пересчёты по чертежу её больше не меняют.");
    }

    /// <summary>
    /// Задать количество вручную.
    /// Пустая строка снимает ручное значение и возвращает подсчитанное.
    /// </summary>
    public JournalEditResult SetQuantity(MeasurementRecord record, string? text)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!record.IsPiece)
            return JournalEditResult.Fail("Количество есть только у штучных изделий — правь колонку «Длина, м».");

        var input = (text ?? string.Empty).Trim();

        if (input.Length == 0)
        {
            if (record.ManualQuantity is null) return JournalEditResult.NoChange();

            record.ManualQuantity = null;
            record.UpdatedAt = DateTime.Now;
            return JournalEditResult.Ok(
                $"Ручное количество снято, вернулось подсчитанное: {record.Quantity} шт.");
        }

        if (!FlexibleNullableDoubleConverter.TryParse(input, out var value))
            return JournalEditResult.Fail($"«{input}» — не число.");

        if (value < 0) return JournalEditResult.Fail("Количество не может быть отрицательным.");

        if (Math.Abs(value - Math.Round(value)) > 1e-9)
            return JournalEditResult.Fail("Количество штучных изделий — целое число.");

        record.ManualQuantity = (int)Math.Round(value);
        record.UpdatedAt = DateTime.Now;

        return JournalEditResult.Ok(
            $"Количество задано вручную: {record.Quantity} шт. Пересчёты по чертежу его больше не меняют.");
    }

    // ======================= Служебное =======================

    /// <summary>
    /// Перевести геометрию записи на новый слой: создать слой при отсутствии,
    /// перенести объекты, перенести вертикальные участки и убрать опустевший слой.
    /// </summary>
    private int Relayer(MeasurementRecord record, string newLayer, Material material, string newSection)
    {
        var oldLayer = record.LayerName;
        if (string.Equals(oldLayer, newLayer, StringComparison.OrdinalIgnoreCase)) return 0;

        _layers.EnsureLayer(material, newLayer);

        var moved = string.IsNullOrWhiteSpace(oldLayer)
            ? 0
            : _layers.MoveMeasurementGeometry(oldLayer, newLayer, record.IsPiece);

        _verticalRuns.Move(oldLayer, record.Section, newLayer, newSection);

        // Старый слой остался пустым — прибираем. Неудача не критична.
        if (!string.IsNullOrWhiteSpace(oldLayer)) _layers.TryDeleteLayer(oldLayer);

        return moved;
    }

    /// <summary>
    /// Правка со сменой слоя возможна только в том чертеже, где сделан замер:
    /// геометрию неоткрытого DWG плагин не двигает.
    /// </summary>
    private bool IsCurrentDrawing(MeasurementRecord record, out string error)
    {
        var current = _workspace.CurrentDrawingFileName;

        if (string.Equals(record.DrawingFileName, current, StringComparison.OrdinalIgnoreCase))
        {
            error = string.Empty;
            return true;
        }

        error = $"Запись сделана в чертеже «{record.DrawingFileName}».\n" +
                "Открой его, чтобы менять материал или участок: вместе с записью\n" +
                "переезжает и геометрия на другой слой.";
        return false;
    }

    private static string DescribeSection(string? section) =>
        string.IsNullOrWhiteSpace(section) ? "— не задан —" : section;
}
