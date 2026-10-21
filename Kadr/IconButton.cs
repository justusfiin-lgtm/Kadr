using System.Windows;
using System.Windows.Controls;

namespace Kadr;

/// <summary>Кнопка со значком. Внешний вид — стиль в App.xaml; здесь только скругление углов.</summary>
public class IconButton : Button
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(IconButton), new FrameworkPropertyMetadata(new CornerRadius(6)));

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }
}
