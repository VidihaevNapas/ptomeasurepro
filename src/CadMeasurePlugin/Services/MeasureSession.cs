using CadMeasureDomain.Models;
using CadMeasureDomain.Services;
using CadMeasureDomain.Tools;

namespace CadMeasurePlugin.Services;

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
