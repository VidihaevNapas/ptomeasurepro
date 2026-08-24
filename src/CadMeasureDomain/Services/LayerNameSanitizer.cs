using System.Text;

namespace CadMeasureDomain.Services;

/// <summary>
/// Приведение произвольной строки к символам, допустимым в имени слоя AutoCAD.
/// AutoCAD запрещает: &lt; &gt; / \ " : ; ? * | , = ` и управляющие символы.
/// </summary>
public static class LayerNameSanitizer
{
    private static readonly char[] Forbidden = { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', ',', '=', '`' };

    /// <summary>Максимальная длина имени слоя в AutoCAD — 255 символов.</summary>
    public const int MaxLayerNameLength = 255;

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsControl(ch)) continue;
            sb.Append(Array.IndexOf(Forbidden, ch) >= 0 ? '_' : ch);
        }

        var result = sb.ToString().Trim();
        if (result.Length > MaxLayerNameLength)
            result = result.Substring(0, MaxLayerNameLength);

        return result;
    }

    /// <summary>
    /// Пригодно ли имя для слоя AutoCAD.
    /// Проверка нужна как последний рубеж: имя слоя вычисляется из данных
    /// реестра материалов, которые правит человек, и туда может попасть что угодно.
    /// </summary>
    public static bool IsValidLayerName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length > MaxLayerNameLength) return false;

        // Ведущие и замыкающие пробелы AutoCAD отбрасывает молча,
        // из-за чего два разных материала могут получить один слой.
        if (name != name.Trim()) return false;

        foreach (var ch in name)
        {
            if (char.IsControl(ch)) return false;
            if (Array.IndexOf(Forbidden, ch) >= 0) return false;
        }

        return true;
    }
}
