using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace Kadr;

/// <summary>
/// Окно плейлиста: показывает <see cref="Playlist"/> и меняет его (добавить, удалить, очистить, сортировать).
/// Что воспроизводить — решает окно видео: окно плейлиста только просит об этом через <see cref="PlayRequested"/>.
/// </summary>
public partial class PlaylistWindow : FloatingWindow
{
    private Playlist? _playlist;

    public PlaylistWindow()
    {
        InitializeComponent();
    }

    /// <summary>Пользователь выбрал файл для воспроизведения (двойной щелчок или Enter).</summary>
    public event Action<PlaylistItem>? PlayRequested;

    internal void Attach(Playlist playlist)
    {
        _playlist = playlist;
        DataContext = playlist;
        playlist.PropertyChanged += OnPlaylistPropertyChanged;
    }

    private void OnPlaylistPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Playlist.Current) || _playlist?.Current is not { } current)
            return;
        // Под заголовком — папка файла, который играет; сам файл — в поле зрения.
        FolderText.Text = current.Path.Contains("://") ? "" : Path.GetDirectoryName(current.Path);
        List.ScrollIntoView(current);
    }

    // ---------- Список ----------

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Двойной щелчок по пустому месту списка — не по строке — ничего не делает.
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(List, source) is ListBoxItem { DataContext: PlaylistItem item })
            PlayRequested?.Invoke(item);
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (List.SelectedItem is not PlaylistItem item)
            return;
        if (e.Key == Key.Delete)
        {
            RemoveSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            PlayRequested?.Invoke(item);
            e.Handled = true;
        }
    }

    private void RemoveSelected()
    {
        if (_playlist is null || List.SelectedItem is not PlaylistItem item)
            return;
        var index = List.SelectedIndex;
        _playlist.Remove(item);
        // Выделение переходит на соседнюю строку — так удобно удалять несколько подряд.
        if (_playlist.Items.Count > 0)
            List.SelectedIndex = Math.Min(index, _playlist.Items.Count - 1);
    }

    // ---------- Контекстное меню (раздел 6.2) ----------

    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        var hasItems = _playlist?.Items.Count > 0;
        RemoveMenuItem.IsEnabled = List.SelectedItem is not null;
        // Правый щелчок по строке выделяет её — пункт относится к этому файлу.
        ShowInFolderMenuItem.IsEnabled = List.SelectedItem is PlaylistItem selected && FolderOf(selected.Path) is not null;
        ClearMenuItem.IsEnabled = hasItems;
        SortMenuItem.IsEnabled = hasItems;
        ShuffleMenuItem.IsEnabled = _playlist?.Items.Count > 1;
    }

    private void OnAddFilesClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Kadr — добавить файлы в плейлист",
            Filter = VideoFiles.DialogFilter,
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) == true)
            _playlist?.Append(VideoFiles.Expand(dialog.FileNames));
    }

    private void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Kadr — добавить папку (со всеми подпапками)" };
        if (dialog.ShowDialog(this) == true)
            _playlist?.Append(VideoFiles.FolderTree(dialog.FolderName));
    }

    /// <summary>
    /// «Открыть расположение файла»: окно проводника с папкой файла, сам файл выделен
    /// (как одноимённый пункт в проводнике). Если файла уже нет — просто его папка.
    /// </summary>
    private void OnShowInFolderClick(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not PlaylistItem item || FolderOf(item.Path) is not { } folder)
            return;
        if (File.Exists(item.Path) && NativeMethods.ShowInExplorer(item.Path))
            return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    /// <summary>Существующая папка файла; null — это ссылка или папки уже нет (диск отключён и т. п.).</summary>
    private static string? FolderOf(string path) =>
        path.Contains("://") ? null
        : Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder) ? folder
        : null;

    private void OnRemoveClick(object sender, RoutedEventArgs e) => RemoveSelected();

    private void OnClearClick(object sender, RoutedEventArgs e) => _playlist?.Clear();

    private void OnSortClick(object sender, RoutedEventArgs e) => _playlist?.SortByName();

    private void OnShuffleClick(object sender, RoutedEventArgs e)
    {
        _playlist?.Shuffle();
        // Играющий файл теперь первый — показываем начало списка.
        if (_playlist?.Items.Count > 0)
            List.ScrollIntoView(_playlist.Items[0]);
    }

    // ---------- Перетаскивание файлов и папок в окно: добавить в конец ----------

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            _playlist?.Append(VideoFiles.Expand(paths));
    }

    // Крестик прячет плейлист, а не закрывает программу. Показать снова — клавиша L.
    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();
}
