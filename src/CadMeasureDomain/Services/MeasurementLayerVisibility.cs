namespace CadMeasureDomain.Services;

/// <summary>
/// Что сделать со слоями чертежа: какие включить, какие выключить.
/// Слои, которых нет ни в одном списке, не трогаются вовсе.
/// </summary>
/// <param name="TurnOn">Слои, которые должны стать видимыми.</param>
/// <param name="TurnOff">Слои, которые нужно выключить.</param>
public sealed record LayerVisibilityPlan(IReadOnlyList<string> TurnOn, IReadOnlyList<string> TurnOff)
{
    /// <summary>Ничего менять не нужно.</summary>
    public bool IsEmpty => TurnOn.Count == 0 && TurnOff.Count == 0;
}

/// <summary>
/// Видимость замерных слоёв: изоляция одного и возврат всех.
///
/// Решение о том, что делать, принимается здесь — без AutoCAD, на голых
/// именах слоёв. Плагин только исполняет готовый план. Так правило
/// «трогаем ТОЛЬКО свои слои» проверяется тестами, а не глазами: перепутать
/// проектный слой с замерным в чужом чертеже — это испорченная работа
/// пользователя, а не косметическая ошибка.
///
/// Замерным считается слой, который разбирается обратно в пару
/// «материал + участок» (<see cref="LayerNameFactory.TryResolveLayer"/>) —
/// то же правило, по которому журнал собирается из чертежа.
/// </summary>
public static class MeasurementLayerVisibility
{
    /// <summary>Отобрать среди слоёв чертежа те, что созданы плагином.</summary>
    public static IReadOnlyList<string> SelectMeasurementLayers(
        IEnumerable<string>? layerNames,
        LayerNameFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return (layerNames ?? Enumerable.Empty<string>())
            .Where(name => factory.TryResolveLayer(name, out _, out _))
            .ToList();
    }

    /// <summary>
    /// Оставить видимым только выбранный замерный слой, остальные замерные —
    /// выключить. Проектные слои не участвуют ни в одном списке.
    /// </summary>
    /// <param name="layerNames">Все слои чертежа.</param>
    /// <param name="factory">Реестр имён слоёв — правило распознавания.</param>
    /// <param name="selectedLayer">Слой выбранной записи журнала.</param>
    public static LayerVisibilityPlan PlanIsolation(
        IEnumerable<string>? layerNames,
        LayerNameFactory factory,
        string? selectedLayer)
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (string.IsNullOrWhiteSpace(selectedLayer))
            return new LayerVisibilityPlan(Array.Empty<string>(), Array.Empty<string>());

        var measurement = SelectMeasurementLayers(layerNames, factory);

        var turnOn = measurement
            .Where(name => string.Equals(name, selectedLayer, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var turnOff = measurement
            .Where(name => !string.Equals(name, selectedLayer, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new LayerVisibilityPlan(turnOn, turnOff);
    }

    /// <summary>
    /// Включить все замерные слои. Проектные слои не трогаются: пользователь
    /// мог выключить их сам, и возвращать их — не наше дело.
    /// </summary>
    public static LayerVisibilityPlan PlanShowAll(IEnumerable<string>? layerNames, LayerNameFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return new LayerVisibilityPlan(SelectMeasurementLayers(layerNames, factory), Array.Empty<string>());
    }

    /// <summary>
    /// Включить режим «только замерные слои»: показать все слои плагина
    /// и погасить проектные.
    ///
    /// В список на выключение попадают только те проектные слои, которые
    /// сейчас видимы. Это и есть след режима: при снятии галочки включаются
    /// ровно они, а слои, выключенные пользователем до режима, остаются
    /// выключенными — иначе выход из режима «чинил» бы чертёж по-своему.
    /// </summary>
    /// <param name="layerNames">Все слои чертежа.</param>
    /// <param name="alreadyOff">Слои, выключенные на момент включения режима.</param>
    /// <param name="factory">Правило распознавания замерных слоёв.</param>
    public static LayerVisibilityPlan PlanEnableOnlyMeasurement(
        IEnumerable<string>? layerNames,
        IEnumerable<string>? alreadyOff,
        LayerNameFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var all = (layerNames ?? Enumerable.Empty<string>()).ToList();
        var off = new HashSet<string>(alreadyOff ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        var measurement = SelectMeasurementLayers(all, factory);
        var measurementSet = new HashSet<string>(measurement, StringComparer.OrdinalIgnoreCase);

        return new LayerVisibilityPlan(
            measurement,
            all.Where(name => !measurementSet.Contains(name) && !off.Contains(name)).ToList());
    }

    /// <summary>
    /// Включить режим «только слой текущего замера»: погасить остальные
    /// замерные слои. Проектные слои не участвуют — за них отвечает
    /// другая галочка, и режимы не должны мешать друг другу.
    /// </summary>
    /// <param name="layerNames">Все слои чертежа.</param>
    /// <param name="alreadyOff">Слои, выключенные на момент включения режима.</param>
    /// <param name="factory">Правило распознавания замерных слоёв.</param>
    /// <param name="currentLayer">Слой текущего замера.</param>
    public static LayerVisibilityPlan PlanEnableCurrentLayerOnly(
        IEnumerable<string>? layerNames,
        IEnumerable<string>? alreadyOff,
        LayerNameFactory factory,
        string? currentLayer)
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (string.IsNullOrWhiteSpace(currentLayer))
            return new LayerVisibilityPlan(Array.Empty<string>(), Array.Empty<string>());

        var off = new HashSet<string>(alreadyOff ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var measurement = SelectMeasurementLayers(layerNames, factory);

        var turnOn = measurement
            .Where(name => string.Equals(name, currentLayer, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var turnOff = measurement
            .Where(name => !string.Equals(name, currentLayer, StringComparison.OrdinalIgnoreCase))
            .Where(name => !off.Contains(name))
            .ToList();

        return new LayerVisibilityPlan(turnOn, turnOff);
    }

    /// <summary>
    /// Выключить режим: вернуть ровно те слои, которые он погасил.
    /// Ничего другого не трогаем — состояние, заданное второй галочкой
    /// или самим пользователем, сохраняется.
    /// </summary>
    public static LayerVisibilityPlan PlanDisableMode(IEnumerable<string>? hiddenByMode) =>
        new((hiddenByMode ?? Enumerable.Empty<string>()).ToList(), Array.Empty<string>());
}
