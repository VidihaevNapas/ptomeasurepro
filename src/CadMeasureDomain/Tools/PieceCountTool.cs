using CadMeasureDomain.Models;
using CadMeasureDomain.Services;

namespace CadMeasureDomain.Tools;

/// <summary>
/// Виды штучных изделий. Используются для группировки в реестре и Excel;
/// на способ замера не влияют — считается всегда количество.
/// </summary>
public static class PieceKinds
{
    public const string PipeFitting = "Фасонные изделия трубопроводов";
    public const string Valve = "Запорная арматура";
    public const string Flange = "Фланцы и заглушки";
    public const string DuctFitting = "Фасонные изделия вентиляции";
    public const string Equipment = "Оборудование";

    public static readonly string[] All = { PipeFitting, Valve, Flange, DuctFitting, Equipment };
}

/// <summary>
/// Подсчёт штучных изделий: фасонные изделия, запорная арматура, фланцы
/// и заглушки, фасонные изделия вентиляции, оборудование.
///
/// От линейных инструментов отличается только тем, что на слое считается
/// количество кругов-маркеров, а не длина полилиний. Слой, цвет и участок —
/// общие, через те же сервисы.
/// </summary>
public sealed class PieceCountTool : IMeasureTool
{
    private readonly ICadWorkspace _workspace;
    private readonly LayerNameFactory _layerNames;

    public PieceCountTool(ICadWorkspace workspace, LayerNameFactory layerNames)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _layerNames = layerNames ?? throw new ArgumentNullException(nameof(layerNames));
    }

    public string ToolName => "Подсчёт штучных изделий";

    public string MaterialClass => MaterialClasses.Piece;

    public Material? CurrentMaterial { get; private set; }

    public void SelectMaterial(Material material)
    {
        ArgumentNullException.ThrowIfNull(material);

        if (!string.Equals(material.Class, MaterialClass, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Инструмент «{ToolName}» работает только с материалами класса «{MaterialClasses.Piece}».");

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
