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

        // «Kadr.exe --unregister» — убрать Kadr из «Открыть с помощью» и выйти.
        if (e.Args is ["--unregister"])
        {
            Registration.Unregister();
            MessageBox.Show("Kadr убран из меню «Открыть с помощью».", "Kadr",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Kadr — в «Открыть с помощью» для видеофайлов (обновляется, если программу перенесли).
        Registration.EnsureRegistered();

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
        var dialog = new OpenFileDialog
        {
            Title = "Kadr — открыть видео",
            Filter = VideoFiles.DialogFilter,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
