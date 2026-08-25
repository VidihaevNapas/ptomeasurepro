using System.Globalization;
using CadMeasureDomain.Models;

namespace CadMeasureDomain.Services;

/// <summary>Поле позиции спецификации, доступное для ручной правки.</summary>
public enum SpecificationField
{
    Name,
    Mark,
    EquipmentCode,
    Manufacturer,
    Unit,
    Quantity
}

/// <summary>
/// Ручная правка полей спецификации в таблице журнала.
///
/// Нужна там, где импорт не смог прочитать данные: в присланном файле
/// съехали колонки, часть ячеек пустая, единица написана так, что её
/// не распознать. Автоматически такое не исправить, а работать нужно —
/// поэтому пустые поля разрешено дописывать руками.
///
/// Правка идёт сразу в двух местах: в записи журнала (её видит таблица
/// и ведомость) и в позиции спецификации (её видит свод в Excel). Иначе
/// исправленное наименование показывалось бы в палитре, а в выгрузку уходило
/// бы прежнее.
///
/// Тронутая строка помечается флагом <see cref="MeasurementRecord.SpecificationEditedManually"/>:
/// пользователь должен видеть, что эти данные не из файла, а введены им.
/// </summary>
public static class SpecificationManualEdit
{
    /// <summary>Русское название поля — для сообщения в журнале событий.</summary>
    public static string ToRussian(SpecificationField field) => field switch
    {
        SpecificationField.Name => "наименование",
        SpecificationField.Mark => "марка",
        SpecificationField.EquipmentCode => "код оборудования",
        SpecificationField.Manufacturer => "изготовитель",
        SpecificationField.Unit => "ед. изм.",
        SpecificationField.Quantity => "кол-во",
        _ => field.ToString()
    };

    /// <summary>
    /// Поле пустое, то есть импорт его не прочитал. Только такие поля
    /// и разрешено править: перебивать данные, которые в файле есть,
    /// значило бы тихо расходиться с проектом.
    /// </summary>
    public static bool IsUnread(MeasurementRecord record, SpecificationField field)
    {
        ArgumentNullException.ThrowIfNull(record);

        return field switch
        {
            SpecificationField.Name => string.IsNullOrWhiteSpace(record.MaterialName),
            SpecificationField.Mark => string.IsNullOrWhiteSpace(record.Mark),
            SpecificationField.EquipmentCode => string.IsNullOrWhiteSpace(record.EquipmentCode),
            SpecificationField.Manufacturer => string.IsNullOrWhiteSpace(record.Manufacturer),
            SpecificationField.Unit => string.IsNullOrWhiteSpace(record.Unit),
            SpecificationField.Quantity => record.SpecificationQuantity is null or 0,
            _ => false
        };
    }

    /// <summary>
    /// Записать значение в поле записи и в связанную позицию спецификации.
    /// </summary>
    /// <param name="record">Строка журнала.</param>
    /// <param name="specification">Загруженная спецификация, если есть.</param>
    /// <param name="field">Поле.</param>
    /// <param name="value">Новое значение; для количества принимается «12,5» и «12.5».</param>
    /// <returns>Строка для журнала событий, либо null, если значение не изменилось.</returns>
    public static string? Apply(
        MeasurementRecord record,
        Specification? specification,
        SpecificationField field,
        string? value)
    {
        ArgumentNullException.ThrowIfNull(record);

        var text = (value ?? string.Empty).Trim();
        var item = specification?.Items.FirstOrDefault(i => i.Number == record.SpecificationItemId);

        switch (field)
        {
            case SpecificationField.Name:
                if (text.Length == 0 || text == record.MaterialName) return null;
                record.MaterialName = text;
                if (item is not null) item.Name = text;
                break;

            case SpecificationField.Mark:
                if (text == record.Mark) return null;
                record.Mark = text;
                if (item is not null) item.Mark = text;
                break;

            case SpecificationField.EquipmentCode:
                if (text == record.EquipmentCode) return null;
                record.EquipmentCode = text;
                if (item is not null) item.EquipmentCode = text;
                break;

            case SpecificationField.Manufacturer:
                if (text == record.Manufacturer) return null;
                record.Manufacturer = text;
                if (item is not null) item.Manufacturer = text;
                break;

            case SpecificationField.Unit:
                if (text.Length == 0 || text == record.Unit) return null;
                record.Unit = text;
                if (item is not null) item.Unit = text;
                break;

            case SpecificationField.Quantity:
            {
                if (!FlexibleNullableDoubleConverter.TryParse(text, out var quantity))
                    throw new InvalidOperationException(
                        $"«{text}» не похоже на количество. Введи число, например 12,5.");

                if (record.SpecificationQuantity is not null &&
                    Math.Abs(record.SpecificationQuantity.Value - quantity) < 1e-9)
                    return null;

                // Расхождение пересчитывается само: оно вычисляется
                // от SpecificationQuantity и замеренного количества.
                record.SpecificationQuantity = quantity;
                if (item is not null) item.Quantity = quantity;
                break;
            }

            default:
                return null;
        }

        record.SpecificationEditedManually = true;
        record.UpdatedAt = DateTime.Now;

        var number = record.SpecificationItemId?.ToString(CultureInfo.InvariantCulture) ?? "—";
        return $"Позиция спецификации п/п {number} изменена вручную: {ToRussian(field)} = {text}";
    }
}
