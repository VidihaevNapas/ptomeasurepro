using CadMeasureDomain.Models;

namespace CadMeasureDomain.Tools;

/// <summary>
/// Всё, что инструментам нужно от AutoCAD.
/// Реализуется в проекте плагина — благодаря этому домен (модели, журнал,
/// Excel) не зависит от AutoCAD API и остаётся тестируемым.
/// </summary>
public interface ICadWorkspace
{
    /// <summary>
    /// Создать слой материала, если его ещё нет (цвет + вес линии),
    /// и сделать его текущим. Возвращает имя слоя.
    /// </summary>
    string EnsureLayer(Material material, string layerName);
}

/// <summary>
/// Общий контракт инструмента замера.
///
/// Инструмент не выбирается пользователем — он определяется классом материала
/// (<see cref="Material.Class"/>). Палитра просто спрашивает у сессии
/// инструмент под выбранный материал.
///
/// Инструмент отвечает за подготовку слоя. Сам журнал заполняется не им, а
/// сканированием чертежа (MeasurementJournalService): источник истины —
/// геометрия, а не последовательность нажатий.
///
/// Методы принимают участок: он входит в имя слоя, потому что журнал ведётся
/// автоматически и определить участок можно только по слою.
/// </summary>
public interface IMeasureTool
{
    /// <summary>Название инструмента для сообщений, например «Замер трубопровода».</summary>
    string ToolName { get; }

    /// <summary>Класс материалов, с которыми работает инструмент.</summary>
    string MaterialClass { get; }

    /// <summary>Выбранный материал, либо null.</summary>
    Material? CurrentMaterial { get; }

    /// <summary>Назначить инструменту материал, выбранный в окне выбора.</summary>
    void SelectMaterial(Material material);

    /// <summary>
    /// Снять текущий материал. Нужно, когда позицию удалили из реестра:
    /// иначе инструмент продолжил бы работать по материалу, которого больше нет.
    /// </summary>
    void ClearMaterial();

    /// <summary>Имя слоя выбранного материала на участке. Слой не создаётся.</summary>
    string GetLayerName(string? section);

    /// <summary>
    /// Создать слой материала при первом использовании и сделать его текущим.
    /// Возвращает имя слоя.
    /// </summary>
    string PrepareLayerOrSelection(string? section);
}
