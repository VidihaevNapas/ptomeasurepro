using CadMeasureDomain.Models;
using CadMeasureDomain.Services;
using CadMeasureDomain.Tools;

namespace CadMeasurePlugin.Services;

/// <summary>Что произошло при загрузке или перезагрузке спецификации.</summary>
/// <param name="Specification">Принятая спецификация.</param>
/// <param name="Previous">Прежняя спецификация сессии, либо null при первой загрузке.</param>
/// <param name="MaterialsCreated">Сколько материалов заведено в реестре.</param>
/// <param name="MaterialsSkipped">Сколько позиций пропущено из-за нераспознанной единицы.</param>
/// <param name="RecordsRebound">Сколько записей журнала привязано к новым позициям.</param>
/// <param name="RecordsUnbound">Сколько записей потеряло привязку: позиции нет в новом файле.</param>
/// <param name="Log">Строки для командной строки AutoCAD.</param>
public sealed record SpecificationLoadResult(
    Specification Specification,
    Specification? Previous,
    int MaterialsCreated,
    int MaterialsSkipped,
    int RecordsRebound,
    int RecordsUnbound,
    IReadOnlyList<string> Log)
{
    /// <summary>Загрузка поверх уже действующей спецификации.</summary>
    public bool IsReload => Previous is not null;
}

/// <summary>
/// Состояние сессии замеров: реестр материалов, журнал, инструменты, слои.
///
/// Живёт от NETLOAD до закрытия AutoCAD и общий для всех открытых чертежей —
/// именно поэтому журнал может накапливать замеры по нескольким DWG,
/// а каждая запись помнит свой чертёж.
///
/// Инструмент замера пользователем не выбирается: он определяется классом
/// выбранного материала (труба / воздуховод / кабель / штучное изделие).
/// </summary>
public sealed class MeasureSession
{
    private static readonly Lazy<MeasureSession> Lazy = new(() => new MeasureSession());

    /// <summary>Единственный экземпляр сессии.</summary>
    public static MeasureSession Instance => Lazy.Value;

    private MeasureSession()
    {
        LayerNames = new LayerNameFactory();
        TextStyles = new TextStyleService();
        Labels = new LabelService(TextStyles);
        LayerService = new LayerService(LayerNames, Labels);
        Workspace = new AcadWorkspace(LayerService);
        VerticalRuns = new VerticalRunStore();
        Materials = new MaterialRepository();
        Journal = new MeasurementJournal();
        LayerVisibility = new LayerVisibilityService();
        ExcelExport = new ExcelExportService();

        JournalService = new MeasurementJournalService(
            Journal, Workspace, LayerService, LayerNames, VerticalRuns, Materials);

        MaterialDeletion = new MaterialDeletionService(
            Materials, JournalService, Journal, LayerService, LayerNames, Workspace);

        JournalEdit = new JournalEditService(
            Journal, Materials, LayerService, LayerNames, VerticalRuns, Workspace);

        Tables = new AcadMeasurementTableService(Journal, Workspace, TextStyles);

        PipeTool = new PipeMeasureTool(Workspace, LayerNames);
        DuctTool = new DuctMeasureTool(Workspace, LayerNames);
        CableTool = new CableMeasureTool(Workspace, LayerNames);
        PieceTool = new PieceCountTool(Workspace, LayerNames);

        ActiveTool = PipeTool;

        // Основы имён слоёв всегда должны соответствовать текущему реестру:
        // по ним автосканирование разбирает слои чертежа обратно в материалы.
        Materials.Changed += (_, _) => LayerNames.SyncWithRegistry(Materials.Materials);
    }

    public AcadWorkspace Workspace { get; }
    public VerticalRunStore VerticalRuns { get; }
    public LayerNameFactory LayerNames { get; }

    /// <summary>Создание, активация и очистка слоёв замеров.</summary>
    public LayerService LayerService { get; }

    /// <summary>Поиск и создание текстовых стилей плагина.</summary>
    public TextStyleService TextStyles { get; }

    /// <summary>Подписи: длина над полилинией, номер внутри круга-маркера.</summary>
    public LabelService Labels { get; }

