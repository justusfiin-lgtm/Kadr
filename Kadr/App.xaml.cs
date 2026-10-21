using System.Windows;
using Microsoft.Win32;

namespace Kadr;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
            MessageBox.Show(args.Exception.ToString(), "Kadr — непредвиденная ошибка",
                MessageBoxButton.OK, MessageBoxImage.Error);

        // Файл передаётся аргументом (двойной клик в проводнике, «Открыть с помощью»),
        // а если его нет — спрашиваем у пользователя.
        string? path = e.Args.Length > 0 ? e.Args[0] : AskForFile();
        if (path is null)
        {
            Shutdown();
            return;
        }

        new VideoWindow(path).Show();
    }

    private static string? AskForFile()
    {
        var patterns = string.Join(";", VideoFiles.Extensions.Select(ext => "*." + ext));
        var dialog = new OpenFileDialog
        {
            Title = "Kadr — открыть видео",
            Filter = $"Видеофайлы|{patterns}|Все файлы|*.*",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
