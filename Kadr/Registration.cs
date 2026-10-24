using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Kadr;

/// <summary>
/// Kadr в «Открыть с помощью» проводника для видеофайлов (CLAUDE.md, раздел 12).
/// Всё пишется только в HKEY_CURRENT_USER — права администратора не нужны. Программой «по умолчанию»
/// Kadr себя не делает (Windows разрешает это только самому пользователю), но появляется в списке
/// «Открыть с помощью» и в «Параметры → Приложения по умолчанию».
/// </summary>
internal static class Registration
{
    private const string ProgId = "Kadr.Video";
    private const string ClassesKey = @"Software\Classes";
    private const string CapabilitiesKey = @"Software\Kadr\Capabilities";

    private static string ExePath => Environment.ProcessPath ?? "";

    private static string OpenCommand => $"\"{ExePath}\" \"%1\"";

    /// <summary>
    /// Зарегистрироваться (или обновить путь, если программу перенесли в другую папку).
    /// Вызывается при каждом запуске; если всё уже на месте — ничего не пишет.
    /// Запуск из папки сборки проекта (bin\Debug, bin\Release) не регистрируется — иначе каждая
    /// пробная сборка перехватывала бы «Открыть с помощью» у настоящей программы.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (IsDevelopmentBuild() || IsUpToDate())
            return;
        try
        {
            Register();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // Не получилось записать в реестр — плеер всё равно работает, просто без «Открыть с помощью».
            System.Diagnostics.Debug.WriteLine(ex.Message);
        }
    }

    /// <summary>Убрать все записи Kadr из реестра (команда «Kadr.exe --unregister»).</summary>
    public static void Unregister()
    {
        using var classes = Registry.CurrentUser.CreateSubKey(ClassesKey);
        classes.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);
        classes.DeleteSubKeyTree(@"Applications\Kadr.exe", throwOnMissingSubKey: false);
        foreach (var ext in VideoFiles.Extensions)
        {
            using var openWith = classes.OpenSubKey($@".{ext}\OpenWithProgids", writable: true);
            openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
        }
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Kadr", throwOnMissingSubKey: false);
        using (var apps = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
            apps?.DeleteValue("Kadr", throwOnMissingValue: false);
        NotifyExplorer();
    }

    private static void Register()
    {
        using var classes = Registry.CurrentUser.CreateSubKey(ClassesKey);

        // Тип файла «Видеофайл (Kadr)»: значок и команда открытия.
        using (var progId = classes.CreateSubKey(ProgId))
        {
            progId.SetValue("", "Видеофайл (Kadr)");
            using (var icon = progId.CreateSubKey("DefaultIcon"))
                icon.SetValue("", $"\"{ExePath}\",0");
            using (var command = progId.CreateSubKey(@"shell\open\command"))
                command.SetValue("", OpenCommand);
        }

        // Сама программа: имя в списке «Открыть с помощью» и какие файлы она умеет открывать.
        using (var app = classes.CreateSubKey(@"Applications\Kadr.exe"))
        {
            app.SetValue("FriendlyAppName", "Kadr");
            using (var command = app.CreateSubKey(@"shell\open\command"))
                command.SetValue("", OpenCommand);
            using var types = app.CreateSubKey("SupportedTypes");
            foreach (var ext in VideoFiles.Extensions)
                types.SetValue("." + ext, "");
        }

        // Для каждого расширения — «Kadr тоже умеет это открывать» (программа по умолчанию не меняется).
        foreach (var ext in VideoFiles.Extensions)
        {
            using var openWith = classes.CreateSubKey($@".{ext}\OpenWithProgids");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        // «Параметры → Приложения по умолчанию»: там пользователь сможет выбрать Kadr для видео.
        using (var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesKey))
        {
            capabilities.SetValue("ApplicationName", "Kadr");
            capabilities.SetValue("ApplicationDescription", "Видеоплеер Kadr");
            using var associations = capabilities.CreateSubKey("FileAssociations");
            foreach (var ext in VideoFiles.Extensions)
                associations.SetValue("." + ext, ProgId);
        }
        using (var apps = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            apps.SetValue("Kadr", CapabilitiesKey);

        NotifyExplorer();
    }

    /// <summary>Записи уже есть и указывают на этот Kadr.exe — переписывать не нужно.</summary>
    private static bool IsUpToDate()
    {
        using var command = Registry.CurrentUser.OpenSubKey($@"{ClassesKey}\{ProgId}\shell\open\command");
        if (command?.GetValue("") as string != OpenCommand)
            return false;
        foreach (var ext in VideoFiles.Extensions)
        {
            using var openWith = Registry.CurrentUser.OpenSubKey($@"{ClassesKey}\.{ext}\OpenWithProgids");
            if (openWith?.GetValue(ProgId) is null)
                return false;
        }
        return true;
    }

    private static bool IsDevelopmentBuild()
    {
        var folder = Path.GetDirectoryName(ExePath) ?? "";
        return folder.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)
            || folder.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Сказать проводнику, что связи файлов изменились, — чтобы меню обновилось сразу.</summary>
    private static void NotifyExplorer() => SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);

    private const int SHCNE_ASSOCCHANGED = 0x08000000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
