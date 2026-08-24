using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CadMeasureDomain.Models;
using CadMeasureDomain.Services;
using CadMeasureDomain.Tools;

namespace CadMeasurePlugin.UI;

/// <summary>
/// Окно добавления материала в реестр — с нуля либо копированием существующего.
///
/// При копировании переносятся ВСЕ поля, включая наименование: пользователь
/// правит копию на месте. Уникальность наименования проверяется при сохранении,
/// поэтому «сохранить копию, ничего не изменив» просто не выйдет.
///
/// Класс материала задаётся снаружи и не редактируется: окно открывается из
/// списка конкретного класса, и материал должен появиться именно в этом списке.
/// Набор числовых полей меняется под класс и под форму сечения воздуховода,
/// поэтому в разметке три универсальных поля, а подписи и тип разбора
/// (целое / дробное) проставляются здесь.
///
/// Числовые характеристики необязательны: слой создастся и без них,
/// по запасному шаблону (см. LayerNameFactory.ComposeBaseName).
/// </summary>
public partial class MaterialEditorWindow : Window
{
    /// <summary>Что вводится в поле и как это разбирать.</summary>
    private enum FieldKind
    {
        Hidden,
        /// <summary>Целое число: стороны воздуховода, условный проход.</summary>
        Integer,
        /// <summary>Дробное число: диаметр трубы, толщина стенки, толщина листа.</summary>
        Decimal
    }

    private readonly MaterialRepository _repository;
    private readonly string _materialClass;
    private readonly bool _initialized;

    private FieldKind _field1Kind = FieldKind.Hidden;
    private FieldKind _field2Kind = FieldKind.Hidden;
    private FieldKind _field3Kind = FieldKind.Hidden;

    /// <summary>Созданный материал (null, если нажата «Отмена»).</summary>
    public Material? CreatedMaterial { get; private set; }

