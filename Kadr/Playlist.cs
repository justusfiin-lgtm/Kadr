using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Kadr;

/// <summary>Строка плейлиста.</summary>
public sealed class PlaylistItem(string path) : INotifyPropertyChanged
{
    private int _number;
    private bool _isCurrent;

    public string Path { get; } = path;

    public string Name { get; } = System.IO.Path.GetFileName(path) is { Length: > 0 } name ? name : path;

    /// <summary>Номер строки (с 1).</summary>
    public int Number
    {
        get => _number;
        set => Set(ref _number, value);
    }

    /// <summary>Этот файл сейчас играет.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => Set(ref _isCurrent, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // Имя строки для экранного диктора и автотестов (UI Automation).
    public override string ToString() => Name;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Список файлов одного экземпляра плеера и то, какой из них играет.
/// Только данные: открывает файлы окно видео, показывает — окно плейлиста.
/// </summary>
public sealed class Playlist : INotifyPropertyChanged
{
    private PlaylistItem? _current;
    private bool _isAlphabetical = true;

    public Playlist()
    {
        Items.CollectionChanged += (_, _) =>
        {
            for (int i = 0; i < Items.Count; i++)
                Items[i].Number = i + 1;
            OnPropertyChanged(nameof(Summary));
        };
    }

    public ObservableCollection<PlaylistItem> Items { get; } = [];

    /// <summary>Файл, который играет; null — ничего не выбрано (например, его убрали из списка).</summary>
    public PlaylistItem? Current
    {
        get => _current;
        set
        {
            if (_current == value)
                return;
            if (_current is not null)
                _current.IsCurrent = false;
            _current = value;
            if (_current is not null)
                _current.IsCurrent = true;
            OnPropertyChanged();
        }
    }

    /// <summary>Следующий файл без перехода по кругу — для автоперехода: в конце списка — остановка.</summary>
    public PlaylistItem? Next => NeighbourOfCurrent(+1);

    /// <summary>
    /// Следующий по кругу — для кнопки и клавиш: после последнего — первый.
    /// Если играющий файл убрали из списка — первый.
    /// </summary>
    public PlaylistItem? NextInCircle => InCircle(+1);

    /// <summary>Предыдущий по кругу: перед первым — последний.</summary>
    public PlaylistItem? PreviousInCircle => InCircle(-1);

    /// <summary>Подвал окна: «12 файлов · по алфавиту»; «по алфавиту» — если порядок не меняли вручную.</summary>
    public string Summary
    {
        get
        {
            var text = $"{Items.Count} {FilesWord(Items.Count)}";
            return _isAlphabetical && Items.Count > 1 ? text + " · по алфавиту" : text;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Заменить весь список (пути уже отсортированы по алфавиту).</summary>
    public void Replace(IEnumerable<string> paths)
    {
        Current = null;
        Items.Clear();
        foreach (var path in paths)
            Items.Add(new PlaylistItem(path));
        SetAlphabetical(true);
    }

    /// <summary>Добавить в конец списка — ручной порядок не ломается.</summary>
    public void Append(IEnumerable<string> paths)
    {
        var added = false;
        foreach (var path in paths)
        {
            Items.Add(new PlaylistItem(path));
            added = true;
        }
        if (added)
            SetAlphabetical(false);
    }

    /// <summary>Убрать строку из списка (файл на диске не трогается). Если она играла — файл доигрывает.</summary>
    public void Remove(PlaylistItem item)
    {
        if (item == Current)
            Current = null;
        Items.Remove(item);
    }

    public void Clear()
    {
        Current = null;
        Items.Clear();
        SetAlphabetical(true);
    }

    /// <summary>Упорядочить весь список по именам файлов «как в проводнике».</summary>
    public void SortByName()
    {
        var sorted = Items
            .OrderBy(item => item.Name, NaturalComparer.Instance)
            .ThenBy(item => item.Path, NaturalComparer.Instance)
            .ToList();
        Items.Clear();
        foreach (var item in sorted)
            Items.Add(item);
        SetAlphabetical(true);
    }

    public PlaylistItem? Find(string path) =>
        Items.FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));

    private PlaylistItem? InCircle(int step)
    {
        if (Items.Count == 0)
            return null;
        if (Current is null)
            return Items[0];
        var index = (Items.IndexOf(Current) + step + Items.Count) % Items.Count;
        return Items[index];
    }

    private PlaylistItem? NeighbourOfCurrent(int step)
    {
        if (Current is null)
            return null;
        var index = Items.IndexOf(Current) + step;
        return index >= 0 && index < Items.Count ? Items[index] : null;
    }

    private void SetAlphabetical(bool value)
    {
        _isAlphabetical = value;
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>«1 файл», «2 файла», «5 файлов», «21 файл».</summary>
    private static string FilesWord(int count)
    {
        var lastTwo = count % 100;
        var last = count % 10;
        if (lastTwo is >= 11 and <= 14)
            return "файлов";
        return last switch
        {
            1 => "файл",
            >= 2 and <= 4 => "файла",
            _ => "файлов",
        };
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
