using System.Diagnostics;
using System.IO;
using System.Windows;

namespace CadMeasurePlugin.UI;

/// <summary>
/// Итог экспорта: путь к готовой книге и быстрый переход к ней.
///
/// Раньше здесь было простое сообщение с путём, и путь приходилось копировать
/// глазами. Окно немодальное: пользователь может открыть ведомость, свернуть
/// Excel и продолжить работу в AutoCAD, не закрывая его.
/// </summary>
public partial class ExportResultWindow : Window
{
    private readonly string _path;

    /// <param name="path">Полный путь к созданной книге.</param>
    /// <param name="details">Пояснение под путём: что было сделано перед выгрузкой.</param>
    public ExportResultWindow(string path, string details)
    {
        _path = path ?? string.Empty;

        InitializeComponent();

        PathText.Text = _path;
        DetailsText.Text = details ?? string.Empty;
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_path))
        {
            AcadUiHelper.ShowWarning(this, $"Файл не найден: {_path}");
            return;
        }

        // UseShellExecute — чтобы файл открылся тем приложением, которое
        // назначено на .xlsx в системе, а не запускался как программа.
        Launch(() => Process.Start(new ProcessStartInfo(_path) { UseShellExecute = true }));
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(_path);

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            AcadUiHelper.ShowWarning(this, $"Папка не найдена: {folder}");
            return;
        }

        // Если файл на месте — открываем проводник с выделенным файлом,
        // иначе просто саму папку.
        Launch(() => File.Exists(_path)
            ? Process.Start("explorer.exe", $"/select,\"{_path}\"")
            : Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Запустить внешнюю программу, не роняя палитру: причин отказа много —
    /// от отсутствующего Excel до политик безопасности, — и все они
    /// пользователю понятнее текстом, чем исключением в AutoCAD.
    /// </summary>
    private void Launch(Func<Process?> start)
    {
        try
        {
            start();
        }
        catch (Exception ex)
        {
            AcadUiHelper.ShowError(this, $"Не удалось открыть файл/папку: {ex.Message}");
        }
    }
}
