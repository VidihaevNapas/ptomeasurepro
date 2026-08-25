using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using CadMeasureDomain.Services;

namespace CadMeasureDomain.Models;

/// <summary>
/// Одна строка журнала замеров: сочетание «материал + участок + DWG».
///
/// Значения приходят из сканирования чертежа, но длину и количество можно
/// переопределить вручную в таблице. Ручное значение имеет приоритет и
/// переживает пересчёты — иначе правка жила бы до ближайшего скана.
/// Такая строка помечается флагом <see cref="HasManualValue"/>, чтобы было
/// видно: она больше не соответствует геометрии автоматически.
///
/// Реализует INotifyPropertyChanged, чтобы таблица журнала в палитре
/// обновлялась сразу после пересчёта, без пересборки коллекции.
/// </summary>
public sealed class MeasurementRecord : INotifyPropertyChanged
{
    private double _horizontalLengthM;
    private double _verticalLengthM;
    private double _areaPerMeterM2;
    private int? _specificationItemId;
    private double? _specificationQuantity;
    private string _specificationFileName = string.Empty;
    private string _mark = string.Empty;
    private string _equipmentCode = string.Empty;
    private string _manufacturer = string.Empty;
    private bool _materialMissing;
    private bool _specificationEditedManually;
    private double? _manualLengthM;
    private int _polylineCount;
    private int _scannedQuantity;
    private int? _manualQuantity;
    private string _materialClass = MaterialClasses.Pipe;
    private string _materialName = string.Empty;
    private string _characteristic = string.Empty;
    private string _unit = "м.п.";
    private string _section = string.Empty;
    private string _layerName = string.Empty;
    private DateTime _updatedAt = DateTime.Now;

    /// <summary>"Pipe" | "Duct" | "Cable" | "Piece".</summary>
    public string MaterialClass
    {
        get => _materialClass;
        set
        {
            if (!SetField(ref _materialClass, value)) return;

            OnPropertyChanged(nameof(MaterialClassRu));
            OnPropertyChanged(nameof(IsPiece));
            NotifyLengthChanged();
            OnPropertyChanged(nameof(QuantityDisplay));
        }
    }

    /// <summary>Полное наименование материала (Material.Name).</summary>
    public string MaterialName
    {
        get => _materialName;
        set => SetField(ref _materialName, value);
    }

    /// <summary>Характеристика: «⌀89x4», «1250x800, 1 мм», «Dn15».</summary>
    public string Characteristic
    {
        get => _characteristic;
        set => SetField(ref _characteristic, value);
    }

    /// <summary>Единица измерения: «м.п.» или «шт.».</summary>
    public string Unit
    {
        get => _unit;
        set => SetField(ref _unit, value);
    }

    /// <summary>
    /// Вид штучного изделия (см. PieceKinds) — для группировки в Excel.
    /// У линейных материалов пустой.
    /// </summary>
    public string PieceKind { get; set; } = string.Empty;

    /// <summary>Русское название класса — для Excel.</summary>
    [JsonIgnore]
    public string MaterialClassRu => MaterialClasses.ToRussian(MaterialClass);

    /// <summary>Длина горизонтальных участков (по полилиниям слоя), м.</summary>
    public double HorizontalLengthM
    {
        get => _horizontalLengthM;
        set { if (SetField(ref _horizontalLengthM, value)) NotifyLengthChanged(); }
    }

    /// <summary>Суммарная длина вертикальных участков («Подъём»/«Опуск»), м.</summary>
    public double VerticalLengthM
    {
        get => _verticalLengthM;
        set { if (SetField(ref _verticalLengthM, value)) NotifyLengthChanged(); }
    }

    /// <summary>
    /// Длина, введённая вручную. Если задана — заменяет измеренную и не
    /// сбрасывается сканированием чертежа.
    /// </summary>
    public double? ManualLengthM
    {
        get => _manualLengthM;
        set
        {
            if (!SetField(ref _manualLengthM, value)) return;

            NotifyLengthChanged();
            OnPropertyChanged(nameof(HasManualValue));
        }
    }

    /// <summary>
    /// Общая длина, м: ручное значение либо горизонталь + вертикаль.
    ///
    /// Округление до 0,01 применяется к итогу, а не к слагаемым: сами
    /// <see cref="HorizontalLengthM"/> и <see cref="VerticalLengthM"/>
    /// хранятся с полной точностью.
    /// </summary>
    [JsonIgnore]
    public double LengthM =>
        MeasurementRounding.RoundLength(ManualLengthM ?? HorizontalLengthM + VerticalLengthM);

    /// <summary>
    /// Удельная площадь поверхности, м² на метр длины. Заполняется по материалу
    /// при пересчёте (см. <see cref="Services.DuctAreaCalculator"/>); у труб,
    /// кабелей и штучных изделий равна нулю.
    ///
    /// Хранится коэффициент, а не готовая площадь: длина записи меняется
    /// и после замера — вертикальными участками и ручной правкой, — а площадь
    /// обязана следовать за ней.
    /// </summary>
    public double AreaPerMeterM2
    {
        get => _areaPerMeterM2;
        set { if (SetField(ref _areaPerMeterM2, value)) OnPropertyChanged(nameof(AreaM2)); }
    }