    /// <param name="repository">Реестр — нужен для проверки уникальности и записи файла.</param>
    /// <param name="materialClass">Класс создаваемого материала.</param>
    /// <param name="prototype">Материал-образец при копировании, либо null для создания с нуля.</param>
    public MaterialEditorWindow(MaterialRepository repository, string materialClass, Material? prototype)
    {
        InitializeComponent();

        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _materialClass = materialClass;

        var isCopy = prototype is not null;

        Title = isCopy ? "Копирование материала" : "Добавление материала";
        HeaderText.Text = isCopy
            ? "Копия материала — измени наименование перед сохранением"
            : "Новый материал";

        ClassText.Text = $"Класс: {MaterialClasses.ToRussian(materialClass)}";
        TargetFileText.Text = $"Будет записан в файл: {_repository.LoadedFrom}";

        // Полная копия, включая наименование.
        var source = prototype is null
            ? new Material { Class = materialClass, Unit = DefaultUnitFor(materialClass) }
            : MaterialRepository.Duplicate(prototype);

        if (materialClass == MaterialClasses.Duct)
        {
            ShapePanel.Visibility = Visibility.Visible;
            RoundShapeButton.IsChecked = source.IsRoundDuct;
            RectShapeButton.IsChecked = !source.IsRoundDuct;
        }

        if (materialClass == MaterialClasses.Piece)
        {
            PieceKindPanel.Visibility = Visibility.Visible;
            foreach (var kind in PieceKinds.All) PieceKindBox.Items.Add(kind);

            PieceKindBox.SelectedItem = PieceKinds.All.FirstOrDefault(k =>
                string.Equals(k, source.PieceKind, StringComparison.OrdinalIgnoreCase)) ?? PieceKinds.PipeFitting;
        }

        ConfigureFields();
        FillFrom(source);

        _initialized = true;
        UpdatePreview();

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            // При копировании выделяем наименование целиком: его нужно менять.
            if (isCopy) NameBox.SelectAll();
            else NameBox.CaretIndex = NameBox.Text.Length;
        };
    }

    private bool IsRoundDuctSelected => RoundShapeButton.IsChecked == true;

    private static string DefaultUnitFor(string materialClass) =>
        materialClass == MaterialClasses.Piece ? "шт." : "м.п.";

    /// <summary>Подписи, видимость и тип разбора числовых полей.</summary>
    private void ConfigureFields()
    {
        switch (_materialClass)
        {
            case MaterialClasses.Pipe:
                SetField(Field1Label, Field1Box, "Наружный диаметр, мм:", FieldKind.Decimal, ref _field1Kind);
                SetField(Field2Label, Field2Box, "Толщина стенки, мм:", FieldKind.Decimal, ref _field2Kind);
                SetField(Field3Label, Field3Box, string.Empty, FieldKind.Hidden, ref _field3Kind);
                break;

            case MaterialClasses.Duct when IsRoundDuctSelected:
                SetField(Field1Label, Field1Box, "Диаметр, мм:", FieldKind.Decimal, ref _field1Kind);
                SetField(Field2Label, Field2Box, string.Empty, FieldKind.Hidden, ref _field2Kind);
                SetField(Field3Label, Field3Box, "Толщина листа, мм:", FieldKind.Decimal, ref _field3Kind);
                break;

            case MaterialClasses.Duct:
                SetField(Field1Label, Field1Box, "Ширина, мм:", FieldKind.Integer, ref _field1Kind);
                SetField(Field2Label, Field2Box, "Высота, мм:", FieldKind.Integer, ref _field2Kind);
                SetField(Field3Label, Field3Box, "Толщина листа, мм:", FieldKind.Decimal, ref _field3Kind);
                break;

            case MaterialClasses.Cable:
                SetField(Field1Label, Field1Box, "Число жил:", FieldKind.Integer, ref _field1Kind);
                SetField(Field2Label, Field2Box, "Сечение жилы, мм²:", FieldKind.Decimal, ref _field2Kind);
                SetField(Field3Label, Field3Box, string.Empty, FieldKind.Hidden, ref _field3Kind);
                break;

            case MaterialClasses.Piece:
                SetField(Field1Label, Field1Box, "Условный проход Dn, мм:", FieldKind.Integer, ref _field1Kind);
                SetField(Field2Label, Field2Box, string.Empty, FieldKind.Hidden, ref _field2Kind);
                SetField(Field3Label, Field3Box, string.Empty, FieldKind.Hidden, ref _field3Kind);
                break;

            default:
                // Неизвестный класс из materials.json: числовых характеристик нет,
                // имя слоя построится по запасному шаблону.
                SetField(Field1Label, Field1Box, string.Empty, FieldKind.Hidden, ref _field1Kind);
                SetField(Field2Label, Field2Box, string.Empty, FieldKind.Hidden, ref _field2Kind);
                SetField(Field3Label, Field3Box, string.Empty, FieldKind.Hidden, ref _field3Kind);
                break;
        }
    }

    private static void SetField(TextBlock label, TextBox box, string caption, FieldKind kind, ref FieldKind target)
    {
        target = kind;

        var visible = kind != FieldKind.Hidden;
        label.Text = caption;
        label.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        box.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        if (!visible) box.Clear();
    }

    private void FillFrom(Material source)
    {
        NameBox.Text = source.Name;
        UnitBox.Text = string.IsNullOrWhiteSpace(source.Unit) ? DefaultUnitFor(_materialClass) : source.Unit;

        switch (_materialClass)
        {
            case MaterialClasses.Pipe:
                Field1Box.Text = Format(source.DiameterMm);
                Field2Box.Text = Format(source.WallThicknessMm);
                break;

            case MaterialClasses.Duct when IsRoundDuctSelected:
                Field1Box.Text = Format(source.DiameterMm);
                Field3Box.Text = Format(source.SheetThicknessMm);
                break;

            case MaterialClasses.Duct:
                Field1Box.Text = Format(source.WidthMm);
                Field2Box.Text = Format(source.HeightMm);
                Field3Box.Text = Format(source.SheetThicknessMm);
                break;

            case MaterialClasses.Cable:
                Field1Box.Text = Format(source.CoreCount);
                Field2Box.Text = Format(source.CrossSectionMm2);
                break;

            case MaterialClasses.Piece:
                Field1Box.Text = Format(source.NominalDiameterMm);
                break;
        }
    }

    private static string Format(int? value) => value?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;

    /// <summary>Дробное — в локали пользователя: ввёл «3,5», увидел «3,5».</summary>
    private static string Format(double? value) => value is null ? string.Empty : MaterialFormatter.FormatNumber(value.Value);

    /// <summary>Собрать материал из полей. Числа, которые не разобрались, остаются null.</summary>
    private Material BuildMaterial()
    {
        var material = new Material
        {
            Class = _materialClass,
            Name = NameBox.Text?.Trim() ?? string.Empty,
            Unit = UnitBox.Text?.Trim() ?? string.Empty
        };

        switch (_materialClass)
        {
            case MaterialClasses.Pipe:
                material.DiameterMm = ParseDecimal(Field1Box.Text);
                material.WallThicknessMm = ParseDecimal(Field2Box.Text);
                break;

            case MaterialClasses.Duct when IsRoundDuctSelected:
                material.DiameterMm = ParseDecimal(Field1Box.Text);
                material.SheetThicknessMm = ParseDecimal(Field3Box.Text);
                break;

            case MaterialClasses.Duct:
                material.WidthMm = ParseInteger(Field1Box.Text);
                material.HeightMm = ParseInteger(Field2Box.Text);
                material.SheetThicknessMm = ParseDecimal(Field3Box.Text);
                break;

            case MaterialClasses.Cable:
                material.CoreCount = ParseInteger(Field1Box.Text);
                material.CrossSectionMm2 = ParseDecimal(Field2Box.Text);
                break;

            case MaterialClasses.Piece:
                material.NominalDiameterMm = ParseInteger(Field1Box.Text);
                material.PieceKind = PieceKindBox.SelectedItem as string;
                break;
        }

        return material;
    }

    /// <summary>Разбор дробного числа: принимаем и «3,5», и «3.5».</summary>
    private static double? ParseDecimal(string? text) =>
        FlexibleNullableDoubleConverter.TryParse(text, out var value) ? value : null;

    private static int? ParseInteger(string? text)
    {
        var normalized = (text ?? string.Empty).Trim();
        if (normalized.Length == 0) return null;

        return int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>Целые поля пропускают только цифры, дробные — ещё запятую и точку.</summary>
    private void Number_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var kind = GetKind(sender as TextBox);
        if (kind == FieldKind.Hidden) return;

        foreach (var ch in e.Text)
        {
            if (char.IsDigit(ch)) continue;
            if (kind == FieldKind.Decimal && (ch == ',' || ch == '.')) continue;

            e.Handled = true;
            return;
        }
    }

    private FieldKind GetKind(TextBox? box)
    {
        if (ReferenceEquals(box, Field1Box)) return _field1Kind;
        if (ReferenceEquals(box, Field2Box)) return _field2Kind;
        if (ReferenceEquals(box, Field3Box)) return _field3Kind;
        return FieldKind.Hidden;
    }

    private void Shape_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;

        // Сохраняем то, что уже введено, и переносим в новую раскладку полей.
        var current = BuildMaterial();
        ConfigureFields();
        FillFrom(current);
        UpdatePreview();
    }

    private void Field_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_initialized) return;
        UpdatePreview();
    }

    private void PieceKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        UpdatePreview();
    }

    /// <summary>
    /// Живой предпросмотр: инженер сразу видит и характеристику для спецификации,
    /// и имя слоя, на который лягут замеры.
    /// </summary>
    private void UpdatePreview()
    {
        var material = BuildMaterial();

        var characteristic = material.Characteristic;
        PreviewCharacteristicText.Text = string.IsNullOrEmpty(characteristic)
            ? "Характеристика: — (числовые поля не заполнены)"
            : $"Характеристика: {characteristic}";

        try
        {
            PreviewLayerText.Text = $"Слой: {LayerNameFactory.ComposeBaseName(material)}";
        }
        catch (Exception ex)
        {
            PreviewLayerText.Text = $"Слой: не удалось построить — {ex.Message}";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var material = BuildMaterial();

        if (!Validate(material, out var error))
        {
            ShowError(error);
            return;
        }

        try
        {
            // Add сам проверяет уникальность и пишет materials.json;
            // если файл не записался, материал в реестр не попадёт.
            CreatedMaterial = _repository.Add(material);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private bool Validate(Material material, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(material.Name))
        {
            error = "Заполни наименование материала.";
            NameBox.Focus();
            return false;
        }

        if (string.IsNullOrWhiteSpace(material.Unit))
        {
            error = "Заполни единицу измерения («м.п.» или «шт.»).";
            UnitBox.Focus();
            return false;
        }

        if (_repository.NameExists(material.Name))
        {
            error = $"Материал с наименованием «{material.Name}» уже есть в реестре.\n" +
                    "Наименование должно быть уникальным — измени его.";
            NameBox.Focus();
            NameBox.SelectAll();
            return false;
        }

        // Заполненное, но неразобранное число — почти всегда опечатка.
        if (!CheckNumberField(Field1Box, Field1Label, _field1Kind, out error)) return false;
        if (!CheckNumberField(Field2Box, Field2Label, _field2Kind, out error)) return false;
        if (!CheckNumberField(Field3Box, Field3Label, _field3Kind, out error)) return false;

        return true;
    }

    private static bool CheckNumberField(TextBox box, TextBlock label, FieldKind kind, out string error)
    {
        error = string.Empty;
        if (kind == FieldKind.Hidden) return true;

        var text = box.Text?.Trim() ?? string.Empty;
        if (text.Length == 0) return true;

        var caption = label.Text.TrimEnd(':');

        if (kind == FieldKind.Integer)
        {
            var parsed = ParseInteger(text);
            if (parsed is null or <= 0)
            {
                error = $"Поле «{caption}»: нужно целое число больше нуля либо пустое значение.";
                box.Focus();
                box.SelectAll();
                return false;
            }

            return true;
        }

        var value = ParseDecimal(text);
        if (value is null or <= 0)
        {
            error = $"Поле «{caption}»: нужно число больше нуля (например, 3,5) либо пустое значение.";
            box.Focus();
            box.SelectAll();
            return false;
        }

        return true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CreatedMaterial = null;
        DialogResult = false;
    }
}
