using System.ComponentModel;
using System.IO;
using System.Windows;
using Kadr.Mpv;

namespace Kadr;

public partial class VideoWindow : Window
{
    private readonly string _filePath;
    private MpvPlayer? _player;

    public VideoWindow(string filePath)
    {
        InitializeComponent();
        // Ссылки (http://…, av://…) mpv открывает сам — путь на диске из них делать нельзя.
        _filePath = filePath.Contains("://") ? filePath : Path.GetFullPath(filePath);
        Title = $"{Path.GetFileName(_filePath)} — Kadr";
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _player = new MpvPlayer(Video.Handle);
        }
        catch (DllNotFoundException)
        {
            ShowError("Не найден файл libmpv-2.dll. Он должен лежать в одной папке с Kadr.exe.");
            Close();
            return;
        }
        catch (MpvException ex)
        {
            ShowError(ex.Message);
            Close();
            return;
        }

        _player.EndFile += OnEndFile;
        _player.Shutdown += Close;
        _player.LoadFile(_filePath);
    }

    private void OnEndFile(MpvEndFileReason reason, string? error)
    {
        if (reason == MpvEndFileReason.Error)
            ShowError($"Не удалось воспроизвести файл:\n{_filePath}\n\n{error}");
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_player is null)
            return;

        // mpv рисует прямо в наше окно, поэтому его нужно остановить раньше, чем окно исчезнет.
        // Останавливаем в фоновом потоке, чтобы окно не «зависло», пока mpv завершается,
        // а потом закрываем окно уже по-настоящему.
        e.Cancel = true;
        var player = _player;
        _player = null;
        Hide();
        await Task.Run(player.Dispose);
        Close();
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "Kadr", MessageBoxButton.OK, MessageBoxImage.Error);
}
