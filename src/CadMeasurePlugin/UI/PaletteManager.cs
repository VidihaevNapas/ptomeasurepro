using Autodesk.AutoCAD.Windows;

namespace CadMeasurePlugin.UI;

/// <summary>
/// Создаёт и показывает PaletteSet с WPF-палитрой замеров.
///
/// PaletteSet создаётся один раз за сессию: повторный вызов команды просто
/// показывает уже созданную палитру, поэтому журнал и выбранный материал
/// не теряются при закрытии палитры крестиком.
/// </summary>
public static class PaletteManager
{
    // Постоянный GUID: по нему AutoCAD запоминает положение и размер палитры
    // между сеансами. Менять его нельзя, иначе настройки пользователя сбросятся.
    private static readonly Guid PaletteSetId = new("8B6F1D2A-3C74-4F58-9A21-6E0D5C7B4A19");

    private static PaletteSet? _paletteSet;
    private static MeasurePaletteControl? _control;

    /// <summary>Показать палитру (создав её при первом вызове).</summary>
    public static void Show()
    {
        if (_paletteSet is null)
        {
            _control = new MeasurePaletteControl();

            _paletteSet = new PaletteSet(PluginSettings.PaletteTitle, PaletteSetId)
            {
                Style = PaletteSetStyles.ShowPropertiesMenu
                        | PaletteSetStyles.ShowAutoHideButton
                        | PaletteSetStyles.ShowCloseButton
                        | PaletteSetStyles.Snappable,
                MinimumSize = new System.Drawing.Size(420, 520),
                DockEnabled = DockSides.Left | DockSides.Right
            };

            // AddVisual размещает WPF-элемент в палитре напрямую,
            // без прослойки WinForms ElementHost.
            _paletteSet.AddVisual("Замеры", _control);
            _paletteSet.Size = new System.Drawing.Size(560, 720);
        }

        _paletteSet.Visible = true;
        _paletteSet.KeepFocus = false;
    }

    /// <summary>Освобождение при выгрузке плагина.</summary>
    public static void Shutdown()
    {
        if (_paletteSet is null) return;

        try
        {
            _paletteSet.Visible = false;
            _paletteSet.Dispose();
        }
        catch
        {
            // AutoCAD закрывается — молча выходим.
        }
        finally
        {
            _paletteSet = null;
            _control = null;
        }
    }
}
