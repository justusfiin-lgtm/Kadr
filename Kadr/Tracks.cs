using System.Globalization;
using Kadr.Mpv;

namespace Kadr;

/// <summary>Звуковая дорожка или дорожка субтитров файла (из свойства mpv track-list).</summary>
internal sealed record MediaTrack(
    long Id, string? Title, string? Language, string? Codec, int Channels,
    bool Selected, bool External, bool Forced)
{
    /// <summary>
    /// Подпись для меню и экранной надписи: «Русский — AC3 5.1», «Режиссёрский (Английский) — AAC стерео»,
    /// «Русский — SRT, файл».
    /// </summary>
    public string Label
    {
        get
        {
            var language = LanguageName(Language);
            // У внешнего файла mpv ставит названием кусок имени файла («rus.srt») — язык понятнее.
            var shownTitle = External && language is not null ? null : Title;
            var name = (shownTitle, language) switch
            {
                ({ Length: > 0 } title, { } lang) when !title.Contains(lang, StringComparison.OrdinalIgnoreCase)
                    => $"{title} ({lang})",
                ({ Length: > 0 } title, _) => title,
                (_, { } lang) => lang,
                _ => $"Дорожка {Id}",
            };

            List<string> details = [];
            var codec = CodecName(Codec);
            var channels = ChannelsName(Channels);
            if (codec is not null)
                details.Add(channels is null ? codec : $"{codec} {channels}");
            if (Forced)
                details.Add("форсированные");
            if (External)
                details.Add("файл");
            return details.Count > 0 ? $"{name} — {string.Join(", ", details)}" : name;
        }
    }

    private static string? LanguageName(string? code) => code?.ToLowerInvariant() switch
    {
        null or "" or "und" => null,
        "ru" or "rus" => "Русский",
        "en" or "eng" => "Английский",
        "uk" or "ukr" => "Украинский",
        "be" or "bel" => "Белорусский",
        "kk" or "kaz" => "Казахский",
        "de" or "deu" or "ger" => "Немецкий",
        "fr" or "fra" or "fre" => "Французский",
        "es" or "spa" => "Испанский",
        "it" or "ita" => "Итальянский",
        "pt" or "por" => "Португальский",
        "pl" or "pol" => "Польский",
        "ja" or "jpn" => "Японский",
        "zh" or "chi" or "zho" => "Китайский",
        "ko" or "kor" => "Корейский",
        _ => code,
    };

    private static string? CodecName(string? codec) => codec?.ToLowerInvariant() switch
    {
        null or "" => null,
        "eac3" => "E-AC3",
        "truehd" => "TrueHD",
        "subrip" => "SRT",
        "hdmv_pgs_subtitle" => "PGS",
        "dvd_subtitle" => "VobSub",
        "webvtt" => "WebVTT",
        "mov_text" => "MP4 Text",
        var other => other.ToUpperInvariant(),
    };

    private static string? ChannelsName(int channels) => channels switch
    {
        <= 0 => null,
        1 => "моно",
        2 => "стерео",
        6 => "5.1",
        8 => "7.1",
        _ => $"{channels} кан.",
    };
}

/// <summary>
/// Выбор звуковой дорожки и субтитров, сдвиг синхронизации (CLAUDE.md, раздел 7).
/// Каждое действие показывает короткую надпись на видео.
/// </summary>
internal sealed class TrackSwitcher(MpvPlayer player)
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Какие субтитры были включены перед выключением клавишей S — их же S и включит.</summary>
    private long? _lastSubtitleId;

    public List<MediaTrack> AudioTracks() => ReadTracks("audio");

    public List<MediaTrack> SubtitleTracks() => ReadTracks("sub");

    public void SelectAudio(MediaTrack track)
    {
        if (MpvPlayer.Try(() => player.SetProperty("aid", Id(track.Id))))
            Osd($"Аудио: {track.Label}");
    }

    /// <summary>Включить эти субтитры; null — выключить.</summary>
    public void SelectSubtitles(MediaTrack? track)
    {
        if (track is null)
        {
            var current = SubtitleTracks().FirstOrDefault(t => t.Selected);
            if (current is not null)
                _lastSubtitleId = current.Id;
            if (MpvPlayer.Try(() => player.SetProperty("sid", "no")))
                Osd("Субтитры выключены");
            return;
        }

        if (MpvPlayer.Try(() => player.SetProperty("sid", Id(track.Id))))
            Osd($"Субтитры: {track.Label}");
    }

    /// <summary>Клавиша A: следующая звуковая дорожка по кругу.</summary>
    public void NextAudio()
    {
        var tracks = AudioTracks();
        if (tracks.Count == 0)
        {
            Osd("Звуковых дорожек нет");
            return;
        }
        SelectAudio(NextAfterSelected(tracks));
    }

    /// <summary>Shift+S: следующая дорожка субтитров по кругу (если были выключены — включаются).</summary>
    public void NextSubtitles()
    {
        var tracks = SubtitleTracks();
        if (tracks.Count == 0)
        {
            Osd("Субтитров нет");
            return;
        }
        SelectSubtitles(NextAfterSelected(tracks));
    }

    /// <summary>Клавиша S: выключить субтитры или включить те, что были (иначе — первые).</summary>
    public void ToggleSubtitles()
    {
        var tracks = SubtitleTracks();
        if (tracks.Count == 0)
        {
            Osd("Субтитров нет");
            return;
        }
        if (tracks.Any(t => t.Selected))
            SelectSubtitles(null);
        else
            SelectSubtitles(tracks.FirstOrDefault(t => t.Id == _lastSubtitleId) ?? tracks[0]);
    }

    /// <summary>Z / X: субтитры раньше / позже.</summary>
    public void ShiftSubtitles(double seconds) => Shift("sub-delay", seconds, "Сдвиг субтитров");

    /// <summary>Shift+Z / Shift+X: звук раньше / позже.</summary>
    public void ShiftAudio(double seconds) => Shift("audio-delay", seconds, "Сдвиг звука");

    private void Shift(string property, double seconds, string caption)
    {
        if (!MpvPlayer.Try(() => player.Command("add", property, seconds.ToString(CultureInfo.InvariantCulture))))
            return;
        if (double.TryParse(player.GetProperty(property), CultureInfo.InvariantCulture, out var value))
            Osd($"{caption}: {value.ToString("+0.0;−0.0;0.0", Russian)} с");
    }

    private static MediaTrack NextAfterSelected(List<MediaTrack> tracks)
    {
        var index = tracks.FindIndex(t => t.Selected);
        return tracks[(index + 1) % tracks.Count];
    }

    private List<MediaTrack> ReadTracks(string type)
    {
        List<MediaTrack> tracks = [];
        if (!int.TryParse(player.GetProperty("track-list/count"), out var count))
            return tracks;

        for (int i = 0; i < count; i++)
        {
            string? Get(string name) => player.GetProperty($"track-list/{i}/{name}");
            if (Get("type") != type || !long.TryParse(Get("id"), out var id))
                continue;
            int.TryParse(Get("demux-channel-count"), out var channels);
            tracks.Add(new MediaTrack(id, Get("title"), Get("lang"), Get("codec"), channels,
                Selected: Get("selected") == "yes", External: Get("external") == "yes", Forced: Get("forced") == "yes"));
        }
        return tracks;
    }

    private void Osd(string text) => MpvPlayer.Try(() => player.ShowText(text));

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
}
