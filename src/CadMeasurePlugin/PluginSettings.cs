using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;

namespace CadMeasurePlugin;

/// <summary>
/// Настройки плагина, которые имеет смысл править под конкретный отдел.
/// </summary>
public static class PluginSettings
{
    /// <summary>Название продукта — совпадает с Name в PackageContents.xml.</summary>
    public const string ProductName = "PTO Measure Pro";

    /// <summary>
    /// Версия продукта.
    ///
    /// ЕДИНСТВЕННЫЙ источник номера — свойство PtoVersion в файле Version.props
    /// в корне решения. Оттуда оно попадает в AssemblyInformationalVersion обеих
    /// сборок, а сборка проверяет, что то же число стоит в PackageContents.xml
    /// (цель ПроверкаВерсииBundle), и падает при расхождении.
    ///
    /// Здесь номер только читается из атрибута сборки — руками его тут править
    /// не нужно и нельзя.
    /// </summary>
    public static string PluginVersion { get; } = ReadInformationalVersion();

    /// <summary>Версия в формате «PTO Measure Pro 1.0.0» — для заголовков и сообщений.</summary>
    public static string ProductTitle => $"{ProductName} {PluginVersion}";

    private static string ReadInformationalVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational)) return assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        // SourceLink дописывает к версии «+<хеш коммита>» — в UI это лишнее.
        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational.Substring(0, plus) : informational;
    }

    /// <summary>
    /// Сколько единиц чертежа приходится на 1 метр.
    /// Чертежи ОВ/ВК ведутся в миллиметрах, поэтому 1000.
    /// Если чертёж в метрах — поставь 1.
    /// </summary>
    public const double DrawingUnitsPerMeter = 1000.0;

    /// <summary>
    /// Вес замерных линий. В ТЗ указано 1,8 мм; в шкале AutoCAD такого значения нет,
    /// ближайшие — 1.58 мм (LineWeight158) и 2.00 мм (LineWeight200).
    /// Взято 2.00 мм как ближайшее. Если нужна обычная чертёжная толщина 0,18 мм —
    /// поменяй на LineWeight.LineWeight018.
    /// </summary>
    public const LineWeight MeasureLineWeight = LineWeight.LineWeight200;

    /// <summary>
    /// Удалять ли запись журнала, у которой на слое не осталось полилиний,
    /// но заданы вертикальные участки («Подъём» / «Опуск»).
    ///
    /// По умолчанию false — такие записи сохраняются. Вертикальные участки
    /// вводятся с клавиатуры и на чертеже не рисуются: стояк, замеренный
    /// только по высоте, — законный замер, и автоудаление молча стёрло бы
    /// данные, которые нечем восстановить.
    ///
    /// Поставь true, если нужно жёсткое правило «нет полилиний — нет записи».
    /// </summary>
    public const bool DeleteRecordsWithOnlyVerticalRuns = false;

    /// <summary>
    /// Диаметр круга-маркера штучного изделия, мм.
    /// Штучки не рисуются полилиниями: каждое изделие помечается кругом этого
    /// диаметра на слое материала, а количество = число таких кругов на слое.
    /// </summary>
    public const double PieceMarkerDiameterMm = 70.0;

    /// <summary>
    /// Допуск на диаметр маркера, мм. Нужен, потому что круг мог быть
    /// скопирован, слегка отмасштабирован или получен из блока.
    /// </summary>
    public const double PieceMarkerToleranceMm = 0.5;

    // ======================= Подписи объектов замера =======================

    /// <summary>Текстовый стиль подписей. Создаётся плагином, если его нет в чертеже.</summary>
    public const string LabelTextStyleName = "ISOCPEUR";

    /// <summary>Гарнитура подписей (TrueType, входит в поставку AutoCAD).</summary>
    public const string LabelFontTypeface = "ISOCPEUR";

    /// <summary>Файл шрифта — запасной способ задать стиль, если гарнитура не подхватится.</summary>
    public const string LabelFontFileName = "isocpeur.ttf";

    /// <summary>
    /// Создавать ли подпись длины для НОВЫХ замерных полилиний.
    ///
    /// Переключается флажком в палитре и сохраняется между сеансами AutoCAD
    /// (см. <see cref="UserSettingsStore"/>). На уже созданные подписи
    /// не влияет: переключатель управляет только тем, что рисуется дальше.
    /// </summary>
    public static bool ShowPolylineLengthLabels
    {
        get => UserSettingsStore.Current.ShowPolylineLengthLabels;
        set
        {
            if (UserSettingsStore.Current.ShowPolylineLengthLabels == value) return;

            UserSettingsStore.Current.ShowPolylineLengthLabels = value;
            UserSettingsStore.Save();
        }
    }

    /// <summary>Высота подписи длины над полилинией, мм.</summary>
    public const double PolylineLabelHeightMm = 250.0;

    /// <summary>
    /// Подъём подписи над полилинией, мм. Половина высоты текста: подпись
    /// стоит вплотную над линией, но не лежит на ней.
    /// </summary>
    public const double PolylineLabelOffsetMm = 125.0;

    /// <summary>
    /// Высота номера внутри круга-маркера, мм.
    ///
    /// Подобрана под трёхзначный номер в круге ⌀70: в ISOCPEUR цифра занимает
    /// примерно 0,55 высоты по ширине, значит «999» при высоте 25 — это около
    /// 41x25 мм. Половина диагонали такого прямоугольника sqrt(20,5² + 12,5²) ≈ 24 мм
    /// против радиуса 35 мм, то есть запас остаётся даже с отбивкой от контура.
    /// </summary>
    public const double PieceLabelHeightMm = 25.0;

    // ======================= Таблицы журнала на чертеже =======================

    /// <summary>
    /// Слой для таблиц. Отдельный, чтобы таблицы не попадали на замерные слои:
    /// иначе очистка слоя материала или режим «только слои замеров» задевали бы их.
    /// </summary>
    public const string TableLayerName = "PTO_TABLES";

    /// <summary>Цвет слоя таблиц (ACI). 7 — стандартный «по фону» для оформления.</summary>
    public const short TableLayerColorIndex = 7;

    /// <summary>
    /// Текстовый стиль таблицы ведомости. Создаётся плагином при отсутствии;
    /// гарнитура — та же, что у подписей замеров (ISOCPEUR).
    /// </summary>
    public const string TableTextStyleName = "ISOPEUR_1";

    /// <summary>Высота текста в ячейках, мм. Согласована с подписями замеров.</summary>
    public const double TableTextHeightMm = 250.0;

    /// <summary>Высота текста заголовка таблицы, мм.</summary>
    public const double TableTitleTextHeightMm = 350.0;

    /// <summary>Высота строки, мм. Примерно 1,8 высоты текста — с отбивкой.</summary>
    public const double TableRowHeightMm = 450.0;

    /// <summary>Ширина колонки «п/п», мм.</summary>
    public const double TableNumberColumnWidthMm = 1200.0;

    /// <summary>Ширина колонки «Наименование материала», мм — под длинные наименования.</summary>
    public const double TableMaterialColumnWidthMm = 10000.0;

    /// <summary>Ширина колонки «Ед. изм.», мм.</summary>
    public const double TableUnitColumnWidthMm = 1600.0;

    /// <summary>Ширина колонки «Кол-во», мм.</summary>
    public const double TableValueColumnWidthMm = 2600.0;

    /// <summary>Имя палитры.</summary>
    public const string PaletteTitle = "PTO Measure Pro";

    /// <summary>Заголовок сообщений об ошибках.</summary>
    public const string MessageBoxTitle = "PTO Measure Pro";
}
