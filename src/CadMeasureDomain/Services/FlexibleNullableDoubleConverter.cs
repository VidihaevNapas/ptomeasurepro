using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CadMeasureDomain.Services;

/// <summary>
/// Конвертер дробных характеристик для materials.json.
///
/// Пишет всегда инвариантно («3.5») — этого требует формат JSON.
/// Читает терпимо: число 3.5, строку "3.5" и строку "3,5". Последнее нужно
/// потому, что файл правят руками в русской локали, и запятая туда попадает
/// постоянно; без этого позиция молча теряла бы характеристику.
/// </summary>
public sealed class FlexibleNullableDoubleConverter : JsonConverter<double?>
{
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.Number:
                return reader.GetDouble();

            case JsonTokenType.String:
            {
                var text = reader.GetString();
                return TryParse(text, out var value) ? value : null;
            }

            default:
                throw new JsonException(
                    $"Ожидалось число для дробной характеристики, получено {reader.TokenType}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteNumberValue(value.Value);
    }

    /// <summary>Разбор числа из текста: принимаем и точку, и запятую.</summary>
    public static bool TryParse(string? text, out double value)
    {
        value = 0;

        var normalized = (text ?? string.Empty).Trim().Replace(',', '.');
        if (normalized.Length == 0) return false;

        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
