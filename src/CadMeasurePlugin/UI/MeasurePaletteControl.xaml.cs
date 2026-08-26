using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using CadMeasureDomain.Models;
using CadMeasureDomain.Services;
using CadMeasurePlugin.Commands;
using CadMeasurePlugin.Services;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace CadMeasurePlugin.UI;

/// <summary>
/// Главная палитра плагина: выбор материала, участок, запуск замера,
/// журнал и экспорт.
///
/// Переключателя инструментов нет — инструмент определяется классом
/// выбранного материала (труба / воздуховод / кабель / штучное изделие).
/// Кнопки «Записать» тоже нет: журнал выводится из чертежа автоматически,
/// а «Обновить журнал» просто запускает пересканирование немедленно.
///
/// Палитра живёт в UI-потоке AutoCAD, поэтому все обращения к базе чертежа
/// идут через сервисы, которые сами берут блокировку документа.
/// Интерактивное рисование запускается командой (SendStringToExecute):
/// из обработчика кнопки указывать точки нельзя.
/// </summary>
public partial class MeasurePaletteControl : UserControl
{
    private readonly MeasureSession _session = MeasureSession.Instance;
    private readonly JournalWatcher _watcher;
    private readonly ContextMenu _moreMenu;

    // Пункты меню «Ещё», состояние которых меняется по ходу работы.
    private MenuItem? _showLabelsMenuItem;
    private MenuItem? _resetVerticalMenuItem;
    private MenuItem? _deleteSpecificationMenuItem;
    private MenuItem? _onlyMeasurementLayersMenuItem;
    private MenuItem? _onlyCurrentLayerMenuItem;

    // Инициализируется значением true (а не в теле конструктора): разметка
    // задаёт Text ещё во время InitializeComponent, и события сработали бы
    // раньше, чем будут созданы остальные элементы палитры.
    private bool _suppressEvents = true;

    public MeasurePaletteControl()
    {
        InitializeComponent();

        try
        {
            _session.EnsureMaterialsLoaded();
        }
        catch (Exception ex)
        {
            AcadUiHelper.ShowError(this, $"Не удалось загрузить реестр материалов:\n{ex.Message}");
        }

        _session.SyncCurrentDrawing();
        JournalGrid.ItemsSource = _session.Journal.Records;
        RefreshMaterialColumnSource();

        SectionBox.Text = _session.Section;
        _moreMenu = BuildMoreMenu();
        ApplyHeaderContextMenu();

        ApplySpecificationColumnVisibility();
        UpdateSpecificationHeader();

        // Журнал пересчитывается сам: следим за завершением команд AutoCAD
        // и за изменениями реестра.
        _watcher = new JournalWatcher(_session.JournalService, Dispatcher);
        _watcher.Refreshed += Watcher_Refreshed;

        _suppressEvents = false;

        UpdateMaterialInfo();
        UpdateJournalHeader();
        ReportMaterialsSource();

        // Подписки живут ровно столько, сколько палитра показана: при скрытии
        // PaletteSet элемент выгружается, и висящие подписки удерживали бы
        // палитру в памяти и дёргали уже ненужный UI.
        Loaded += OnPaletteLoaded;
        Unloaded += OnPaletteUnloaded;
    }

    private void OnPaletteLoaded(object sender, RoutedEventArgs e)
    {
        AcadApp.DocumentManager.DocumentActivated += DocumentManager_DocumentActivated;
        _session.Journal.Records.CollectionChanged += Records_CollectionChanged;
        _session.Materials.Changed += Materials_Changed;

        _watcher.Start();

        // Пока палитра была скрыта, могли сменить чертёж или дорисовать трассы.
        _session.SyncCurrentDrawing();
        UpdateJournalHeader();
        UpdateMaterialInfo();
    }

    private void OnPaletteUnloaded(object sender, RoutedEventArgs e)
    {
        AcadApp.DocumentManager.DocumentActivated -= DocumentManager_DocumentActivated;
        _session.Journal.Records.CollectionChanged -= Records_CollectionChanged;
        _session.Materials.Changed -= Materials_Changed;

        _watcher.Stop();
    }

    // ======================= Служебное =======================

