using System.IO;

namespace Kadr;

/// <summary>Какие файлы плеер считает видео и как он их собирает из папок (CLAUDE.md, раздел 6).</summary>
internal static class VideoFiles
{
    public static readonly string[] Extensions =
    [
        "mkv", "mp4", "m4v", "avi", "mov", "wmv", "webm", "ts", "m2ts", "mts",
        "mpg", "mpeg", "flv", "3gp", "ogv", "vob", "divx", "rmvb",
    ];

    private static readonly HashSet<string> ExtensionSet = new(Extensions, StringComparer.OrdinalIgnoreCase);

    public static bool IsVideo(string path) => ExtensionSet.Contains(Path.GetExtension(path).TrimStart('.'));

    /// <summary>
    /// Все видео из папки этого файла (без подпапок) и сам файл, по алфавиту «как в проводнике».
    /// Так строится плейлист при открытии одного файла.
    /// </summary>
    public static List<string> FolderOf(string file)
    {
        List<string> files = [];
        var folder = Path.GetDirectoryName(file);
        if (folder is not null)
        {
            try
            {
                // По умолчанию скрытые и системные файлы пропускаются, недоступные — тоже.
                files.AddRange(Directory.EnumerateFiles(folder, "*", new EnumerationOptions()).Where(IsVideo));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Папку прочитать не удалось — в плейлисте будет только сам файл.
            }
        }

        if (!files.Contains(file, StringComparer.OrdinalIgnoreCase))
            files.Add(file);
        files.Sort(NaturalComparer.Instance);
        return files;
    }

    /// <summary>
    /// Все видео из папки и всех её подпапок. Порядок — по пути внутри папки:
    /// «Сезон 2\…» раньше «Сезон 10\…», внутри подпапки — по именам файлов.
    /// Скрытые и системные папки пропускаются, недоступная подпапка не мешает остальным.
    /// </summary>
    public static List<string> FolderTree(string folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*", options).Where(IsVideo).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        files.Sort((a, b) => ComparePaths(Path.GetRelativePath(folder, a), Path.GetRelativePath(folder, b)));
        return files;
    }

    /// <summary>
    /// То, что пользователь перетащил или выбрал в диалоге: видеофайлы — как есть,
    /// папки — со всеми подпапками. Остальные файлы (субтитры и т. п.) пропускаются.
    /// </summary>
    public static List<string> Expand(IEnumerable<string> paths)
    {
        List<string> result = [];
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
                result.AddRange(FolderTree(path));
            else if (File.Exists(path) && IsVideo(path))
                result.Add(path);
        }
        return result;
    }

    /// <summary>Сравнить пути по частям (папка за папкой), каждую часть — «естественно».</summary>
    private static int ComparePaths(string a, string b)
    {
        var partsA = a.Split(Path.DirectorySeparatorChar);
        var partsB = b.Split(Path.DirectorySeparatorChar);
        for (int i = 0; i < Math.Min(partsA.Length, partsB.Length); i++)
        {
            var result = NaturalComparer.Instance.Compare(partsA[i], partsB[i]);
            if (result != 0)
                return result;
        }
        return partsA.Length.CompareTo(partsB.Length);
    }
}

/// <summary>
/// Сравнение строк как в проводнике Windows: числа сравниваются как числа
/// («Серия 2» раньше «Серия 10»), регистр букв не важен.
/// </summary>
internal sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y) => NativeMethods.StrCmpLogicalW(x ?? "", y ?? "");
}
