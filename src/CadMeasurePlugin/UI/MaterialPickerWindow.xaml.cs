using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CadMeasureDomain.Models;
using CadMeasureDomain.Services;
using CadMeasurePlugin.Services;

namespace CadMeasurePlugin.UI;

/// <summary>
/// Окно выбора материала.
///
/// Материалы разделены по классам на вкладки: «Трубопроводы», «Воздуховоды»,
/// «Кабельная продукция», «Штучные изделия». Каждая вкладка показывает только
/// свой класс, поиск работает внутри активной вкладки.
///
/// Инструмент замера пользователь не выбирает — он определяется классом
/// выбранного материала, поэтому вкладки свободно переключаются.
///
/// В таблице только «Наименование»: класс и размеры уже содержатся в нём.
/// Характеристика и единица измерения остаются в модели и уходят в Excel.
///
/// Отсюда же реестр пополняется: «Добавить материал», «Копировать выбранный»
/// (копируются все поля) и «Удалить материал» (каскадом, вместе с замерами).
///
/// Список рассчитан на сотни позиций: включена виртуализация, фильтр по
/// вхождению подстроки работает через ICollectionView, без пересборки коллекции.
/// </summary>
public partial class MaterialPickerWindow : Window
{
    private readonly MaterialRepository _repository;
    private readonly LayerService _layerService;
    private readonly MaterialDeletionService _deletionService;
    private readonly string _section;

    private ICollectionView? _view;
    private bool _initialized;

    /// <summary>Материал, выбранный пользователем (null, если нажата «Отмена»).</summary>
    public Material? SelectedMaterial { get; private set; }

