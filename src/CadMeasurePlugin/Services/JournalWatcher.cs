using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace CadMeasurePlugin.Services;

/// <summary>
/// Автообновление журнала: следит за изменениями чертежа и пересчитывает
/// записи, удаляя те, чьи слои опустели.
///
/// Ловим завершение любой команды AutoCAD (CommandEnded / Cancelled / Failed).
/// Через команды проходит всё, что нас интересует: рисование, стирание,
/// СВОЙСТВА со сменой слоя, ОТМЕНИТЬ. Слушать ObjectErased на базе было бы
/// в разы дороже — событие приходит на каждый объект.
///
/// Пересчёт не выполняется прямо в обработчике: команда в этот момент ещё
/// сворачивается, и работа с базой оттуда ненадёжна. Вместо этого взводится
/// таймер на четверть секунды — он же склеивает пачку событий (например,
/// серию ПЛИНИЙ) в один пересчёт.
/// </summary>
public sealed class JournalWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private readonly MeasurementJournalService _journalService;
    private readonly DispatcherTimer _timer;

    private Document? _hookedDocument;
    private bool _running;
    private bool _refreshing;
    private bool _suspended;
    private bool _pendingWhileSuspended;
    private bool _disposed;

    public JournalWatcher(MeasurementJournalService journalService, Dispatcher dispatcher)
    {
        _journalService = journalService ?? throw new ArgumentNullException(nameof(journalService));

        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = Debounce };
        _timer.Tick += OnTimerTick;
    }

    /// <summary>Журнал пересчитан. В аргументах — сколько записей обновлено и удалено.</summary>
    public event EventHandler<JournalRefreshedEventArgs>? Refreshed;

    /// <summary>Начать следить за чертежами.</summary>
    public void Start()
    {
        if (_running || _disposed) return;
        _running = true;

        AcadApp.DocumentManager.DocumentActivated += OnDocumentActivated;
        AcadApp.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;

        HookDocument(AcadApp.DocumentManager.MdiActiveDocument);
        RequestRefresh();
    }

    /// <summary>
    /// Приостановить пересчёт на время правки ячейки журнала.
    ///
    /// Пересчёт пересоздаёт и удаляет строки коллекции; если он сработает,
    /// пока открыт редактор ячейки, правка потеряется, а DataGrid может
    /// остаться в несогласованном состоянии.
    /// </summary>
    public void Suspend()
    {
        _suspended = true;
        _timer.Stop();
    }

    /// <summary>
    /// Возобновить пересчёт. Если во время паузы приходили события чертежа,
    /// пересчёт запускается сразу — иначе журнал остался бы устаревшим.
    /// </summary>
    public void Resume()
    {
        if (!_suspended) return;
        _suspended = false;

        if (!_pendingWhileSuspended) return;

        _pendingWhileSuspended = false;
        RequestRefresh();
    }

    /// <summary>Перестать следить.</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;

        AcadApp.DocumentManager.DocumentActivated -= OnDocumentActivated;
        AcadApp.DocumentManager.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;

        HookDocument(null);
        _timer.Stop();
    }

    /// <summary>
    /// Запросить пересчёт вручную — после изменения журнала или реестра материалов.
    /// Несколько запросов подряд склеиваются в один.
    /// </summary>
    public void RequestRefresh()
    {
        if (_disposed) return;

        // Во время правки ячейки только запоминаем, что пересчёт нужен:
        // выполним его сразу после Resume.
        if (_suspended)
        {
            _pendingWhileSuspended = true;
            return;
        }

        _timer.Stop();
        _timer.Start();
    }

    // ======================= События AutoCAD =======================

    private void OnDocumentActivated(object? sender, DocumentCollectionEventArgs e)
    {
        HookDocument(e.Document);
        RequestRefresh();
    }

    private void OnDocumentToBeDestroyed(object? sender, DocumentCollectionEventArgs e)
    {
        if (ReferenceEquals(_hookedDocument, e.Document)) HookDocument(null);
    }

    private void OnCommandFinished(object? sender, CommandEventArgs e) => RequestRefresh();

    /// <summary>Перевесить обработчики команд на активный документ.</summary>
    private void HookDocument(Document? document)
    {
        if (ReferenceEquals(_hookedDocument, document)) return;

        if (_hookedDocument is not null)
        {
            _hookedDocument.CommandEnded -= OnCommandFinished;
            _hookedDocument.CommandCancelled -= OnCommandFinished;
            _hookedDocument.CommandFailed -= OnCommandFinished;
        }

        _hookedDocument = document;

        if (_hookedDocument is not null)
        {
            _hookedDocument.CommandEnded += OnCommandFinished;
            _hookedDocument.CommandCancelled += OnCommandFinished;
            _hookedDocument.CommandFailed += OnCommandFinished;
        }
    }

    // ======================= Пересчёт =======================

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();

        // Пересчёт сам меняет журнал; повторный вход только запутал бы счётчики.
        if (_refreshing || _disposed) return;

        _refreshing = true;
        try
        {
            var result = _journalService.ScanDrawing();
            if (result.HasChanges) Refreshed?.Invoke(this, new JournalRefreshedEventArgs(result));
        }
        catch (Exception)
        {
            // Автообновление не должно ронять AutoCAD: чертёж мог закрыться
            // или оказаться занят другой операцией. Следующий тик всё исправит.
        }
        finally
        {
            _refreshing = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        Stop();
        _timer.Tick -= OnTimerTick;
        _disposed = true;
    }
}

/// <summary>Итог автоматического пересканирования чертежа.</summary>
public sealed class JournalRefreshedEventArgs : EventArgs
{
    public JournalRefreshedEventArgs(JournalScanResult result) => Result = result;

    public JournalScanResult Result { get; }
}
