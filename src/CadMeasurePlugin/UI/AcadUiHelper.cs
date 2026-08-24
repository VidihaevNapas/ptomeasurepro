using System.Windows;
using System.Windows.Interop;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace CadMeasurePlugin.UI;

/// <summary>
/// Мелкие помощники для показа WPF-окон поверх AutoCAD.
/// Без назначения владельца окно теряется за окном AutoCAD и выглядит как зависание.
/// </summary>
public static class AcadUiHelper
{
    /// <summary>Показать модальное окно, владельцем которого будет главное окно AutoCAD.</summary>
    public static bool ShowDialogOverAcad(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        try
        {
            var mainWindowHandle = AcadApp.MainWindow?.Handle ?? IntPtr.Zero;
            if (mainWindowHandle != IntPtr.Zero)
                new WindowInteropHelper(window).Owner = mainWindowHandle;
        }
        catch
        {
            // Если AutoCAD не отдал хэндл — покажем окно без владельца, это не критично.
        }

        return window.ShowDialog() == true;
    }

    /// <summary>Вернуть фокус в область чертежа, чтобы сразу можно было указывать точки.</summary>
    public static void FocusDrawingArea()
    {
        try
        {
            AcadApp.MainWindow?.Focus();
            AcadApp.DocumentManager.MdiActiveDocument?.Window?.Focus();
        }
        catch
        {
            // Не критично: пользователь щёлкнет по чертежу сам.
        }
    }

    public static void ShowError(DependencyObject? owner, string message) =>
        MessageBox.Show(message, PluginSettings.MessageBoxTitle, MessageBoxButton.OK, MessageBoxImage.Error);

    public static void ShowWarning(DependencyObject? owner, string message) =>
        MessageBox.Show(message, PluginSettings.MessageBoxTitle, MessageBoxButton.OK, MessageBoxImage.Warning);

    public static void ShowInfo(DependencyObject? owner, string message) =>
        MessageBox.Show(message, PluginSettings.MessageBoxTitle, MessageBoxButton.OK, MessageBoxImage.Information);

    public static bool Confirm(string message) =>
        MessageBox.Show(message, PluginSettings.MessageBoxTitle, MessageBoxButton.YesNo, MessageBoxImage.Question)
            == MessageBoxResult.Yes;
}