    public MaterialPickerWindow(
        MaterialRepository repository,
        LayerService layerService,
        MaterialDeletionService deletionService,
        string section,
        Material? currentMaterial)
    {
        InitializeComponent();

        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _layerService = layerService ?? throw new ArgumentNullException(nameof(layerService));
        _deletionService = deletionService ?? throw new ArgumentNullException(nameof(deletionService));
        _section = section ?? string.Empty;

        BuildTabs();

        // Открываемся на вкладке текущего материала.
        SelectTabForClass(currentMaterial?.Class ?? MaterialClasses.Pipe);

        _initialized = true;
        LoadMaterials(currentMaterial);

        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            if (MaterialsList.SelectedItem is not null)
                MaterialsList.ScrollIntoView(MaterialsList.SelectedItem);
        };
    }

    /// <summary>Активный класс материалов (по выбранной вкладке).</summary>
    private string ActiveClass =>
        (ClassTabs.SelectedItem as TabItem)?.Tag as string ?? MaterialClasses.Pipe;

    /// <summary>
    /// Вкладки строятся из MaterialClasses.All: добавление нового класса
    /// в домене само добавит вкладку, править разметку не нужно.
    /// Содержимое вкладок пустое — список общий и лежит ниже, вкладки
    /// работают только как переключатель класса.
    /// </summary>
    private void BuildTabs()
    {
        foreach (var materialClass in MaterialClasses.All)
        {
            ClassTabs.Items.Add(new TabItem
            {
                Header = MaterialClasses.ToTabTitle(materialClass),
                Tag = materialClass,
                Padding = new Thickness(12, 5, 12, 5)
            });
        }
    }

    private void SelectTabForClass(string materialClass)
    {
        foreach (TabItem tab in ClassTabs.Items)
        {
            if (!string.Equals(tab.Tag as string, materialClass, StringComparison.OrdinalIgnoreCase)) continue;

            ClassTabs.SelectedItem = tab;
            return;
        }

        if (ClassTabs.Items.Count > 0) ClassTabs.SelectedIndex = 0;
    }

    private void LoadMaterials(Material? materialToSelect)
    {
        var items = _repository.GetByClass(ActiveClass);

        MaterialsList.ItemsSource = items;
        _view = CollectionViewSource.GetDefaultView(MaterialsList.ItemsSource);
        _view.Filter = FilterByName;

        if (materialToSelect is not null &&
            string.Equals(materialToSelect.Class, ActiveClass, StringComparison.OrdinalIgnoreCase))
        {
            var match = items.FirstOrDefault(m =>
                string.Equals(m.Name, materialToSelect.Name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) MaterialsList.SelectedItem = match;
        }

        UpdateFoundCount();
    }

    /// <summary>
    /// Фильтр по первым буквам нескольких слов: «тр 88» находит
    /// «Труба стальная электросварная Dn80 (⌀88,9x3,5)».
    /// Правило одно на весь плагин — см. <see cref="MaterialSearch"/>.
    /// </summary>
    private bool FilterByName(object item) =>
        item is Material material && MaterialSearch.Matches(material.Name, SearchBox.Text);

    private void ClassTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        if (!ReferenceEquals(e.OriginalSource, ClassTabs)) return;

        LoadMaterials(null);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized || _view is null) return;

        _view.Refresh();
        UpdateFoundCount();
    }

    private void UpdateFoundCount()
    {
        if (_view is null) return;

        var shown = _view.Cast<object>().Count();
        var total = _repository.GetByClass(ActiveClass).Count;

        FoundCountText.Text = shown == total
            ? $"Всего: {total}"
            : $"Найдено: {shown} из {total}";
    }

    private void ResetSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void MaterialsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var material = MaterialsList.SelectedItem as Material;

        OkButton.IsEnabled = material is not null;
        CopyMaterialButton.IsEnabled = material is not null;
        DeleteMaterialButton.IsEnabled = material is not null;

        SelectedText.Text = material is null
            ? string.Empty
            : $"Выбрано: {material.Name}";
    }

    private void MaterialsList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (MaterialsList.SelectedItem is Material) Confirm();
    }

    // ======================= Пополнение реестра =======================

    private void AddMaterial_Click(object sender, RoutedEventArgs e) => CreateMaterial(prototype: null);

    private void CopyMaterial_Click(object sender, RoutedEventArgs e)
    {
        if (MaterialsList.SelectedItem is not Material prototype)
        {
            MessageBox.Show(this, "Выдели материал, который нужно скопировать.",
                PluginSettings.MessageBoxTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        CreateMaterial(prototype);
    }

    /// <summary>
    /// Создать позицию реестра и сразу подготовить под неё слой.
    /// Порядок: запись в materials.json (внутри MaterialRepository.Add),
    /// затем создание слоя, затем обновление списка.
    /// </summary>
    private void CreateMaterial(Material? prototype)
    {
        var editor = new MaterialEditorWindow(_repository, ActiveClass, prototype);
        if (!AcadUiHelper.ShowDialogOverAcad(editor) || editor.CreatedMaterial is null) return;

        var created = editor.CreatedMaterial;

        // Материал уже в реестре и в файле. Слой — отдельный шаг: если чертёж
        // не открыт, позицию терять не за что, просто сообщаем.
        string layerNote;
        try
        {
            var layerName = _layerService.EnsureLayerForSection(created, _section);
            layerNote = $"Слой «{layerName}» создан и сделан текущим.";
        }
        catch (Exception ex)
        {
            layerNote = $"Слой создать не удалось: {ex.Message}\nОн будет создан при первом замере.";
        }

        // Сбрасываем поиск, иначе новая позиция может не попасть под фильтр.
        SearchBox.Clear();
        LoadMaterials(created);
        if (MaterialsList.SelectedItem is not null) MaterialsList.ScrollIntoView(MaterialsList.SelectedItem);

        MessageBox.Show(this,
            $"Материал добавлен в реестр:\n{created.Name}\n\n" +
            $"Файл: {_repository.LoadedFrom}\n{layerNote}",
            PluginSettings.MessageBoxTitle, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// Удалить материал из реестра каскадом: сначала его геометрия и записи
    /// журнала, только потом сама позиция. Если геометрию стереть не удалось,
    /// материал остаётся — иначе в чертеже повисли бы линии на слое,
    /// которому больше не соответствует ни одна позиция реестра.
    /// </summary>
    private void DeleteMaterial_Click(object sender, RoutedEventArgs e)
    {
        if (MaterialsList.SelectedItem is not Material material)
        {
            MessageBox.Show(this, "Выдели материал, который нужно удалить.",
                PluginSettings.MessageBoxTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var usage = _deletionService.GetUsage(material);

            var details = usage.IsUsed
                ? $"\n\nМатериал используется:\n" +
                  $"  • объектов на его слоях в текущем чертеже: {usage.ObjectsInCurrentDrawing}\n" +
                  $"  • слои: {usage.LayerName}\n" +
                  $"  • записей журнала по текущему чертежу: {usage.RecordsInCurrentDrawing}" +
                  BuildOtherDrawingsNote(usage) +
                  "\n\nВсё перечисленное будет удалено вместе с материалом."
                : "\n\nМатериал нигде не используется.";

            if (MessageBox.Show(this,
                    $"Удалить материал из реестра?\n\n{material.Name}{details}\n\n" +
                    $"Файл: {_repository.LoadedFrom}",
                    PluginSettings.MessageBoxTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes)
                return;

            var result = _deletionService.Delete(material);

            if (!result.Success)
            {
                MessageBox.Show(this, $"Материал НЕ удалён.\n\n{result.Message}",
                    PluginSettings.MessageBoxTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SearchBox.Clear();
            LoadMaterials(null);

            MessageBox.Show(this, result.Message,
                PluginSettings.MessageBoxTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Удаление материала: не удалось выполнить.\n\n{ex.Message}",
                PluginSettings.MessageBoxTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Замеры в других чертежах плагин удалит только из журнала: править
    /// неоткрытый DWG он не может. Об этом надо предупредить явно.
    /// </summary>
    private static string BuildOtherDrawingsNote(MaterialUsage usage)
    {
        if (usage.RecordsInOtherDrawings == 0) return string.Empty;

        var drawings = usage.OtherDrawingRecords
            .Select(r => r.DrawingFileName)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return $"\n  • записей журнала по другим чертежам: {usage.RecordsInOtherDrawings} " +
               $"({string.Join(", ", drawings)})\n" +
               "    ВНИМАНИЕ: их записи будут убраны из журнала, но геометрия в тех\n" +
               "    чертежах останется — плагин правит только открытый чертёж.";
    }

    // ======================= Подтверждение выбора =======================

    private void Ok_Click(object sender, RoutedEventArgs e) => Confirm();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        SelectedMaterial = null;
        DialogResult = false;
    }

    private void Confirm()
    {
        if (MaterialsList.SelectedItem is not Material material)
        {
            MessageBox.Show(this, "Материал не выбран.", PluginSettings.MessageBoxTitle,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedMaterial = material;
        DialogResult = true;
    }
}
