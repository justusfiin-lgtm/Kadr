using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Kadr.Mpv;

internal sealed class MpvException(string message) : Exception(message);

/// <summary>
/// Один экземпляр движка mpv, который рисует видео в заданное окно.
/// Все события этого класса вызываются в UI-потоке.
/// </summary>
internal sealed class MpvPlayer : IDisposable
{
    private IntPtr _ctx;
    private readonly Dispatcher _dispatcher;
    private readonly Thread _eventThread;
    private volatile bool _stopping;

    /// <summary>Изменилось наблюдаемое свойство: имя и новое значение
    /// (string, bool, long, double или null, если значения сейчас нет).</summary>
    public event Action<string, object?>? PropertyChanged;

    public event Action? FileLoaded;

    /// <summary>Файл перестал играть: причина и текст ошибки, если она была.</summary>
    public event Action<MpvEndFileReason, string?>? EndFile;

    /// <summary>Изменились параметры картинки (размер, пропорции) — например, открылось новое видео.</summary>
    public event Action? VideoReconfig;

    /// <summary>mpv завершил работу сам (не через <see cref="Dispose"/>).</summary>
    public event Action? Shutdown;

    /// <param name="videoWindow">HWND окна, в котором mpv будет рисовать картинку.</param>
    public MpvPlayer(IntPtr videoWindow)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _ctx = MpvNative.mpv_create();
        if (_ctx == IntPtr.Zero)
            throw new MpvException("Не удалось запустить движок mpv.");