    public MaterialRepository Materials { get; }
    public MeasurementJournal Journal { get; }

    /// <summary>Ведение журнала по фактической геометрии чертежа.</summary>
    public MeasurementJournalService JournalService { get; }

    /// <summary>Каскадное удаление материала: геометрия → журнал → реестр → слои.</summary>
    public MaterialDeletionService MaterialDeletion { get; }

    /// <summary>Правка журнала прямо в таблице: материал, участок, длина, количество.</summary>
    public JournalEditService JournalEdit { get; }

    /// <summary>Таблицы журнала на чертеже — производное отображение журнала.</summary>
    public AcadMeasurementTableService Tables { get; }

    public LayerVisibilityService LayerVisibility { get; }
    public ExcelExportService ExcelExport { get; }

    public PipeMeasureTool PipeTool { get; }
    public DuctMeasureTool DuctTool { get; }
    public CableMeasureTool CableTool { get; }
    public PieceCountTool PieceTool { get; }

    /// <summary>Все инструменты — для операций «по всем сразу».</summary>
    public IReadOnlyList<IMeasureTool> AllTools => new IMeasureTool[] { PipeTool, DuctTool, CableTool, PieceTool };

    /// <summary>
    /// Инструмент, соответствующий выбранному материалу.
    /// Меняется автоматически при выборе материала — отдельного переключателя
    /// инструментов в палитре нет.
    /// </summary>
    public IMeasureTool ActiveTool { get; private set; }

    /// <summary>Участок (зона / часть проекта). Входит в имя слоя.</summary>
    public string Section { get; set; } = string.Empty;

    /// <summary>Папка, в которой лежит dll плагина (…\PTOMeasurePro.bundle\Contents).</summary>
    public static string PluginDirectory => PluginPaths.PluginDirectory;

    // ======================= Первоначальная спецификация =======================

    /// <summary>
    /// Загруженная спецификация проекта, либо null. Живёт столько же, сколько
    /// сессия, поэтому переход между чертежами её не сбрасывает — именно так
    /// и набираются столбцы «Подсчёт по &lt;файл&gt;» по нескольким DWG.
    /// </summary>
    public Specification? Specification { get; private set; }

    /// <summary>Спецификация загружена.</summary>
    public bool HasSpecification => Specification is not null;

    /// <summary>
    /// Какие столбцы спецификации показывать в таблице журнала: заголовок → показывать.
    ///
    /// Живёт в сессии, а не в настройках пользователя: это про текущую работу
    /// («сейчас мешает — убрал»), а не про постоянное предпочтение. Палитру
    /// можно закрыть и открыть заново — выбор сохранится до закрытия AutoCAD.
    /// </summary>
    public Dictionary<string, bool> SpecificationColumnVisibility { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Принять импортированную спецификацию: завести недостающие материалы,
    /// связать с ней журнал и запомнить её на сессию.
    ///
    /// Записи журнала при этом не создаются: какие позиции брать в работу,
    /// решает пользователь в окне импорта.
    /// </summary>
    /// <returns>Что произошло — для вывода в командную строку AutoCAD.</returns>
    public SpecificationLoadResult LoadSpecification(Specification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);

        var previous = Specification;
        var log = new List<string>();

        // 1. Материалы. Позиции, которых нет в реестре, заводятся сразу —
        //    иначе замерять спецификацию было бы нечем.
        var sync = SpecificationRegistrySync.EnsureMaterials(Materials, specification);
        log.AddRange(sync.Log);

        // 2. Журнал. При перезагрузке привязки пересобираются: старые номера
        //    позиций указывали бы не на те строки нового файла.
        var (rebound, unbound) = Journal.RebindToSpecification(specification);

        // 3. Записи, замеренные до импорта, тоже связываем — иначе их подсчёт
        //    не попал бы в свод.
        foreach (var record in Journal.Records)
        {
            if (record.IsFromSpecification) continue;

            var item = specification.FindByName(record.MaterialName);
            if (item is null) continue;

            MeasurementJournal.BindToSpecification(record, item, specification.FileName);
            rebound++;
        }

        Specification = specification;
        JournalService.Specification = specification;

        if (previous is not null)
            log.Add($"Спецификация перезапущена: {previous.FileName} → {specification.FileName}");

        return new SpecificationLoadResult(
            specification,
            previous,
            sync.Created.Count,
            sync.Skipped.Count,
            rebound,
            unbound,
            log);
    }

