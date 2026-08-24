using CadMeasureDomain.Models;

namespace CadMeasureDomain.Services;

/// <summary>
/// Выдаёт ACI-индекс цвета (1..255) для слоя материала.
///
/// Трубы   — «левый» диапазон индексов 1..128;
/// воздуховоды — «правый» диапазон 129..255;
/// штучные изделия — тоже левый диапазон, но со смещением, чтобы визуально
/// не сливаться с трубами.
///
/// Цвет детерминированный (зависит только от наименования материала),
/// поэтому один и тот же материал в разных сеансах получает один и тот же цвет.
/// Сервис намеренно не зависит от AutoCAD: возвращает голый индекс,
/// а Color создаёт уже плагин.
/// </summary>
public static class LayerColorService
{
    private const int PipeRangeStart = 1;
    private const int PipeRangeEnd = 128;
    private const int DuctRangeStart = 129;
    private const int DuctRangeEnd = 255;

    // Индекс 7 в AutoCAD — чёрный/белый «по фону»: такие линии плохо видно
    // среди основной графики, поэтому для замеров его пропускаем.
    private const int ReservedIndex = 7;
    private const int ReservedReplacement = 6;

    public static short GetColorIndex(Material material)
    {
        ArgumentNullException.ThrowIfNull(material);
        return GetColorIndex(material.Class, material.Key);
    }

    public static short GetColorIndex(string? materialClass, string materialKey)
    {
        var hash = MaterialFormatter.StableHash(materialKey);

        // Трубы и воздуховоды делят диапазоны так, как задано в ТЗ.
        // Кабели и штучные изделия живут в левом диапазоне со смещением,
        // чтобы визуально не сливаться с трубами.
        int index = materialClass switch
        {
            MaterialClasses.Duct => DuctRangeStart + hash % (DuctRangeEnd - DuctRangeStart + 1),
            MaterialClasses.Cable => PipeRangeStart + (hash + 32) % (PipeRangeEnd - PipeRangeStart + 1),
            MaterialClasses.Piece => PipeRangeStart + (hash + 64) % (PipeRangeEnd - PipeRangeStart + 1),
            _ => PipeRangeStart + hash % (PipeRangeEnd - PipeRangeStart + 1)
        };

        if (index == ReservedIndex) index = ReservedReplacement;
        return (short)index;
    }
}
