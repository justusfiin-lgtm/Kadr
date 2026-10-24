using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Kadr;

/// <summary>Окно «Информация о файле»: строки «название — значение».</summary>
public partial class FileInfoWindow : Window
{
    public FileInfoWindow(IEnumerable<(string Label, string Value)> rows)
    {
        InitializeComponent();
        foreach (var (label, value) in rows)
        {
            var row = Rows.RowDefinitions.Count;
            Rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Add(new TextBlock
            {
                Text = label,
                FontSize = 13,
                Margin = new Thickness(0, 0, 16, 6),
                Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
            }, row, 0);
            // TextBox только для чтения — значение можно выделить и скопировать (например, путь к папке).
            Add(new TextBox
            {
                Text = value,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 6),
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
                CaretBrush = (System.Windows.Media.Brush)FindResource("TextBrush"),
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(0),
            }, row, 1);
        }
    }

    private void Add(UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Rows.Children.Add(element);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key is Key.Escape or Key.Enter)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
