namespace CadMeasurePlugin.Services;

/// <summary>Накопленные вертикальные участки по одному сочетанию «слой + участок».</summary>
public sealed class VerticalRunTotals
{
    /// <summary>Суммарная длина подъёмов, мм.</summary>
    public double UpMm { get; set; }

    /// <summary>Суммарная длина опусков, мм.</summary>
    public double DownMm { get; set; }

    /// <summary>Сколько раз вводились вертикальные участки.</summary>
    public int EntryCount { get; set; }

    public double TotalMm => UpMm + DownMm;

    public double TotalM => TotalMm / 1000.0;
}

/// <summary>
/// Хранилище вертикальных участков («Подъём» / «Опуск»).
///
/// Вертикальные участки не рисуются на чертеже — их длина вводится с клавиатуры
/// в миллиметрах и копится отдельно от геометрии, по ключу «слой + участок».
/// И подъём, и опуск добавляют длину: труба расходуется в обе стороны одинаково.
///
/// Хранение по ключу делает кнопку «Записать» идемпотентной: сколько раз её ни
/// нажимай, вертикальная составляющая одна и та же, а не складывается повторно.
/// </summary>
public sealed class VerticalRunStore
{
    private readonly Dictionary<string, VerticalRunTotals> _totals = new(StringComparer.Ordinal);

    private static string BuildKey(string? layerName, string? section) =>
        string.Join('\u0001',
            (layerName ?? string.Empty).Trim().ToUpperInvariant(),
            (section ?? string.Empty).Trim().ToUpperInvariant());

    /// <summary>Добавить вертикальный участок.</summary>
    /// <param name="layerName">Слой материала.</param>
    /// <param name="section">Участок.</param>
    /// <param name="lengthMm">Длина участка в миллиметрах (положительная).</param>
    /// <param name="isRise">true — подъём, false — опуск.</param>
    public VerticalRunTotals AddRun(string layerName, string section, double lengthMm, bool isRise)
    {
        if (lengthMm <= 0) throw new ArgumentOutOfRangeException(nameof(lengthMm), "Длина участка должна быть больше нуля.");

        var key = BuildKey(layerName, section);
        if (!_totals.TryGetValue(key, out var totals))
        {
            totals = new VerticalRunTotals();
            _totals[key] = totals;
        }

        if (isRise) totals.UpMm += lengthMm;
        else totals.DownMm += lengthMm;

        totals.EntryCount++;
        return totals;
    }

    /// <summary>Накопленные суммы для слоя и участка (никогда не null).</summary>
    public VerticalRunTotals GetTotals(string layerName, string section) =>
        _totals.TryGetValue(BuildKey(layerName, section), out var totals) ? totals : new VerticalRunTotals();

    /// <summary>Суммарная длина вертикальных участков в метрах.</summary>
    public double GetVerticalLengthM(string layerName, string section) =>
        GetTotals(layerName, section).TotalM;

    /// <summary>Обнулить вертикальные участки для слоя и участка (ошибочный ввод).</summary>
    public void Reset(string layerName, string section) => _totals.Remove(BuildKey(layerName, section));

    /// <summary>
    /// Перенести накопленные вертикальные участки на другой слой и участок.
    ///
    /// Нужно при правке материала или участка в таблице журнала: ключ хранилища —
    /// «слой + участок», и без переноса подъёмы с опусками остались бы висеть
    /// за старым ключом, а из длины записи молча пропали бы.
    ///
    /// Если по новому ключу что-то уже накоплено, суммы складываются: обе части
    /// относятся к одному и тому же материалу на одном участке.
    /// </summary>
    public void Move(string fromLayer, string fromSection, string toLayer, string toSection)
    {
        var fromKey = BuildKey(fromLayer, fromSection);
        var toKey = BuildKey(toLayer, toSection);

        if (string.Equals(fromKey, toKey, StringComparison.Ordinal)) return;
        if (!_totals.TryGetValue(fromKey, out var source)) return;

        _totals.Remove(fromKey);

        if (_totals.TryGetValue(toKey, out var target))
        {
            target.UpMm += source.UpMm;
            target.DownMm += source.DownMm;
            target.EntryCount += source.EntryCount;
            return;
        }

        _totals[toKey] = source;
    }
}