    /// <summary>
    /// Площадь поверхности, м². Ноль означает, что площадь для этого материала
    /// не считается — в выгрузке такая ячейка остаётся пустой.
    /// </summary>
    [JsonIgnore]
    public double AreaM2 => MeasurementRounding.RoundArea(AreaPerMeterM2 * LengthM);

    /// <summary>Количество кругов-маркеров, найденных сканированием.</summary>
    public int ScannedQuantity
    {
        get => _scannedQuantity;
        set
        {
            if (!SetField(ref _scannedQuantity, value)) return;

            OnPropertyChanged(nameof(QuantityDisplay));
            NotifyMeasuredQuantityChanged();
        }
    }

    /// <summary>
    /// Количество, введённое вручную. Если задано — заменяет посчитанное
    /// и не сбрасывается сканированием.
    /// </summary>
    public int? ManualQuantity
    {
        get => _manualQuantity;
        set
        {
            if (!SetField(ref _manualQuantity, value)) return;

            OnPropertyChanged(nameof(Quantity));
            OnPropertyChanged(nameof(QuantityDisplay));
            OnPropertyChanged(nameof(HasManualValue));
            NotifyMeasuredQuantityChanged();
        }
    }

    /// <summary>Количество штучных изделий: ручное либо посчитанное.</summary>
    [JsonIgnore]
    public int Quantity => ManualQuantity ?? ScannedQuantity;

    /// <summary>
    /// Значение строки задано руками и больше не следует за чертежом.
    /// Палитра помечает такие строки, чтобы расхождение не осталось незамеченным.
    /// </summary>
    [JsonIgnore]
    public bool HasManualValue => ManualLengthM.HasValue || ManualQuantity.HasValue;

    /// <summary>Штучное изделие (считается количеством, а не длиной).</summary>
    [JsonIgnore]
    public bool IsPiece => MaterialClass == MaterialClasses.Piece;

    /// <summary>
    /// Единица измерения для журнала и ведомости: «м» либо «шт».
    /// Берётся по классу материала, а не из реестра, где встречаются
    /// «м.п.» и «шт.».
    /// </summary>
    [JsonIgnore]
    public string StatementUnit => IsPiece ? StatementBuilder.PieceUnit : StatementBuilder.LinearUnit;

    /// <summary>
    /// Количество для колонки «Кол-во»: длина в метрах у линейных материалов,
    /// число изделий у штучных. Одна колонка на оба случая — раздельные
    /// «Длина» и «Количество» всегда наполовину пустовали.
    /// </summary>
    [JsonIgnore]
    public string QuantityDisplay => IsPiece
        ? Quantity.ToString(CultureInfo.CurrentCulture)
        : LengthM.ToString(MeasurementRounding.LengthFormat, CultureInfo.CurrentCulture);

    // ======================= Привязка к спецификации =======================

    /// <summary>
    /// Номер позиции первоначальной спецификации, если запись ей соответствует.
    /// Null означает замер, которого в спецификации нет, — такое бывает
    /// сплошь и рядом и само по себе не ошибка.
    /// </summary>
    public int? SpecificationItemId
    {
        get => _specificationItemId;
        set
        {
            if (!SetField(ref _specificationItemId, value)) return;

            OnPropertyChanged(nameof(IsFromSpecification));
            OnPropertyChanged(nameof(SpecificationDifference));
        }
    }

    /// <summary>Имя файла спецификации, из которой пришла позиция.</summary>
    public string SpecificationFileName
    {
        get => _specificationFileName;
        set => SetField(ref _specificationFileName, value);
    }

    /// <summary>Марка из спецификации.</summary>
    public string Mark
    {
        get => _mark;
        set => SetField(ref _mark, value);
    }

    /// <summary>Код оборудования из спецификации.</summary>
    public string EquipmentCode
    {
        get => _equipmentCode;
        set => SetField(ref _equipmentCode, value);
    }

    /// <summary>Изготовитель из спецификации.</summary>
    public string Manufacturer
    {
        get => _manufacturer;
        set => SetField(ref _manufacturer, value);
    }

    /// <summary>Проектное количество по спецификации.</summary>
    public double? SpecificationQuantity
    {
        get => _specificationQuantity;
        set { if (SetField(ref _specificationQuantity, value)) OnPropertyChanged(nameof(SpecificationDifference)); }
    }

    /// <summary>Запись соответствует строке спецификации.</summary>
    [JsonIgnore]
    public bool IsFromSpecification => SpecificationItemId.HasValue;

