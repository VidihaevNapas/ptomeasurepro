using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;

namespace CadMeasurePlugin.UI;

/// <summary>Столбец таблицы в списке настройки видимости.</summary>
public sealed class ColumnVisibilityRow : INotifyPropertyChanged
{
    private bool _isVisible;

    public ColumnVisibilityRow(string title, bool isVisible)
    {
        Title = title;
        _isVisible = isVisible;
    }

    /// <summary>Заголовок столбца — он же ключ настройки.</summary>
    public string Title { get; }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;

            _isVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Настройка видимости столбцов спецификации в таблице журнала.
///
/// Скрытие столбца — это про удобство работы, а не про состав данных:
/// скрытая колонка никуда не девается ни из журнала, ни из выгрузки.
/// Поэтому окно и предупреждает об этом прямо в заголовке.
/// </summary>
public partial class ColumnVisibilityWindow : Window
{
    private readonly ObservableCollection<ColumnVisibilityRow> _rows = new();

    public ColumnVisibilityWindow(IEnumerable<(string Title, bool IsVisible)> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        InitializeComponent();

        foreach (var (title, isVisible) in columns) _rows.Add(new ColumnVisibilityRow(title, isVisible));

        ColumnsList.ItemsSource = _rows;
    }

    /// <summary>Итоговая видимость по заголовкам столбцов.</summary>
    public IReadOnlyDictionary<string, bool> ColumnVisibility { get; private set; } =
        new Dictionary<string, bool>();

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.IsVisible = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ColumnVisibility = _rows.ToDictionary(r => r.Title, r => r.IsVisible);
        DialogResult = true;
    }
}
