using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kadr;

/// <summary>Положение и размер окна в пикселях экрана, и было ли оно показано.</summary>
internal sealed class WindowPlacement
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Visible { get; set; } = true;
}

/// <summary>Общие настройки всех экземпляров плеера: %APPDATA%\Kadr\settings.json.</summary>
internal sealed class Settings
{
    public WindowPlacement? VideoWindow { get; set; }
    public WindowPlacement? PanelWindow { get; set; }
    public WindowPlacement? PlaylistWindow { get; set; }
    public double Volume { get; set; } = 100;
}

/// <summary>
/// Плейлист последнего закрытого плеера: %APPDATA%\Kadr\session.json. Восстанавливается, когда
/// Kadr.exe запускают без файла (CLAUDE.md, раздел 11).
/// </summary>
internal sealed class Session
{
    public List<string> Files { get; set; } = [];

    /// <summary>Какой файл играл; null — никакой.</summary>
    public string? Current { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ListOrder Order { get; set; } = ListOrder.Alphabetical;
}

/// <summary>Где остановился просмотр одного файла.</summary>
internal sealed class ResumePoint
{
    public double Position { get; set; }
    public DateTime SavedAt { get; set; }
}

/// <summary>
/// Чтение и запись файлов в %APPDATA%\Kadr (CLAUDE.md, раздел 11).
/// Одновременно может работать несколько плееров, поэтому каждое изменение — «прочитать свежий файл,
/// поменять, записать» под общей блокировкой (именованный мьютекс), а запись — через временный файл,
/// чтобы при сбое не остался обрезанный JSON. Ошибки чтения/записи не роняют плеер: настройки просто
/// не сохранятся (или будут взяты по умолчанию).
/// </summary>
internal static class Storage
{
    /// <summary>Сколько файлов помнить для «продолжить с места остановки».</summary>
    private const int MaxResumePoints = 1000;

    /// <summary>Не запоминать, если посмотрели меньше стольких секунд.</summary>
    private const double MinResumeSeconds = 10;

    /// <summary>Не запоминать, если досмотрели дальше этой доли (последние ~3% — титры).</summary>
    private const double FinishedFraction = 0.97;

    // Для автотестов: переменная окружения KADR_DATA_DIR указывает другую папку,
    // чтобы тесты не трогали настоящие настройки пользователя.
    private static readonly string Folder =
        Environment.GetEnvironmentVariable("KADR_DATA_DIR") is { Length: > 0 } testFolder
            ? testFolder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kadr");

    private static readonly string SettingsPath = Path.Combine(Folder, "settings.json");
    private static readonly string ResumePath = Path.Combine(Folder, "resume.json");
    private static readonly string SessionPath = Path.Combine(Folder, "session.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // Русские буквы в путях пишутся как есть, а не Ф — файл можно читать глазами.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Settings LoadSettings() => Read<Settings>(SettingsPath) ?? new Settings();

    public static void UpdateSettings(Action<Settings> change) => Update(SettingsPath, change);

    /// <summary>Плейлист последнего закрытого плеера; null — ещё ни разу не сохраняли.</summary>
    public static Session? LoadSession() => Read<Session>(SessionPath);

    /// <summary>Запомнить плейлист при закрытии плеера («последний закрытый побеждает»).</summary>
    public static void SaveSession(Session session) => Update<Session>(SessionPath, stored =>
    {
        stored.Files = session.Files;
        stored.Current = session.Current;
        stored.Order = session.Order;
    });

    /// <summary>С какой секунды продолжить этот файл; null — смотреть с начала.</summary>
    public static double? ResumePosition(string path) =>
        Read<Dictionary<string, ResumePoint>>(ResumePath)?.GetValueOrDefault(ResumeKey(path))?.Position;

    /// <summary>
    /// Запомнить, где остановился просмотр. Если посмотрели совсем чуть-чуть или почти до конца —
    /// наоборот, забыть: в следующий раз файл начнётся с начала.
    /// </summary>
    public static void SaveResumePosition(string path, double position, double duration)
    {
        var remember = position >= MinResumeSeconds && (duration <= 0 || position < duration * FinishedFraction);
        Update<Dictionary<string, ResumePoint>>(ResumePath, points =>
        {
            var key = ResumeKey(path);
            if (!remember)
            {
                points.Remove(key);
                return;
            }

            points[key] = new ResumePoint { Position = Math.Round(position, 1), SavedAt = DateTime.Now };
            // Самые старые записи выбрасываем, чтобы файл не рос бесконечно.
            foreach (var old in points.OrderByDescending(p => p.Value.SavedAt).Skip(MaxResumePoints).ToList())
                points.Remove(old.Key);
        });
    }

    /// <summary>Пути в Windows не различают регистр — ключ всегда в нижнем регистре.</summary>
    private static string ResumeKey(string path) => Path.GetFullPath(path).ToLowerInvariant();

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine(ex.Message);
            return null;
        }
    }

    private static void Update<T>(string path, Action<T> change) where T : class, new()
    {
        using var mutex = new Mutex(false, @"Local\Kadr.Storage");
        var locked = false;
        try
        {
            try
            {
                locked = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                // Другой экземпляр закрылся аварийно, не отпустив блокировку, — теперь она наша.
                locked = true;
            }

            var value = Read<T>(path) ?? new T();
            change(value);

            Directory.CreateDirectory(Folder);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(ex.Message);
        }
        finally
        {
            if (locked)
                mutex.ReleaseMutex();
        }
    }
}