    /// <summary>
    /// Поля спецификации в этой строке правились вручную.
    ///
    /// Палитра помечает такие строки: данные в них введены человеком, а не
    /// прочитаны из файла спецификации, и при перезагрузке файла их придётся
    /// вводить заново.
    /// </summary>
    public bool SpecificationEditedManually
    {
        get => _specificationEditedManually;
        set => SetField(ref _specificationEditedManually, value);
    }

    /// <summary>
    /// Наименованию строки не нашлось материала в реестре.
    ///
    /// Такое бывает у позиций спецификации: проектировщик пишет наименование
    /// по-своему. Замерять такую строку нечем — слой строится по материалу
    /// реестра, — поэтому она помечается, а замер по ней не начинается,
    /// пока человек не привяжет материал.
    /// </summary>
    public bool MaterialMissing
    {
        get => _materialMissing;
        set => SetField(ref _materialMissing, value);
    }

    /// <summary>
    /// Замеренное количество в единицах записи: метры у линейных материалов,
    /// штуки у штучных. Одно свойство на оба случая — так свод по
    /// спецификации не разбирается в классах.
    /// </summary>
    [JsonIgnore]
    public double MeasuredQuantity => IsPiece ? Quantity : LengthM;

    /// <summary>
    /// Расхождение с проектом: замерено минус по спецификации.
    /// Null, если позиции в спецификации нет — сравнивать не с чем.
    /// </summary>
    [JsonIgnore]
    public double? SpecificationDifference => SpecificationQuantity is null
        ? null
        : MeasurementRounding.RoundLength(MeasuredQuantity - SpecificationQuantity.Value);

    /// <summary>Участок / зона / часть проекта.</summary>
    public string Section
    {
        get => _section;
        set => SetField(ref _section, value);
    }

    /// <summary>Имя слоя, по которому считалась длина.</summary>
    public string LayerName
    {
        get => _layerName;
        set => SetField(ref _layerName, value);
    }

    /// <summary>Сколько полилиний было найдено на слое при последнем пересчёте.</summary>
    public int PolylineCount
    {
        get => _polylineCount;
        set => SetField(ref _polylineCount, value);
    }

    /// <summary>Имя DWG-файла, в котором делался замер.</summary>
    public string DrawingFileName { get; set; } = string.Empty;

    /// <summary>Время последнего пересчёта.</summary>
    public DateTime UpdatedAt
    {
        get => _updatedAt;
        set => SetField(ref _updatedAt, value);
    }

    /// <summary>
    /// Ключ уникальности строки журнала: материал + участок + DWG.
    /// Пересчёт с тем же ключом обновляет строку, а не плодит новые.
    /// </summary>
    [JsonIgnore]
    public string Key => BuildKey(MaterialName, Section, DrawingFileName);

    public static string BuildKey(string? materialName, string? section, string? drawingFileName) =>
        // Разделитель — управляющий символ: он не встречается в наименованиях материалов и участков,
        // поэтому «Труба»+«1» и «Труб»+«а1» дают разные ключи.
        string.Join('\u0001',
            (materialName ?? string.Empty).Trim().ToUpperInvariant(),
            (section ?? string.Empty).Trim().ToUpperInvariant(),
            (drawingFileName ?? string.Empty).Trim().ToUpperInvariant());

    /// <summary>
    /// Снять привязку к спецификации, оставив саму запись и её замер.
    ///
    /// Нужно при перезагрузке спецификации: позиции, которой в новом файле
    /// нет, соответствовать больше нечему, но замер по чертежу от этого
    /// не перестаёт быть верным.
    /// </summary>
    public void ClearSpecificationBinding()
    {
        SpecificationItemId = null;
        SpecificationFileName = string.Empty;
        SpecificationQuantity = null;
        Mark = string.Empty;
        EquipmentCode = string.Empty;
        Manufacturer = string.Empty;
    }

    /// <summary>
    /// Обнулить всё, что пришло из чертежа: длины, количество и число полилиний.
    ///
    /// Нужно позициям спецификации, под которыми не осталось геометрии:
    /// такая строка — план работ, её нельзя удалять, но и показывать
    /// прежний замер она больше не вправе. Ручное значение не трогается:
    /// оно введено человеком и по правилам журнала переживает пересчёты.
    /// </summary>
    public void ResetMeasuredValues()
    {
        HorizontalLengthM = 0;
        VerticalLengthM = 0;
        ScannedQuantity = 0;
        PolylineCount = 0;
    }

    /// <summary>Длина складывается из нескольких полей — уведомляем обо всех производных.</summary>
    private void NotifyLengthChanged()
    {
        OnPropertyChanged(nameof(LengthM));
        OnPropertyChanged(nameof(QuantityDisplay));
        OnPropertyChanged(nameof(AreaM2));
        NotifyMeasuredQuantityChanged();
    }

    /// <summary>Замеренное количество и расхождение с проектом — производные от него.</summary>
    private void NotifyMeasuredQuantityChanged()
    {
        OnPropertyChanged(nameof(MeasuredQuantity));
        OnPropertyChanged(nameof(SpecificationDifference));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
