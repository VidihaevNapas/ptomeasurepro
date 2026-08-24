using System.Globalization;
using System.Windows;
using System.Windows.Input;
using CadMeasurePlugin.Services;

namespace CadMeasurePlugin.UI;

/// <summary>
/// Ввод длины вертикального участка («Подъём» / «Опуск») в миллиметрах.
/// Вертикальные участки не рисуются на чертеже, поэтому вводятся здесь и
/// копятся отдельно по ключу «слой + участок».
/// </summary>
public partial class VerticalRunWindow : Window
{
    /// <summary>Введённая длина в миллиметрах.</summary>
    public double LengthMm { get; private set; }

    public VerticalRunWindow(bool isRise, string materialName, string layerName, string section, VerticalRunTotals totals)
    {
        InitializeComponent();

        HeaderText.Text = isRise ? "Подъём" : "Опуск";
        Title = isRise ? "Подъём — длина участка" : "Опуск — длина участка";

        MaterialText.Text =
            $"Материал: {materialName}\nСлой: {layerName}\nУчасток: {(string.IsNullOrWhiteSpace(section) ? "— не задан —" : section)}";

        TotalsText.Text =
            $"Уже учтено по этому слою и участку:\n" +
            $"подъёмы — {totals.UpMm / 1000.0:0.###} м, опуски — {totals.DownMm / 1000.0:0.###} м, " +
            $"итого {totals.TotalM:0.###} м ({totals.EntryCount} ввод(ов)).";

        Loaded += (_, _) =>
        {
            LengthBox.Focus();
            LengthBox.SelectAll();
        };
    }

    /// <summary>Пропускаем только цифры, запятую и точку.</summary>
    private void LengthBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        foreach (var ch in e.Text)
        {
            if (!char.IsDigit(ch) && ch != ',' && ch != '.')
            {
                e.Handled = true;
                return;
            }
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseLength(LengthBox.Text, out var value, out var error))
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            LengthBox.Focus();
            LengthBox.SelectAll();
            return;
        }

        LengthMm = value;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Разбор числа: принимаем и «1500», и «1500,5», и «1500.5».</summary>
    public static bool TryParseLength(string? text, out double lengthMm, out string error)
    {
        lengthMm = 0;
        error = string.Empty;

        var normalized = (text ?? string.Empty).Trim().Replace(',', '.');
        if (normalized.Length == 0)
        {
            error = "Введи длину участка в миллиметрах.";
            return false;
        }

        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out lengthMm))
        {
            error = "Не удалось разобрать число. Пример: 2500 или 2500,5";
            return false;
        }

        if (lengthMm <= 0)
        {
            error = "Длина участка должна быть больше нуля.";
            return false;
        }

        return true;
    }
}
