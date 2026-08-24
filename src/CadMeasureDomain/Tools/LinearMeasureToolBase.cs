using CadMeasureDomain.Models;
using CadMeasureDomain.Services;

namespace CadMeasureDomain.Tools;

/// <summary>
/// Общая логика линейных инструментов (трубы, воздуховоды, кабели):
/// имя слоя по материалу и участку, создание слоя при первом замере.
/// Наследники отличаются только классом материала и названием.
/// </summary>
public abstract class LinearMeasureToolBase : IMeasureTool
{
    private readonly ICadWorkspace _workspace;
    private readonly LayerNameFactory _layerNames;

    protected LinearMeasureToolBase(ICadWorkspace workspace, LayerNameFactory layerNames)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _layerNames = layerNames ?? throw new ArgumentNullException(nameof(layerNames));
    }

    public abstract string ToolName { get; }

    public abstract string MaterialClass { get; }

    public Material? CurrentMaterial { get; private set; }

    public void SelectMaterial(Material material)
    {
        ArgumentNullException.ThrowIfNull(material);

        if (!string.Equals(material.Class, MaterialClass, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Инструмент «{ToolName}» не работает с материалами класса «{material.Class}».");

        CurrentMaterial = material;
    }

    public void ClearMaterial() => CurrentMaterial = null;

    public string GetLayerName(string? section) => _layerNames.GetLayerName(RequireMaterial(), section);

    public string PrepareLayerOrSelection(string? section)
    {
        var material = RequireMaterial();
        return _workspace.EnsureLayer(material, _layerNames.GetLayerName(material, section));
    }

    private Material RequireMaterial() =>
        CurrentMaterial ?? throw new InvalidOperationException(
            $"Для инструмента «{ToolName}» не выбран материал.");
}

/// <summary>Замер трубопроводов.</summary>
public sealed class PipeMeasureTool : LinearMeasureToolBase
{
    public PipeMeasureTool(ICadWorkspace workspace, LayerNameFactory layerNames)
        : base(workspace, layerNames) { }

    public override string ToolName => "Замер трубопровода";

    public override string MaterialClass => MaterialClasses.Pipe;
}

/// <summary>Замер воздуховодов.</summary>
public sealed class DuctMeasureTool : LinearMeasureToolBase
{
    public DuctMeasureTool(ICadWorkspace workspace, LayerNameFactory layerNames)
        : base(workspace, layerNames) { }

    public override string ToolName => "Замер воздуховода";

    public override string MaterialClass => MaterialClasses.Duct;
}

/// <summary>
/// Замер кабельных трасс. От труб отличается только классом материала:
/// длина так же считается по полилиниям, вертикальные участки (спуски
/// к оборудованию, подъёмы в лоток) вводятся теми же кнопками.
/// </summary>
public sealed class CableMeasureTool : LinearMeasureToolBase
{
    public CableMeasureTool(ICadWorkspace workspace, LayerNameFactory layerNames)
        : base(workspace, layerNames) { }

    public override string ToolName => "Замер кабельной трассы";

    public override string MaterialClass => MaterialClasses.Cable;
}