    /// <summary>
    /// Материал реестра, соответствующий позиции спецификации, либо null,
    /// если наименования не совпали. Пока материала нет, замерять позицию
    /// нечем: слой строится по материалу реестра.
    /// </summary>
    public Material? FindMaterialFor(SpecificationItem item) =>
        item is null ? null : Materials.FindByName(item.Name);

    /// <summary>
    /// Перенести позиции спецификации в журнал текущего чертежа.
    /// Возвращает количество созданных и обновлённых записей.
    /// </summary>
    public int AddSpecificationItemsToJournal(IEnumerable<SpecificationItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (Specification is null)
            throw new InvalidOperationException("Спецификация не загружена.");

        var drawing = Workspace.CurrentDrawingFileName;
        var added = 0;

        foreach (var item in items)
        {
            Journal.AddFromSpecification(item, Specification.FileName, drawing, FindMaterialFor(item));
            added++;
        }

        return added;
    }

    /// <summary>Загрузить реестр материалов. Вызывается один раз в начале сессии.</summary>
    public void EnsureMaterialsLoaded()
    {
        if (Materials.IsLoaded) return;
        Materials.Load(BuildRegistryLocations());
    }

    /// <summary>Перезагрузить реестр материалов с диска.</summary>
    public void ReloadMaterials() => Materials.Load(BuildRegistryLocations());

    /// <summary>
    /// Где искать реестр: рядом с чертежом → в данных пользователя →
    /// развернуть из шаблона в bundle.
    ///
    /// Внутрь bundle плагин не пишет: при обновлении папка заменяется целиком.
    /// </summary>
    private static MaterialRegistryLocations BuildRegistryLocations() => new()
    {
        DrawingDirectory = AcadWorkspace.GetCurrentDrawingDirectory(),
        UserDataDirectory = PluginPaths.UserDataDirectory,
        TemplatePath = PluginPaths.TemplateRegistryPath
    };

    /// <summary>Инструмент под класс материала.</summary>
    public IMeasureTool GetToolFor(string? materialClass) => materialClass switch
    {
        MaterialClasses.Duct => DuctTool,
        MaterialClasses.Cable => CableTool,
        MaterialClasses.Piece => PieceTool,
        _ => PipeTool
    };

    /// <summary>Зафиксировать в журнале имя активного чертежа.</summary>
    public void SyncCurrentDrawing()
    {
        Journal.CurrentDrawingFileName = Workspace.CurrentDrawingFileName;
    }

    /// <summary>
    /// Выбрать материал: инструмент подбирается по классу материала,
    /// слой находится или создаётся и сразу становится текущим.
    /// Возвращает имя слоя.
    /// </summary>
    public string SelectMaterialAndActivateLayer(Material material)
    {
        ArgumentNullException.ThrowIfNull(material);

        ActiveTool = GetToolFor(material.Class);
        ActiveTool.SelectMaterial(material);

        return LayerService.EnsureLayerForSection(material, Section);
    }

    /// <summary>Пересобрать журнал по фактической геометрии активного чертежа.</summary>
    public JournalScanResult ScanDrawing() => JournalService.ScanDrawing();

    /// <summary>
    /// Очистить журнал текущей сессии.
    ///
    /// Чертёж не трогается: полилинии, круги, подписи, слои, реестр материалов
    /// и выгруженные Excel-файлы остаются на месте. Автоведение журнала при этом
    /// не отключается, поэтому при ближайшем пересчёте записи соберутся заново
    /// по существующей геометрии.
    ///
    /// Вертикальные участки сохраняются: они привязаны к слоям, а слои никуда
    /// не делись — иначе после очистки длины стояков молча пропали бы.
    /// </summary>
    public void ClearJournal() => Journal.Clear();
}
