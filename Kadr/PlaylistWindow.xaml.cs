using System.Windows;

namespace Kadr;

public partial class PlaylistWindow : FloatingWindow
{
    public PlaylistWindow()
    {
        InitializeComponent();
    }

    /// <summary>Папка, которая показывается под заголовком.</summary>
    public string? Folder
    {
        get => FolderText.Text;
        set => FolderText.Text = value;
    }

    // Крестик прячет плейлист, а не закрывает программу. Показать снова — клавиша L.
    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();
}
