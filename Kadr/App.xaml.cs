using System.Windows;

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

        // Файл передаётся аргументом (двойной щелчок в проводнике, «Открыть с помощью»).
        // Без файла окно видео само восстановит плейлист прошлого раза или откроется пустым.
        new VideoWindow(e.Args.Length > 0 ? e.Args[0] : null).Show();
    }
}