    /// <summary>
    /// Обёртка для обработчиков: показывает ошибку в MessageBox и в строке
    /// состояния вместо падения AutoCAD.
    /// </summary>
    private void Run(string actionName, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            SetStatus($"Ошибка: {actionName} — {ex.Message}");
            AcadUiHelper.ShowError(this, $"{actionName}: не удалось выполнить.\n\n{ex.Message}");
        }
    }

    private void SetStatus(string message) => StatusText.Text = message;

    private void Records_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateJournalHeader();

    private void DocumentManager_DocumentActivated(object? sender, Autodesk.AutoCAD.ApplicationServices.DocumentCollectionEventArgs e)
    {
        // Событие приходит не из UI-потока WPF — возвращаемся в него.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _session.SyncCurrentDrawing();
            UpdateJournalHeader();
            UpdateMaterialInfo();
        }));
    }

    /// <summary>Наполнить выпадающий список колонки «Материал» наименованиями реестра.</summary>
    private void RefreshMaterialColumnSource()
    {
        MaterialColumn.ItemsSource = _session.JournalEdit.GetMaterialNames();
    }

    // ======================= Фильтры журнала =======================

    // Действующий фильтр. Живёт в полях, а не в элементах палитры: сам фильтр
    // вызывается из контекстного меню таблицы и на панели ничего не занимает.
    private string? _filterMaterial;
    private int? _filterSpecificationNumber;

    /// <summary>Фильтр включён — часть строк журнала скрыта.</summary>
    private bool IsFilterActive => _filterMaterial is not null || _filterSpecificationNumber is not null;

    private void OpenJournalFilter_Click(object sender, RoutedEventArgs e)
    {
        Run("Фильтр журнала", () =>
        {
            var window = new JournalFilterWindow(
                _session.JournalEdit.GetMaterialNames(),
                _session.Specification,
                _filterMaterial,
                _filterSpecificationNumber);

            if (!AcadUiHelper.ShowDialogOverAcad(window)) return;

            _filterMaterial = window.Material;
            _filterSpecificationNumber = window.SpecificationNumber;

            ApplyJournalFilter();
        });
    }

    private void ResetJournalFilter_Click(object sender, RoutedEventArgs e)
    {
        if (!IsFilterActive)
        {
            SetStatus("Фильтр не включён — в таблице и так все записи.");
            return;
        }

        _filterMaterial = null;
        _filterSpecificationNumber = null;

        ApplyJournalFilter();
    }

    /// <summary>
    /// Пересобрать предикат представления журнала.
    ///
    /// Фильтруется представление, а не сама коллекция: журнал остаётся полным,
    /// пересчёт по чертежу продолжает работать со всеми записями, а скрытые
    /// строки никуда не деваются из Excel и ведомости.
    /// </summary>
    private void ApplyJournalFilter()
    {
        var view = CollectionViewSource.GetDefaultView(JournalGrid.ItemsSource);
        if (view is null) return;

        if (!IsFilterActive)
        {
            view.Filter = null;
            view.Refresh();

            UpdateJournalHeader();
            SetStatus("Фильтр журнала снят: показаны все записи.");
            return;
        }

        var material = _filterMaterial;
        var specificationNumber = _filterSpecificationNumber;

        view.Filter = candidate =>
        {
            if (candidate is not MeasurementRecord record) return false;

            var byMaterial = material is null ||
                             string.Equals(record.MaterialName, material, StringComparison.OrdinalIgnoreCase);
            var bySpecification = specificationNumber is null ||
                                  record.SpecificationItemId == specificationNumber;

            return byMaterial && bySpecification;
        };

        view.Refresh();
        UpdateJournalHeader();

        var parts = new List<string>();
        if (material is not null) parts.Add($"материал «{material}»");
        if (specificationNumber is not null) parts.Add($"позиция спецификации №{specificationNumber}");

        SetStatus($"Фильтр журнала: {string.Join(", ", parts)}. Записи скрыты только в таблице — " +
                  "в ведомость и Excel попадают все.");
    }

    /// <summary>Сколько строк реально показано в таблице при включённом фильтре.</summary>
    private int VisibleRecordCount() =>
        CollectionViewSource.GetDefaultView(JournalGrid.ItemsSource) is { } view
            ? view.Cast<object>().Count()
            : _session.Journal.Records.Count;

    // ======================= Правка журнала в таблице =======================

    /// <summary>
    /// Двойной клик по строке делает её материал текущим: подбирает инструмент
    /// по классу, активирует слой и подставляет участок строки.
    ///
    /// Событие намеренно не помечается обработанным — штатное редактирование
    /// ячейки по двойному клику продолжает работать.
    /// </summary>
    private void JournalGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        Run("Выбор материала из журнала", () =>
        {
            if (JournalGrid.CurrentItem is not MeasurementRecord record) return;

            var material = _session.Materials.FindByName(record.MaterialName);
            if (material is null)
            {
                record.MaterialMissing = true;

                // Для позиции спецификации это штатная ситуация: проектировщик
                // пишет наименование по-своему. Замер по такой строке начать
                // нельзя — сначала её нужно привязать к позиции реестра.
                AcadUiHelper.ShowWarning(this, record.IsFromSpecification
                    ? $"Позиции спецификации «{record.MaterialName}» не найден материал в реестре.\n\n" +
                      "Замер по ней невозможен: слой строится по материалу реестра.\n" +
                      "Выбери подходящую позицию в колонке наименования — либо добавь материал в реестр " +
                      "через окно выбора материала."
                    : $"Материала «{record.MaterialName}» больше нет в реестре.");
                return;
            }

            record.MaterialMissing = false;

            // Участок берём из строки: слой материала зависит от него,
            // иначе активировался бы слой другого участка.
            _suppressEvents = true;
            SectionBox.Text = record.Section;
            _suppressEvents = false;
            _session.Section = record.Section;

            try
            {
                var layerName = _session.SelectMaterialAndActivateLayer(material);
                SetStatus($"Из журнала выбран материал: {material.Name}. Участок: " +
                          $"{(string.IsNullOrWhiteSpace(record.Section) ? "не задан" : record.Section)}. " +
                          $"Текущий слой: {layerName}.");
            }
            catch (Exception ex)
            {
                _session.GetToolFor(material.Class).SelectMaterial(material);
                SetStatus($"Материал выбран, слой не активирован: {ex.Message}");
            }

            UpdateMaterialInfo();
        });
    }

    /// <summary>
    /// Пока открыт редактор ячейки, пересчёт журнала приостановлен: он
    /// пересоздаёт и удаляет строки, и правка потерялась бы прямо под руками.
    /// </summary>
    private void JournalGrid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        // Поля спецификации правятся только там, где импорт их не прочитал:
        // перебивать данные, которые в файле есть, значит тихо разойтись
        // с проектом. Уже поправленную вручную ячейку править можно —
        // человек имеет право исправить свою же опечатку.
        var field = FieldOf(e.Column);
        if (field is not null && e.Row.Item is MeasurementRecord record)
        {
            var editable = record.IsFromSpecification &&
                           (record.SpecificationEditedManually ||
                            SpecificationManualEdit.IsUnread(record, field.Value));

            if (!editable)
            {
                e.Cancel = true;
                SetStatus(record.IsFromSpecification
                    ? $"Поле «{SpecificationManualEdit.ToRussian(field.Value)}» прочитано из спецификации — правка запрещена."
                    : "Строка не связана со спецификацией: править её поля незачем.");
                return;
            }
        }

        _watcher.Suspend();
    }

    /// <summary>Поле спецификации, которое правит эта колонка, либо null.</summary>
    private SpecificationField? FieldOf(DataGridColumn column)
    {
        if (ReferenceEquals(column, SpecificationMarkColumn)) return SpecificationField.Mark;
        if (ReferenceEquals(column, SpecificationCodeColumn)) return SpecificationField.EquipmentCode;
        if (ReferenceEquals(column, SpecificationManufacturerColumn)) return SpecificationField.Manufacturer;
        if (ReferenceEquals(column, SpecificationUnitColumn)) return SpecificationField.Unit;
        if (ReferenceEquals(column, SpecificationQuantityColumn)) return SpecificationField.Quantity;

        return null;
    }

    /// <summary>
    /// Применить правку ячейки.
    ///
    /// Привязки колонок односторонние, поэтому значение берётся из редактора
    /// и проводится через JournalEditService: только он знает, что смена
    /// материала или участка означает перенос геометрии на другой слой.
    /// </summary>
    private void JournalGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        try
        {
            if (e.EditAction != DataGridEditAction.Commit) return;
            if (e.Row.Item is not MeasurementRecord record) return;

            var edit = _session.JournalEdit;
            var text = ReadEditorText(e.EditingElement);

            // Поля спецификации меняются в самой записи и в позиции проекта:
            // геометрии они не касаются, поэтому JournalEditService здесь ни при чём.
            var field = FieldOf(e.Column);
            if (field is not null)
            {
                ApplySpecificationEdit(record, field.Value, text);
                return;
            }

            var result =
                ReferenceEquals(e.Column, MaterialColumn) ? edit.ChangeMaterial(record, text) :
                ReferenceEquals(e.Column, SectionColumn) ? edit.ChangeSection(record, text) :
                ReferenceEquals(e.Column, QuantityColumn)
                    ? (record.IsPiece ? edit.SetQuantity(record, text) : edit.SetLength(record, text)) :
                (JournalEditResult?)null;

            if (result is null) return;

            if (!result.Value.Success)
            {
                AcadUiHelper.ShowWarning(this, result.Value.Message);
                SetStatus($"Правка отклонена: {result.Value.Message.Split('\n')[0]}");
                return;
            }

            if (result.Value.Message.Length > 0)
            {
                SetStatus(result.Value.Message);
                UpdateJournalHeader();
                UpdateMaterialInfo();

                // Ручная правка меняет журнал, но не геометрию, поэтому скан
                // её не увидит — таблицы в чертеже обновляем сами.
                RefreshDrawingTables();
            }
        }
        catch (Exception ex)
        {
            AcadUiHelper.ShowError(this, $"Не удалось применить правку:\n{ex.Message}");
        }
        finally
        {
            // Возобновляем пересчёт в любом случае, иначе журнал «замёрзнет».
            _watcher.Resume();
        }
    }

    /// <summary>Текст из редактора ячейки — TextBox либо ComboBox.</summary>
    private static string ReadEditorText(FrameworkElement? editor) => editor switch
    {
        TextBox box => box.Text,
        ComboBox combo => combo.SelectedItem as string ?? combo.Text,
        _ => string.Empty
    };

    /// <summary>Журнал пересканирован автоматически — обновляем шапку и строку состояния.</summary>
    private void Watcher_Refreshed(object? sender, JournalRefreshedEventArgs e)
    {
        UpdateJournalHeader();
        UpdateMaterialInfo();
        RefreshDrawingTables();

        AutoRefreshText.Text = $"● автообновление: {e.Result.ToRussian()}";

        if (e.Result.Created > 0 || e.Result.Removed > 0)
            SetStatus($"Журнал обновлён автоматически: {e.Result.ToRussian()}.");
    }

    /// <summary>
    /// Перерисовать таблицы журнала в чертеже.
    ///
    /// Вызывается там же, где обновляется журнал, поэтому отдельного скана
    /// чертежа не происходит — таблицы просто перечитывают готовые записи.
    /// Если таблиц в чертеже нет или их удалили руками, метод молча ничего
    /// не делает: это штатная ситуация, а не ошибка.
    /// </summary>
    private void RefreshDrawingTables()
    {
        try
        {
            _session.Tables.Refresh();
        }
        catch (Exception ex)
        {
            // Обновление таблиц не должно ронять палитру и AutoCAD.
            SetStatus($"Таблицы в чертеже обновить не удалось: {ex.Message}");
        }
    }

    /// <summary>
    /// «Вставить таблицу замеров» — запуск команды вставки.
    ///
    /// Нужна точка на чертеже, а её нельзя запросить из обработчика кнопки
    /// палитры: указание точек живёт только в контексте команды.
    /// </summary>
    private void InsertTable_Click(object sender, RoutedEventArgs e)
    {
        Run("Вставить таблицу замеров", () =>
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc is null)
            {
                AcadUiHelper.ShowWarning(this, "Нет активного чертежа.");
                return;
            }

            _session.SyncCurrentDrawing();

            SetStatus("Укажи точку вставки таблиц в чертеже.");
            AcadUiHelper.FocusDrawingArea();
            doc.SendStringToExecute($"{MeasureCommands.TableCommandName} ", true, false, true);
        });
    }

    /// <summary>
    /// Реестр материалов изменился (добавили, удалили, перечитали) —
    /// журнал надо перепроверить, а выбранный материал мог исчезнуть.
    /// </summary>
    private void Materials_Changed(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            DropDeletedMaterialFromTools();
            RefreshMaterialColumnSource();
            UpdateMaterialInfo();
            _watcher.RequestRefresh();
        }));
    }

    /// <summary>
    /// Снять с инструментов материал, которого больше нет в реестре:
    /// иначе замер продолжил бы вестись по удалённой позиции.
    /// </summary>
    private void DropDeletedMaterialFromTools()
    {
        foreach (var tool in _session.AllTools)
        {
            var material = tool.CurrentMaterial;
            if (material is null) continue;
            if (_session.Materials.FindByName(material.Name) is not null) continue;

            tool.ClearMaterial();
        }
    }

    private void ReportMaterialsSource()
    {
        var repo = _session.Materials;
        if (!repo.IsLoaded)
        {
            SetStatus("Реестр материалов не загружен.");
            return;
        }

        if (repo.WasRecovered)
        {
            SetStatus(repo.RecoveryMessage!);
            return;
        }

        SetStatus($"Реестр материалов ({repo.SourceDescription}): {repo.LoadedFrom}. " +
                  $"Позиций: {repo.Materials.Count}.");
    }

    private void UpdateJournalHeader()
    {
        var current = _session.Journal.CurrentDrawingFileName;
        var total = _session.Journal.Records.Count;
        var inCurrent = _session.Journal.GetRecordsForDrawing(current).Count;

        // При включённом фильтре в заголовке сказано, что видно не всё:
        // иначе скрытые строки принимают за пропавшие.
        JournalHeaderText.Text = IsFilterActive
            ? $"Журнал замеров — показано {VisibleRecordCount()} из {total} (фильтр включён)"
            : $"Журнал замеров — записей всего: {total} (в текущем чертеже «{current}»: {inCurrent})";

        JournalHeaderText.Foreground = IsFilterActive
            ? System.Windows.Media.Brushes.SaddleBrown
            : System.Windows.Media.Brushes.Black;

        UpdateSpecificationHeader();
    }

    /// <summary>Обновить блок «выбран материал», имя слоя и вертикальные участки.</summary>
    private void UpdateMaterialInfo()
    {
        var tool = _session.ActiveTool;
        var material = tool.CurrentMaterial;

        if (material is null)
        {
            SelectedMaterialText.Text = "Материал не выбран";
            LayerInfoText.Text = "Нажми «Выбрать материал» — тип замера определится по классу материала.";
            VerticalTotalsText.Text = string.Empty;
            UpdateMeasureButtons(null);
            return;
        }

        SelectedMaterialText.Text = $"Выбран материал: {material.ClassRu} — {material.Name}";

        var layerName = _session.LayerService.GetLayerName(material, _session.Section);
        var colorIndex = LayerColorService.GetColorIndex(material);
        var characteristic = string.IsNullOrEmpty(material.Characteristic) ? "—" : material.Characteristic;

        // Показываем не только имя слоя, но и его фактическое состояние в чертеже:
        // инженеру важно видеть, что рисование пойдёт именно туда.
        string layerState;
        try
        {
            if (!_session.LayerService.LayerExists(layerName))
                layerState = "будет создан при замере";
            else
                layerState = _session.LayerService.IsCurrentLayer(layerName) ? "активный" : "создан, но не текущий";
        }
        catch (Exception)
        {
            layerState = "состояние неизвестно";
        }

        LayerInfoText.Text =
            $"Характеристика: {characteristic}   •   Слой: {layerName} ({layerState}, цвет {colorIndex})" +
            $"   •   Ед. изм.: {material.Unit}";

        UpdateMeasureButtons(material);

        if (material.Class == MaterialClasses.Piece)
        {
            VerticalTotalsText.Text =
                $"Штучные изделия: указывай центр каждого — плагин ставит круг " +
                $"⌀{PluginSettings.PieceMarkerDiameterMm:0} и считает их количество.";
            return;
        }

        var totals = _session.VerticalRuns.GetTotals(layerName, _session.Section);
        VerticalTotalsText.Text = totals.EntryCount == 0
            ? "Вертикальные участки: не заданы."
            : $"Вертикальные участки: подъёмы {totals.UpMm / 1000.0:N3} м, " +
              $"опуски {totals.DownMm / 1000.0:N3} м, итого {totals.TotalM:N3} м.";
    }

    /// <summary>
    /// Кнопки замера под класс материала: у штучных изделий считается
    /// количество, поэтому вертикальные участки к ним неприменимы.
    /// </summary>
    private void UpdateMeasureButtons(Material? material)
    {
        var isPiece = material?.Class == MaterialClasses.Piece;

        StartMeasureButton.Content = isPiece ? "Начать подсчет" : "Начать замер";
        StartMeasureButton.ToolTip = isPiece
            ? $"Делает слой материала текущим и включает расстановку кругов ⌀{PluginSettings.PieceMarkerDiameterMm:0}"
            : "Делает слой материала текущим и включает рисование полилиний";

        RiseButton.IsEnabled = !isPiece;
        DropButton.IsEnabled = !isPiece;
        if (_resetVerticalMenuItem is not null) _resetVerticalMenuItem.IsEnabled = !isPiece;
    }

    /// <summary>
    /// Переключатель подписей длины. Значение сохраняется между сеансами,
    /// на уже созданные подписи не влияет.
    /// </summary>
    private void ShowLengthLabels_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;

        Run("Показывать длину участков", () =>
        {
            var enabled = (sender as MenuItem)?.IsChecked == true;
            PluginSettings.ShowPolylineLengthLabels = enabled;

            SetStatus(enabled
                ? "Новые полилинии будут подписываться длиной."
                : "Новые полилинии создаются без подписи длины. Уже созданные подписи остались на месте.");
        });
    }

    private Material? RequireMaterial()
    {
        var material = _session.ActiveTool.CurrentMaterial;
        if (material is null)
        {
            AcadUiHelper.ShowWarning(this, "Сначала выбери материал.");
            SetStatus("Материал не выбран.");
        }

        return material;
    }

    // ======================= Материал и участок =======================

    private void SelectMaterial_Click(object sender, RoutedEventArgs e)
    {
        Run("Выбор материала", () =>
        {
            _session.EnsureMaterialsLoaded();

            var window = new MaterialPickerWindow(
                _session.Materials,
                _session.LayerService,
                _session.MaterialDeletion,
                _session.Section,
                _session.ActiveTool.CurrentMaterial);

            if (!AcadUiHelper.ShowDialogOverAcad(window) || window.SelectedMaterial is null)
            {
                SetStatus("Выбор материала отменён.");
                return;
            }

            var material = window.SelectedMaterial;

            // Инструмент подбирается по классу материала, слой создаётся
            // и сразу становится текущим — рисовать можно без лишних шагов.
            try
            {
                var layerName = _session.SelectMaterialAndActivateLayer(material);
                SetStatus($"Выбран материал: {material.Name}. Текущий слой: {layerName}.");
            }
            catch (Exception ex)
            {
                // Материал за инструментом всё равно закрепляем, иначе выбор
                // пропадёт впустую; слой создастся при «Начать замер».
                _session.GetToolFor(material.Class).SelectMaterial(material);
                AcadUiHelper.ShowWarning(this,
                    $"Материал выбран, но слой активировать не удалось:\n{ex.Message}\n\n" +
                    "Слой будет создан при нажатии «Начать замер».");
                SetStatus($"Выбран материал: {material.Name}. Слой не активирован.");
            }

            UpdateMaterialInfo();
            _watcher.RequestRefresh();
        });
    }

    private void SectionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;

        _session.Section = SectionBox.Text?.Trim() ?? string.Empty;

        // Участок входит в имя слоя, поэтому его смена меняет и слой,
        // и накопленные вертикальные участки.
        UpdateMaterialInfo();
    }

    // ======================= Замер =======================

    private void StartDrawing_Click(object sender, RoutedEventArgs e)
    {
        Run("Начать замер", () =>
        {
            var material = RequireMaterial();
            if (material is null) return;

            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc is null)
            {
                AcadUiHelper.ShowWarning(this, "Нет активного чертежа.");
                return;
            }

            _session.SyncCurrentDrawing();

            // Рисование требует контекста команды: указывать точки из обработчика
            // кнопки палитры нельзя, поэтому запускаем зарегистрированную команду.
            SetStatus(material.Class == MaterialClasses.Piece
                ? "Режим обводки: обводи изделия замкнутыми фигурами. Enter или Esc — выход."
                : "Режим рисования: указывай точки в чертеже. Enter или Esc — выход.");

            AcadUiHelper.FocusDrawingArea();
            doc.SendStringToExecute($"{MeasureCommands.DrawCommandName} ", true, false, true);
        });
    }

    private void Rise_Click(object sender, RoutedEventArgs e) => AddVerticalRun(isRise: true);

    private void Drop_Click(object sender, RoutedEventArgs e) => AddVerticalRun(isRise: false);

    private void AddVerticalRun(bool isRise)
    {
        Run(isRise ? "Подъём" : "Опуск", () =>
        {
            var material = RequireMaterial();
            if (material is null) return;

            if (material.Class == MaterialClasses.Piece)
            {
                AcadUiHelper.ShowWarning(this,
                    "У штучных изделий нет длины — вертикальные участки к ним не применяются.");
                return;
            }

            var layerName = _session.LayerService.GetLayerName(material, _session.Section);
            var section = _session.Section;
            var totals = _session.VerticalRuns.GetTotals(layerName, section);

            var window = new VerticalRunWindow(isRise, material.Name, layerName, section, totals);
            if (!AcadUiHelper.ShowDialogOverAcad(window))
            {
                SetStatus("Ввод вертикального участка отменён.");
                return;
            }

            var updated = _session.VerticalRuns.AddRun(layerName, section, window.LengthMm, isRise);
            UpdateMaterialInfo();

            // Вертикальные участки не рисуются, но входят в длину записи —
            // просим журнал пересобраться.
            _watcher.RequestRefresh();

            SetStatus($"{(isRise ? "Подъём" : "Опуск")} +{window.LengthMm / 1000.0:N3} м. " +
                      $"Итого вертикальных: {updated.TotalM:N3} м.");
        });
    }

    private void ResetVertical_Click(object sender, RoutedEventArgs e)
    {
        Run("Сброс вертикальных участков", () =>
        {
            var material = RequireMaterial();
            if (material is null) return;

            var layerName = _session.LayerService.GetLayerName(material, _session.Section);
            if (!AcadUiHelper.Confirm(
                    $"Обнулить вертикальные участки?\n\nСлой: {layerName}\nУчасток: " +
                    $"{(string.IsNullOrWhiteSpace(_session.Section) ? "— не задан —" : _session.Section)}"))
                return;

            _session.VerticalRuns.Reset(layerName, _session.Section);
            UpdateMaterialInfo();
            _watcher.RequestRefresh();
            SetStatus("Вертикальные участки обнулены.");
        });
    }

    // ======================= Журнал =======================

    /// <summary>
    /// «Очистить журнал» — сброс записей текущей сессии.
    ///
    /// Чертёж не трогается: полилинии, круги, подписи, слои, реестр материалов
    /// и выгруженные Excel-файлы остаются на месте. Автоведение журнала не
    /// отключается, поэтому по существующей геометрии записи соберутся заново
    /// при ближайшем пересчёте — об этом сказано прямо в подтверждении,
    /// иначе очистка выглядела бы сломанной.
    /// </summary>
    private void ClearJournal_Click(object sender, RoutedEventArgs e)
    {
        Run("Очистить журнал", () =>
        {
            var total = _session.Journal.Records.Count;
            if (total == 0)
            {
                SetStatus("Журнал и так пуст.");
                return;
            }

            var manual = _session.Journal.Records.Count(r => r.HasManualValue);
            var manualNote = manual == 0
                ? string.Empty
                : $"\n\nСреди них строк с ручными значениями: {manual}. Эти правки будут потеряны.";

            if (!AcadUiHelper.Confirm(
                    $"Очистить журнал?\n\nБудет удалено записей: {total} (по всем чертежам).{manualNote}\n\n" +
                    "Чертёж не изменится: полилинии, круги, подписи, слои,\n" +
                    "реестр материалов и выгруженные файлы Excel останутся на месте.\n\n" +
                    "Автоведение журнала продолжит работать, поэтому по существующей\n" +
                    "геометрии записи соберутся заново при ближайшем пересчёте."))
                return;

            _session.ClearJournal();

            UpdateJournalHeader();
            UpdateMaterialInfo();
            RefreshDrawingTables();
            AutoRefreshText.Text = string.Empty;
            SetStatus($"Журнал очищен: удалено записей {total}. Чертёж не изменён.");
        });
    }


    // ======================= Экспорт =======================

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        Run("Экспорт в Excel", () =>
        {
            _session.SyncCurrentDrawing();

            // Перед выгрузкой пересобираем журнал по чертежу:
            // иначе в Excel уедут длины по уже удалённой геометрии.
            var scan = _session.ScanDrawing();
            UpdateJournalHeader();

            // Ведомость строится по активному чертежу — как и таблица в нём.
            var drawing = _session.Journal.CurrentDrawingFileName;
            if (_session.Journal.GetRecordsForDrawing(drawing).Count == 0)
            {
                AcadUiHelper.ShowWarning(this,
                    $"По чертежу «{drawing}» нет записей журнала — экспортировать нечего.");
                SetStatus("Нет записей по текущему чертежу.");
                return;
            }

            // Со спецификацией состав книги настраивается: листы, строки
            // и столбцы свода. Без неё настраивать нечего — экспорт идёт сразу.
            SpecificationExportOptions? options = null;
            if (_session.HasSpecification)
            {
                var parameters = new SpecificationExportWindow(
                    SpecificationSummaryBuilder.GetDrawingColumns(_session.Journal));

                if (!AcadUiHelper.ShowDialogOverAcad(parameters))
                {
                    SetStatus("Экспорт отменён.");
                    return;
                }

                options = parameters.Options;
            }

            // Запасная папка — «Документы\PTO Measure Pro», а не папка плагина:
            // bundle заменяется при обновлении, и выгрузки из него исчезли бы.
            var path = ExcelExportService.BuildExportPath(
                AcadWorkspace.GetCurrentDrawingFullPath(),
                PluginPaths.ExportFallbackDirectory);

            _session.ExcelExport.Export(_session.Journal, drawing, path, _session.Specification, options);

            SetStatus($"Экспорт готов: {path}. Перед выгрузкой: {scan.ToRussian()}.");

            // Окно немодальное: из него открывают книгу и возвращаются
            // к чертежу, не закрывая его.
            AcadUiHelper.ShowOverAcad(new ExportResultWindow(
                path,
                $"Перед выгрузкой журнал обновлён: {scan.ToRussian()}."));
        });
    }

    /// <summary>Применить ручную правку поля спецификации и записать её в журнал событий.</summary>
    private void ApplySpecificationEdit(MeasurementRecord record, SpecificationField field, string text)
    {
        try
        {
            var log = SpecificationManualEdit.Apply(record, _session.Specification, field, text);
            if (log is null) return;

            WriteToCommandLine(new[] { log });
            SetStatus(log);

            // Правка меняет журнал, но не геометрию — таблицы в чертеже
            // обновляем сами, как и при других ручных правках.
            RefreshDrawingTables();
        }
        catch (InvalidOperationException ex)
        {
            AcadUiHelper.ShowWarning(this, ex.Message);
            SetStatus($"Правка отклонена: {ex.Message}");
        }
    }

    // ======================= Столбцы таблицы =======================

    /// <summary>Столбцы, видимостью которых управляет пользователь.</summary>
    private IReadOnlyList<(string Title, DataGridColumn Column)> OptionalColumns => new[]
    {
        ("п/п", (DataGridColumn)SpecificationNumberColumn),
        ("Кол-во (спец.)", SpecificationQuantityColumn),
        ("Расхождение", SpecificationDifferenceColumn),
        ("Марка", SpecificationMarkColumn),
        ("Код оборудования", SpecificationCodeColumn),
        ("Изготовитель", SpecificationManufacturerColumn),
        ("Ед. изм. (спец.)", SpecificationUnitColumn),
        ("Участок", SectionColumn),
        ("Слой", LayerColumn),
        ("Файл DWG", DrawingColumn)
    };

    /// <summary>
    /// Столбцы, показанные по умолчанию.
    ///
    /// Без спецификации журнал ведётся по чертежу, и важны слой, участок и DWG.
    /// Со спецификацией на первый план выходит сверка с проектом: номер позиции,
    /// проектное количество и расхождение, а служебные столбцы чертежа
    /// уходят в «Настроить столбцы». Марка, код и изготовитель скрыты всегда:
    /// они справочные и нужны раз в сто замеров.
    /// </summary>
    private IReadOnlyCollection<string> DefaultVisibleColumns => _session.HasSpecification
        ? new[] { "п/п", "Кол-во (спец.)", "Расхождение", "Участок" }
        : new[] { "Участок", "Слой", "Файл DWG" };

    private void ConfigureSpecificationColumns_Click(object sender, RoutedEventArgs e)
    {
        Run("Настройка столбцов", () =>
        {
            var window = new ColumnVisibilityWindow(
                OptionalColumns.Select(c => (c.Title, c.Column.Visibility == Visibility.Visible)));

            if (!AcadUiHelper.ShowDialogOverAcad(window)) return;

            foreach (var (title, visible) in window.ColumnVisibility)
            {
                _session.SpecificationColumnVisibility[title] = visible;

                var column = OptionalColumns.FirstOrDefault(c => c.Title == title).Column;
                if (column is not null)
                    column.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }

            var hidden = window.ColumnVisibility.Count(v => !v.Value);
            SetStatus(hidden == 0
                ? "Показаны все столбцы таблицы."
                : $"Скрыто столбцов: {hidden}. На выгрузку в Excel это не влияет.");
        });
    }

    /// <summary>
    /// Расставить видимость столбцов: сначала значения по умолчанию для текущего
    /// режима работы, поверх — то, что пользователь выбрал в этой сессии.
    /// Настройка живёт в сессии, а не в файле: она про текущую работу,
    /// а не про постоянные предпочтения.
    /// </summary>
    private void ApplySpecificationColumnVisibility()
    {
        foreach (var (title, column) in OptionalColumns)
        {
            var visible = _session.SpecificationColumnVisibility.TryGetValue(title, out var stored)
                ? stored
                : DefaultVisibleColumns.Contains(title);

            column.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ======================= Действия над записью журнала =======================

    /// <summary>Запись под курсором, либо null с объяснением в строке состояния.</summary>
    private MeasurementRecord? CurrentRecord()
    {
        if (JournalGrid.CurrentItem is MeasurementRecord record) return record;

        SetStatus("Сначала выбери строку журнала.");
        return null;
    }

    private void ShowInDrawing_Click(object sender, RoutedEventArgs e)
    {
        Run("Показать в чертеже", () =>
        {
            var record = CurrentRecord();
            if (record is null) return;

            if (string.IsNullOrWhiteSpace(record.LayerName))
            {
                AcadUiHelper.ShowInfo(this, "У строки нет слоя: она заведена из спецификации и ещё не замерена.");
                return;
            }

            var selected = _session.Workspace.SelectLayerEntities(record.LayerName);
            if (selected == 0)
            {
                SetStatus($"На слое «{record.LayerName}» ничего не выбрано: он пуст, выключен или заморожен.");
                return;
            }

            AcadUiHelper.FocusDrawingArea();
            SetStatus($"Выбрано объектов на слое «{record.LayerName}»: {selected}.");
        });
    }

    private void EditSpecificationRow_Click(object sender, RoutedEventArgs e)
    {
        Run("Правка данных спецификации", () =>
        {
            var record = CurrentRecord();
            if (record is null) return;

            if (!record.IsFromSpecification)
            {
                AcadUiHelper.ShowInfo(this,
                    "Строка не связана со спецификацией — править в ней нечего.\n\n" +
                    "Привяжи её к позиции проекта через контекстное меню.");
                return;
            }

            // Ставим курсор в первую ячейку, которую разрешено править:
            // искать её глазами среди скрытых столбцов — занятие на любителя.
            var target = OptionalColumns
                .Select(c => c.Column)
                .FirstOrDefault(column =>
                {
                    var field = FieldOf(column);
                    return field is not null &&
                           column.Visibility == Visibility.Visible &&
                           (record.SpecificationEditedManually ||
                            SpecificationManualEdit.IsUnread(record, field.Value));
                });

            if (target is null)
            {
                AcadUiHelper.ShowInfo(this,
                    "Все поля спецификации в этой строке прочитаны из файла — править их нельзя.\n\n" +
                    "Правка нужна там, где импорт не смог разобрать данные.");
                return;
            }

            JournalGrid.CurrentCell = new DataGridCellInfo(record, target);
            JournalGrid.BeginEdit();
        });
    }

    private void BindToSpecification_Click(object sender, RoutedEventArgs e)
    {
        Run("Привязка к спецификации", () =>
        {
            var record = CurrentRecord();
            if (record is null) return;

            var specification = _session.Specification;
            if (specification is null)
            {
                AcadUiHelper.ShowInfo(this, "Спецификация не загружена — привязывать не к чему.");
                return;
            }

            var window = new SpecificationPickWindow(specification);
            if (!AcadUiHelper.ShowDialogOverAcad(window) || window.SelectedItem is null) return;

            MeasurementJournal.BindToSpecification(record, window.SelectedItem, specification.FileName);
            ApplySpecificationColumnVisibility();

            var log = $"Запись «{record.MaterialName}» привязана к позиции п/п {window.SelectedItem.Number}";
            WriteToCommandLine(new[] { log });
            SetStatus(log + ".");
        });
    }

    private void UnbindFromSpecification_Click(object sender, RoutedEventArgs e)
    {
        Run("Отвязка от спецификации", () =>
        {
            var record = CurrentRecord();
            if (record is null) return;

            if (!record.IsFromSpecification)
            {
                SetStatus("Строка и так не связана со спецификацией.");
                return;
            }

            var number = record.SpecificationItemId;
            record.ClearSpecificationBinding();

            // Замер остаётся: он сделан по чертежу и от потери привязки
            // верным быть не перестаёт.
            var log = $"Запись «{record.MaterialName}» отвязана от позиции п/п {number}";
            WriteToCommandLine(new[] { log });
            SetStatus(log + ". Замер сохранён.");
        });
    }

    private void DeleteRecord_Click(object sender, RoutedEventArgs e)
    {
        Run("Удаление записи", () =>
        {
            var record = CurrentRecord();
            if (record is null) return;

            if (!AcadUiHelper.Confirm(
                    $"Удалить из журнала запись «{record.MaterialName}»" +
                    (string.IsNullOrWhiteSpace(record.Section) ? string.Empty : $", участок «{record.Section}»") +
                    "?\n\nГеометрия в чертеже останется на месте — удаляется только строка журнала. " +
                    "Если под ней есть полилинии, запись вернётся при ближайшем пересчёте."))
                return;

            _session.JournalService.RemoveRecord(record);

            SetStatus($"Запись «{record.MaterialName}» удалена из журнала.");
            UpdateJournalHeader();
            RefreshDrawingTables();
        });
    }

    // ======================= Второстепенные действия =======================

    /// <summary>
    /// Меню второстепенных действий. Собирается в коде, а не в разметке:
    /// обработчики событий внутри ресурсов и стилей WPF привязать не может,
    /// и палитра падала бы при загрузке.
    /// </summary>
    private ContextMenu BuildMoreMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(MenuItemWith("Вставить таблицу замеров в чертёж", InsertTable_Click));

        _showLabelsMenuItem = new MenuItem { Header = "Подписывать длину новых полилиний", IsCheckable = true };
        _showLabelsMenuItem.Checked += ShowLengthLabels_Changed;
        _showLabelsMenuItem.Unchecked += ShowLengthLabels_Changed;
        menu.Items.Add(_showLabelsMenuItem);

        _resetVerticalMenuItem = MenuItemWith("Сбросить вертикальные участки", ResetVertical_Click);
        menu.Items.Add(_resetVerticalMenuItem);

        menu.Items.Add(new Separator());

        _onlyMeasurementLayersMenuItem = new MenuItem
        {
            Header = "Показать только замерные слои",
            IsCheckable = true,
            ToolTip = "Гасит проектные слои чертежа и показывает все слои плагина. Снятие галочки возвращает исходную видимость"
        };
        _onlyMeasurementLayersMenuItem.Checked += OnlyMeasurementLayers_Changed;
        _onlyMeasurementLayersMenuItem.Unchecked += OnlyMeasurementLayers_Changed;
        menu.Items.Add(_onlyMeasurementLayersMenuItem);

        _onlyCurrentLayerMenuItem = new MenuItem
        {
            Header = "Показать только слой текущего замера",
            IsCheckable = true,
            ToolTip = "Гасит остальные замерные слои. Проектные слои не трогает — за них отвечает галочка выше"
        };
        _onlyCurrentLayerMenuItem.Checked += OnlyCurrentLayer_Changed;
        _onlyCurrentLayerMenuItem.Unchecked += OnlyCurrentLayer_Changed;
        menu.Items.Add(_onlyCurrentLayerMenuItem);

        menu.Items.Add(new Separator());
        _deleteSpecificationMenuItem = MenuItemWith("Удалить спецификацию", DeleteSpecification_Click);
        _deleteSpecificationMenuItem.IsEnabled = _session.HasSpecification;
        menu.Items.Add(_deleteSpecificationMenuItem);

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemWith("Настроить столбцы таблицы", ConfigureSpecificationColumns_Click));
        menu.Items.Add(MenuItemWith("Очистить журнал", ClearJournal_Click));

        menu.Items.Add(new Separator());
        var diagnostics = new MenuItem { Header = "Диагностика" };
        diagnostics.Items.Add(MenuItemWith("Реестр материалов и пути", ShowDiagnostics_Click));
        diagnostics.Items.Add(MenuItemWith("Пересчитать журнал по чертежу", RescanJournal_Click));
        menu.Items.Add(diagnostics);

        return menu;
    }

    /// <summary>Меню заголовков таблицы: по правой кнопке — настройка столбцов.</summary>
    private void ApplyHeaderContextMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItemWith("Настроить столбцы таблицы", ConfigureSpecificationColumns_Click));

        var style = new Style(typeof(DataGridColumnHeader));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(4, 3, 4, 3)));
        style.Setters.Add(new Setter(ContextMenuProperty, menu));

        JournalGrid.ColumnHeaderStyle = style;
    }

    private static MenuItem MenuItemWith(string header, RoutedEventHandler handler)
    {
        var item = new MenuItem { Header = header };
        item.Click += handler;
        return item;
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        // Состояние переключателя подписей берём из настроек: меню создаётся
        // один раз, а настройку могли поменять командой.
        if (_showLabelsMenuItem is not null)
        {
            _suppressEvents = true;
            _showLabelsMenuItem.IsChecked = PluginSettings.ShowPolylineLengthLabels;
            _suppressEvents = false;
        }

        // Меню открывается у кнопки, а не у курсора: так оно ведёт себя
        // предсказуемо и не выпрыгивает за край палитры.
        _moreMenu.PlacementTarget = MoreButton;
        _moreMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        _moreMenu.IsOpen = true;
    }

    private void ShowDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        Run("Диагностика", () =>
        {
            var repository = _session.Materials;
            var specification = _session.Specification;

            AcadUiHelper.ShowInfo(this,
                $"Версия: {PluginSettings.PluginVersion}\n" +
                $"Пакет: {PluginPaths.PluginDirectory}\n" +
                $"Данные пользователя: {PluginPaths.UserDataDirectory}\n\n" +
                $"Реестр материалов ({repository.SourceDescription}): {repository.LoadedFrom}\n" +
                $"Позиций в реестре: {repository.Materials.Count}\n\n" +
                $"Спецификация: {(specification is null ? "не загружена" : $"{specification.FileName}, позиций {specification.Items.Count}")}\n" +
                $"Записей журнала: {_session.Journal.Records.Count}\n" +
                $"Текущий чертёж: {_session.Journal.CurrentDrawingFileName}");
        });
    }

    private void RescanJournal_Click(object sender, RoutedEventArgs e)
    {
        Run("Пересчёт журнала", () =>
        {
            _session.SyncCurrentDrawing();
            var scan = _session.ScanDrawing();

            UpdateJournalHeader();
            SetStatus($"Журнал пересчитан: {scan.ToRussian()}.");
        });
    }

    // ======================= Первоначальная спецификация =======================

    /// <summary>
    /// Единственная команда загрузки. Со второго раза она же заменяет текущую
    /// спецификацию: отдельной кнопки «перезагрузить» нет — пользователю
    /// незачем помнить, загружена спецификация или нет.
    /// </summary>
    private void LoadSpecification_Click(object sender, RoutedEventArgs e)
    {
        if (_session.HasSpecification)
        {
            if (!AcadUiHelper.Confirm(
                    "Загрузить новую спецификацию вместо текущей? " +
                    "Текущие привязки записей к спецификации будут сняты, " +
                    "результаты замеров останутся в журнале."))
            {
                SetStatus("Загрузка спецификации отменена — текущая осталась на месте.");
                return;
            }

            // Старые номера позиций указывали бы не на те строки нового файла,
            // поэтому привязки снимаются до импорта.
            var (previousFile, unbound) = _session.ClearSpecification();
            WriteToCommandLine(new[]
            {
                $"Спецификация удалена: {previousFile}; отвязано записей: {unbound}"
            });
        }

        ImportSpecification();
    }

    private void DeleteSpecification_Click(object sender, RoutedEventArgs e)
    {
        Run("Удаление спецификации", () =>
        {
            if (!_session.HasSpecification)
            {
                SetStatus("Спецификация не загружена — удалять нечего.");
                return;
            }

            if (!AcadUiHelper.Confirm(
                    "Удалить текущую спецификацию? Позиции и результаты замеров останутся " +
                    "в журнале, но связь со спецификацией будет снята."))
            {
                SetStatus("Удаление спецификации отменено.");
                return;
            }

            var (fileName, unbound) = _session.ClearSpecification();

            // Журнал возвращается к обычному виду: столбцы проекта уходят,
            // столбцы чертежа возвращаются.
            ApplySpecificationColumnVisibility();
            UpdateSpecificationHeader();
            UpdateJournalHeader();

            var log = $"Спецификация удалена: {fileName}; отвязано записей: {unbound}";
            WriteToCommandLine(new[] { log });
            SetStatus($"{log}. Замеры, материалы и слои остались на месте.");
        });
    }

    private void ImportSpecification()
    {
        Run("Загрузка спецификации", () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Первоначальная спецификация",
                Filter = "Книга Excel (*.xlsx)|*.xlsx|Все файлы (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != true) return;

            var specification = SpecificationImporter.Import(dialog.FileName);
            if (specification.Items.Count == 0)
            {
                AcadUiHelper.ShowWarning(this,
                    "В файле не нашлось ни одной позиции.\n\n" +
                    "Ожидаемый порядок колонок: п/п, наименование, марка, код оборудования, " +
                    "изготовитель, единица измерения, количество.");
                SetStatus("Спецификация не загружена: позиций не найдено.");
                return;
            }

            // Заводит недостающие материалы в реестре и пересобирает привязки
            // журнала; что именно произошло — уходит в командную строку AutoCAD.
            var result = _session.LoadSpecification(specification);
            WriteToCommandLine(result.Log);

            ShowSpecificationColumns();
            RefreshMaterialColumnSource();
            // Список позиций для фильтра берётся из сессии в момент вызова —
            // заранее наполнять нечего.

            if (result.MaterialsCreated > 0 || result.MaterialsSkipped > 0 || result.RecordsUnbound > 0)
            {
                AcadUiHelper.ShowInfo(this,
                    (result.MaterialsCreated > 0 ? $"Заведено материалов в реестре: {result.MaterialsCreated}.\n" : string.Empty) +
                    (result.MaterialsSkipped > 0 ? $"Пропущено позиций с нераспознанной единицей: {result.MaterialsSkipped}.\n" : string.Empty) +
                    (result.RecordsRebound > 0 ? $"Записей журнала перепривязано: {result.RecordsRebound}.\n" : string.Empty) +
                    (result.RecordsUnbound > 0 ? $"Записей осталось без привязки: {result.RecordsUnbound} — их позиций нет в новом файле.\n" : string.Empty));
            }

            // Какие позиции взять в работу, решает пользователь: спецификация
            // на сотни строк превратила бы журнал в свалку, если тащить всё
            // без спроса. Сама спецификация при этом остаётся целой — по ней
            // строится свод в Excel.
            // Владельца окну назначает ShowDialogOverAcad по хэндлу главного
            // окна AutoCAD: палитра не является WPF-окном, и Window.GetWindow
            // для неё владельца не даёт.
            var selection = new SpecificationImportWindow(
                specification,
                item => _session.FindMaterialFor(item) is not null);

            if (AcadUiHelper.ShowDialogOverAcad(selection))
            {
                var added = _session.AddSpecificationItemsToJournal(selection.SelectedItems);
                SetStatus(
                    $"Спецификация «{specification.FileName}»: в журнал добавлено позиций — {added} " +
                    $"из {specification.Items.Count}. Спецификация действует до закрытия AutoCAD.");
            }
            else
            {
                SetStatus(
                    $"Спецификация «{specification.FileName}» загружена: позиций {specification.Items.Count}. " +
                    "Записи в журнал не добавлены; замеры будут привязываться к ней по наименованию материала.");
            }

            ApplyJournalFilter();
            UpdateJournalHeader();
        });
    }

    /// <summary>
    /// Вывести события в командную строку AutoCAD.
    ///
    /// Отдельного лога у плагина нет, и заводить его ради нескольких строк
    /// незачем: командная строка — то место, куда пользователь смотрит
    /// после команды, и она сохраняет историю сессии.
    /// </summary>
    private static void WriteToCommandLine(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;

        try
        {
            var editor = AcadApp.DocumentManager.MdiActiveDocument?.Editor;
            if (editor is null) return;

            foreach (var line in lines) editor.WriteMessage($"\n{line}");
            editor.WriteMessage("\n");
        }
        catch
        {
            // Вывод в командную строку — вспомогательный: если документа нет
            // или он занят, импорт из-за этого падать не должен.
        }
    }

    /// <summary>
    /// Обновить шапку блока спецификации: загружена ли она, из какого файла
    /// и сколько в ней позиций. Путь к файлу тут не показывается — он длинный
    /// и в работе не нужен; полный путь есть в «Диагностике».
    /// </summary>
    private void UpdateSpecificationHeader()
    {
        var specification = _session.Specification;

        if (specification is null)
        {
            SpecificationStatusText.Text = "Не загружена — журнал ведётся только по чертежу";
            LoadSpecificationButton.Content = "Загрузить";
            if (_deleteSpecificationMenuItem is not null) _deleteSpecificationMenuItem.IsEnabled = false;
            return;
        }

        var measured = _session.Journal.Records.Count(r => r.IsFromSpecification);
        SpecificationStatusText.Text =
            $"{specification.FileName} — позиций {specification.Items.Count}, в журнале {measured}";

        // Со второго раза та же кнопка заменяет спецификацию — и говорит об этом.
        LoadSpecificationButton.Content = "Заменить";
        if (_deleteSpecificationMenuItem is not null) _deleteSpecificationMenuItem.IsEnabled = true;
    }

    /// <summary>
    /// Показать колонки спецификации. До загрузки они скрыты: в проектах,
    /// где спецификации нет, это были бы пять пустых столбцов.
    /// </summary>
    private void ShowSpecificationColumns() => ApplySpecificationColumnVisibility();

    // ======================= Слои =======================

    private void IsolateRecordLayer_Click(object sender, RoutedEventArgs e)
    {
        Run("Показать только этот замерный слой", () =>
        {
            var record = CurrentRecord();
            if (record is null) return;

            if (string.IsNullOrWhiteSpace(record.LayerName))
            {
                AcadUiHelper.ShowInfo(this,
                    "У строки нет слоя: она заведена из спецификации и ещё не замерена.");
                return;
            }

            // Изоляция по строке — тот же режим, что и галочка «только слой
            // текущего замера», просто слой берётся из записи. Поэтому она
            // и отмечается: снятие галочки вернёт остальные слои.
            var visibility = _session.LayerVisibility;

            var plan = MeasurementLayerVisibility.PlanEnableCurrentLayerOnly(
                visibility.GetLayerNames(),
                visibility.GetHiddenLayerNames(),
                _session.LayerNames,
                record.LayerName);

            if (plan.IsEmpty)
            {
                SetStatus($"Слоя «{record.LayerName}» в этом чертеже нет.");
                return;
            }

            var result = visibility.Apply(plan);
            WriteToCommandLine(result.Log);

            _hiddenByCurrentLayerOnly.Clear();
            _hiddenByCurrentLayerOnly.AddRange(plan.TurnOff);

            if (_onlyCurrentLayerMenuItem is not null)
            {
                _suppressEvents = true;
                _onlyCurrentLayerMenuItem.IsChecked = true;
                _suppressEvents = false;
            }

            SetStatus($"Показан только слой «{record.LayerName}». Выключено замерных слоёв: {result.TurnedOff}. " +
                      "Вернуть остальные — снять галочку «Показать только слой текущего замера» в меню «Ещё».");
        });
    }

    // Слои, погашенные каждым из режимов. Возврат идёт ровно по этим спискам:
    // слой, выключенный пользователем до включения режима, обязан остаться
    // выключенным и после выхода.
    private readonly List<string> _hiddenByMeasurementOnly = new();
    private readonly List<string> _hiddenByCurrentLayerOnly = new();

    /// <summary>
    /// Режим «только замерные слои». Отвечает за ПРОЕКТНЫЕ слои: гасит их
    /// при включении и возвращает при снятии. Замерные слои не трогает —
    /// ими распоряжается вторая галочка, поэтому режимы независимы.
    /// </summary>
    private void OnlyMeasurementLayers_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _onlyMeasurementLayersMenuItem is null) return;

        var enabled = _onlyMeasurementLayersMenuItem.IsChecked;

        Run("Показать только замерные слои", () =>
        {
            if (!enabled)
            {
                var restore = MeasurementLayerVisibility.PlanDisableMode(_hiddenByMeasurementOnly);
                var restored = _session.LayerVisibility.Apply(restore);
                WriteToCommandLine(restored.Log);

                SetStatus($"Проектные слои возвращены: {restored.TurnedOn}.");
                _hiddenByMeasurementOnly.Clear();
                return;
            }

            var visibility = _session.LayerVisibility;
            var all = visibility.GetLayerNames();

            var measurement = MeasurementLayerVisibility.SelectMeasurementLayers(all, _session.LayerNames);
            if (measurement.Count == 0)
            {
                // Гасить весь чертёж ради пустого результата нельзя.
                _suppressEvents = true;
                _onlyMeasurementLayersMenuItem.IsChecked = false;
                _suppressEvents = false;

                AcadUiHelper.ShowWarning(this,
                    "В чертеже нет ни одного замерного слоя.\nСначала выполни замер.");
                return;
            }

            var plan = MeasurementLayerVisibility.PlanEnableOnlyMeasurement(
                all, visibility.GetHiddenLayerNames(), _session.LayerNames);

            // Замерные слои включает только этот режим и только когда вторая
            // галочка не изолирует один слой: иначе он бы её отменял.
            if (_onlyCurrentLayerMenuItem?.IsChecked == true)
                plan = new LayerVisibilityPlan(Array.Empty<string>(), plan.TurnOff);

            var result = _session.LayerVisibility.Apply(plan);
            WriteToCommandLine(result.Log);

            _hiddenByMeasurementOnly.Clear();
            _hiddenByMeasurementOnly.AddRange(plan.TurnOff);

            SetStatus($"Показаны только замерные слои. Выключено проектных: {result.TurnedOff}. " +
                      "Снять галочку — вернуть их обратно.");
        });
    }

    /// <summary>
    /// Режим «только слой текущего замера». Отвечает за ЗАМЕРНЫЕ слои: гасит
    /// все, кроме слоя выбранного материала и участка, и возвращает их при
    /// снятии. Проектные слои не трогает, поэтому режим работает и поверх
    /// галочки «только замерные слои», и сам по себе.
    /// </summary>
    private void OnlyCurrentLayer_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _onlyCurrentLayerMenuItem is null) return;

        var enabled = _onlyCurrentLayerMenuItem.IsChecked;

        Run("Показать только слой текущего замера", () =>
        {
            if (!enabled)
            {
                var restore = MeasurementLayerVisibility.PlanDisableMode(_hiddenByCurrentLayerOnly);
                var restored = _session.LayerVisibility.Apply(restore);
                WriteToCommandLine(restored.Log);

                SetStatus($"Замерные слои возвращены: {restored.TurnedOn}.");
                _hiddenByCurrentLayerOnly.Clear();
                return;
            }

            var material = _session.ActiveTool.CurrentMaterial;
            if (material is null)
            {
                _suppressEvents = true;
                _onlyCurrentLayerMenuItem.IsChecked = false;
                _suppressEvents = false;

                AcadUiHelper.ShowInfo(this, "Выберите материал или позицию для изоляции слоя.");
                SetStatus("Материал не выбран — изолировать нечего.");
                return;
            }

            var visibility = _session.LayerVisibility;
            var layerName = _session.LayerNames.GetLayerName(material, _session.Section);

            var plan = MeasurementLayerVisibility.PlanEnableCurrentLayerOnly(
                visibility.GetLayerNames(), visibility.GetHiddenLayerNames(), _session.LayerNames, layerName);

            if (plan.IsEmpty)
            {
                _suppressEvents = true;
                _onlyCurrentLayerMenuItem.IsChecked = false;
                _suppressEvents = false;

                SetStatus($"Слоя «{layerName}» в этом чертеже ещё нет — по нему не было замеров.");
                return;
            }

            var result = visibility.Apply(plan);
            WriteToCommandLine(result.Log);

            _hiddenByCurrentLayerOnly.Clear();
            _hiddenByCurrentLayerOnly.AddRange(plan.TurnOff);

            SetStatus($"Показан только слой «{layerName}». Выключено замерных слоёв: {result.TurnedOff}. " +
                      "Снять галочку — вернуть остальные.");
        });
    }

}