        try
        {
            SetOption("wid", videoWindow.ToInt64().ToString(CultureInfo.InvariantCulture));
            SetOption("hwdec", "auto-safe");
            // Всё управление — в нашем приложении, встроенные кнопки и клавиши mpv не нужны.
            SetOption("osc", "no");
            SetOption("input-default-bindings", "no");
            SetOption("input-vo-keyboard", "no");
            // Досмотрев файл, mpv останавливается на последнем кадре (а не выгружает файл);
            // переход к следующему файлу плейлиста делает наше приложение по свойству eof-reached.
            SetOption("keep-open", "yes");
            // Внешние субтитры из той же папки подхватываются, даже если имя не совпадает в точности
            // («Фильм.rus.srt» к «Фильм.mkv»); кодировка определяется сама (часто это Windows-1251).
            SetOption("sub-auto", "fuzzy");
            SetOption("sub-codepage", "auto");
            // Экранные надписи (громкость, дорожки…) — в углу, шрифтом интерфейса.
            SetOption("osd-font", "Segoe UI");
            SetOption("osd-font-size", "32");

            // Для отладки: если задана переменная окружения KADR_MPV_LOG, mpv пишет туда подробный журнал.
            var logFile = Environment.GetEnvironmentVariable("KADR_MPV_LOG");
            if (!string.IsNullOrEmpty(logFile))
                SetOption("log-file", logFile);

            Check(MpvNative.mpv_initialize(_ctx), "запуск mpv");
        }
        catch
        {
            MpvNative.mpv_terminate_destroy(_ctx);
            _ctx = IntPtr.Zero;
            throw;
        }

        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv events" };
        _eventThread.Start();
    }

    /// <param name="startSeconds">С какой секунды начать; null — с начала.</param>
    public void LoadFile(string path, double? startSeconds = null)
    {
        if (startSeconds is not { } start)
        {
            Command("loadfile", path);
            return;
        }
        // loadfile <файл> <как> <номер> <опции>: «replace» — вместо текущего, «-1» — номер не важен.
        Command("loadfile", path, "replace", "-1",
            "start=" + start.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Выполнить действие с плеером. Ошибка mpv (перемотка файла, который перематывать нельзя;
    /// плеер уже закрывается) — не повод ронять программу: действие просто не сработает.
    /// </summary>
    /// <returns>true — получилось.</returns>
    public static bool Try(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (ex is MpvException or ObjectDisposedException)
        {
            System.Diagnostics.Debug.WriteLine(ex.Message);
            return false;
        }
    }

    /// <summary>Короткая надпись в углу видео на 2 секунды (рисует сам mpv — поверх картинки).</summary>
    public void ShowText(string text) => Command("show-text", text, "2000");

    public void Command(params string[] args)
    {
        // mpv ждёт массив UTF-8 строк, который заканчивается нулевым указателем.
        var native = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++)
                native[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            Check(MpvNative.mpv_command(Ctx, native), $"команда {args[0]}");
        }
        finally
        {
            foreach (var ptr in native)
                Marshal.FreeCoTaskMem(ptr);
        }
    }

    public void SetProperty(string name, string value) =>
        Check(MpvNative.mpv_set_property_string(Ctx, name, value), $"свойство {name}");

    public string? GetProperty(string name)
    {
        var ptr = MpvNative.mpv_get_property_string(Ctx, name);
        if (ptr == IntPtr.Zero)
            return null;
        try
        {
            return Marshal.PtrToStringUTF8(ptr);
        }
        finally
        {
            MpvNative.mpv_free(ptr);
        }
    }

    /// <summary>Подписаться на изменения свойства — они придут в <see cref="PropertyChanged"/>.</summary>
    public void ObserveProperty(string name, MpvFormat format) =>
        Check(MpvNative.mpv_observe_property(Ctx, 0, name, format), $"наблюдение за {name}");

    /// <summary>
    /// Останавливает mpv и освобождает его. Может занять заметное время,
    /// поэтому вызывать лучше не из UI-потока.
    /// </summary>
    public void Dispose()
    {
        if (_ctx == IntPtr.Zero)
            return;

        _stopping = true;
        MpvNative.mpv_wakeup(_ctx);
        _eventThread.Join();
        MpvNative.mpv_terminate_destroy(_ctx);
        _ctx = IntPtr.Zero;
    }

    private IntPtr Ctx
    {
        get
        {
            ObjectDisposedException.ThrowIf(_ctx == IntPtr.Zero, this);
            return _ctx;
        }
    }

    private void SetOption(string name, string value) =>
        Check(MpvNative.mpv_set_option_string(_ctx, name, value), $"настройка {name}={value}");

    private void EventLoop()
    {
        while (!_stopping)
        {
            var ev = Marshal.PtrToStructure<MpvEvent>(MpvNative.mpv_wait_event(_ctx, -1));
            switch (ev.EventId)
            {
                case MpvEventId.Shutdown:
                    Post(() => Shutdown?.Invoke());
                    return;

                case MpvEventId.FileLoaded:
                    Post(() => FileLoaded?.Invoke());
                    break;

                case MpvEventId.VideoReconfig:
                    Post(() => VideoReconfig?.Invoke());
                    break;

                case MpvEventId.EndFile:
                {
                    var end = Marshal.PtrToStructure<MpvEventEndFile>(ev.Data);
                    var error = end.Reason == MpvEndFileReason.Error ? ErrorText(end.Error) : null;
                    Post(() => EndFile?.Invoke(end.Reason, error));
                    break;
                }

                case MpvEventId.PropertyChange:
                {
                    // Данные события живут только до следующего mpv_wait_event — копируем их сразу.
                    var prop = Marshal.PtrToStructure<MpvEventProperty>(ev.Data);
                    var name = Marshal.PtrToStringUTF8(prop.Name)!;
                    var value = ReadValue(prop.Format, prop.Data);
                    Post(() => PropertyChanged?.Invoke(name, value));
                    break;
                }
            }
        }
    }

    private void Post(Action action) => _dispatcher.BeginInvoke(action);

    private static object? ReadValue(MpvFormat format, IntPtr data) => format switch
    {
        MpvFormat.String or MpvFormat.OsdString => Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(data)),
        MpvFormat.Flag => Marshal.ReadInt32(data) != 0,
        MpvFormat.Int64 => Marshal.ReadInt64(data),
        MpvFormat.Double => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(data)),
        // MpvFormat.None — значения сейчас нет (например, файл ещё не открыт).
        _ => null,
    };

    private static string ErrorText(int error) =>
        Marshal.PtrToStringUTF8(MpvNative.mpv_error_string(error)) ?? $"код {error}";

    private static void Check(int error, string action)
    {
        if (error < 0)
            throw new MpvException($"Ошибка mpv ({action}): {ErrorText(error)}");
    }
}
