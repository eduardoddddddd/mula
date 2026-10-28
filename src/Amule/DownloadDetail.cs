namespace AmuleModern.Amule;

public sealed record DownloadDetail(
    string? Link,
    string? PartBaseName,
    ulong? PartMetId,
    ulong? Priority,
    ulong? Transferring,
    ulong? NotCurrent,
    ulong? A4af,
    ulong? CompleteSources,
    ulong? CompleteLow,
    ulong? CompleteHigh,
    ulong? Category,
    ulong? LastSeenUnix,
    ulong? LastRecvUnix,
    ulong? ActiveSeconds,
    ulong? AvailableParts,
    string? Aich,
    string? Comment)
{
    public static DownloadDetail None { get; } = new(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

    public string PriorityText => Priority switch
    {
        null => "No disponible",
        ulong raw => FormatPriority(raw)
    };

    public string SourcesText
    {
        get
        {
            string complete = CompleteSources switch
            {
                null when CompleteLow is ulong low && CompleteHigh is ulong high => low == high ? low.ToString() : $"{low}–{high}",
                ulong count when CompleteLow is ulong low && CompleteHigh is ulong high && low != high => $"{count} ({low}–{high})",
                ulong count => count.ToString(),
                _ => "No disponible"
            };
            return $"Transfiriendo {Num(Transferring)} · no actuales {Num(NotCurrent)} · A4AF {Num(A4af)} · completas {complete}";
        }
    }

    public static DownloadDetail FromTag(EcTag tag)
    {
        ulong? status = Num(tag, 0x308);
        string? partName = Text(tag, 0x408);
        if (status == 9 || partName is null || !IsPartBaseName(partName)) partName = null;
        var aich = tag.Find(0x407);
        return new(
            Text(tag, 0x30e),
            partName,
            Num(tag, 0x302),
            Num(tag, 0x309),
            Num(tag, 0x30d),
            Num(tag, 0x30c),
            Num(tag, 0x30b),
            Num(tag, 0x40d),
            Num(tag, 0x409),
            Num(tag, 0x40a),
            Num(tag, 0x30f),
            Num(tag, 0x311),
            Num(tag, 0x310),
            Num(tag, 0x318),
            Num(tag, 0x31d),
            aich is { Data.Length: 20 } ? Convert.ToHexString(aich.Data) : null,
            Text(tag, 0x40e));
    }

    public static string FormatPriority(ulong raw)
    {
        bool auto = raw >= 10;
        ulong value = auto ? raw - 10 : raw;
        string name = value switch
        {
            0 => "Baja",
            1 => "Normal",
            2 => "Alta",
            3 => "Muy alta",
            4 => "Muy baja",
            6 => "PowerShare",
            _ => "Prioridad " + value
        };
        return auto ? name + " (automática)" : name;
    }

    public static string UnixText(ulong? unix)
    {
        if (unix is null) return "No disponible";
        if (unix == 0) return "Nunca";
        if (unix > (ulong)DateTimeOffset.MaxValue.ToUnixTimeSeconds()) return "No disponible";
        return DateTimeOffset.FromUnixTimeSeconds((long)unix.Value).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    public static string DurationText(ulong? seconds)
    {
        if (seconds is null) return "No disponible";
        var span = TimeSpan.FromSeconds(seconds.Value);
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} h {span.Minutes:00} min";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes} min {span.Seconds:00} s";
        return span.Seconds + " s";
    }

    public static string Num(ulong? value) => value?.ToString() ?? "No disponible";

    public static bool IsPartBaseName(string name) =>
        name.Length is >= 6 and <= 10
        && name.EndsWith(".part", StringComparison.Ordinal)
        && name[..^5].All(char.IsAsciiDigit);

    private static ulong? Num(EcTag tag, ushort name)
    {
        var child = tag.Find(name);
        if (child is null || child.Type is < 2 or > 5) return null;
        try { return child.Number; }
        catch (InvalidDataException) { return null; }
    }

    private static string? Text(EcTag tag, ushort name)
    {
        var child = tag.Find(name);
        if (child is null || child.Type != 6) return null;
        try { return child.String; }
        catch (InvalidDataException) { return null; }
    }
}

public readonly record struct DownloadLocation(string? DataFile, string? MetaFile, string? Folder)
{
    public static DownloadLocation Resolve(string incoming, string temp, DownloadItem item)
    {
        if (item.IsComplete)
        {
            string? name = SafeFileName(item.Name);
            if (name is null || incoming.Length == 0) return default;
            return new(Path.Combine(incoming, name), null, incoming);
        }
        string? part = item.Detail.PartBaseName;
        if (!DownloadDetail.IsPartBaseName(part ?? "") && item.Detail.PartMetId is ulong id and > 0 and <= 65535)
            part = id.ToString(id < 1000 ? "000" : "0") + ".part";
        if (!DownloadDetail.IsPartBaseName(part ?? "") || temp.Length == 0) return default;
        string data = Path.Combine(temp, part!);
        return new(data, data + ".met", temp);
    }

    public static string? SafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") return null;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        return name;
    }
}
