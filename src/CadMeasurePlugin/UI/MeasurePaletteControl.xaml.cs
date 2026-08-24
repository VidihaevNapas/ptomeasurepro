using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
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
        ShowLengthLabelsCheck.IsChecked = PluginSettings.ShowPolylineLengthLabels;

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
    private void RefreshMaterialColumnSource() =>
        MaterialColumn.ItemsSource = _session.JournalEdit.GetMaterialNames();

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
                AcadUiHelper.ShowWarning(this,
                    $"Материала «{record.MaterialName}» больше нет в реестре.");
                return;
            }

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
    private void JournalGrid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e) => _watcher.Suspend();

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

        SetStatus($"Реестр материалов ({repo.SourceDescription}): {repo.LoadedFrom}. " +
                  $"Позиций: {repo.Materials.Count}.");
    }

    private void UpdateJournalHeader()
    {
        var current = _session.Journal.CurrentDrawingFileName;
        var total = _session.Journal.Records.Count;
        var inCurrent = _session.Journal.GetRecordsForDrawing(current).Count;

        JournalHeaderText.Text =
            $"Журнал замеров — записей всего: {total} (в текущем чертеже «{current}»: {inCurrent})";

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
        ResetVerticalButton.IsEnabled = !isPiece;
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
            var enabled = ShowLengthLabelsCheck.IsChecked == true;
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

            // Запасная папка — «Документы\PTO Measure Pro», а не папка плагина:
            // bundle заменяется при обновлении, и выгрузки из него исчезли бы.
            var path = ExcelExportService.BuildExportPath(
                AcadWorkspace.GetCurrentDrawingFullPath(),
                PluginPaths.ExportFallbackDirectory);

            _session.ExcelExport.Export(_session.Journal, drawing, path);

            SetStatus($"Экспорт готов: {path}. Перед выгрузкой: {scan.ToRussian()}.");
            AcadUiHelper.ShowInfo(this, $"Файл создан:\n{path}\n\nПеред выгрузкой журнал обновлён: {scan.ToRussian()}.");
        });
    }

    // ======================= Слои =======================

    private void IsolateLayers_Click(object sender, RoutedEventArgs e)
    {
        Run("Показать только слои замеров", () =>
        {
            _session.SyncCurrentDrawing();

            var layers = _session.Journal.GetUsedLayerNames(_session.Journal.CurrentDrawingFileName);
            if (layers.Count == 0)
            {
                AcadUiHelper.ShowWarning(this,
                    "В журнале текущего чертежа нет ни одного слоя замеров.\n" +
                    "Сначала выполни замер.");
                return;
            }

            var turnedOff = _session.LayerVisibility.ShowOnlyMeasurementLayers(layers);
            SetStatus($"Оставлено видимыми слоёв замеров: {layers.Count}. Выключено прочих слоёв: {turnedOff}.");
        });
    }

    private void RestoreLayers_Click(object sender, RoutedEventArgs e)
    {
        Run("Показать все слои", () =>
        {
            _session.LayerVisibility.RestoreAllLayers();
            SetStatus("Исходная видимость слоёв восстановлена.");
        });
    }

}
