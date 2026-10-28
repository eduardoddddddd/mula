namespace AmuleModern.Amule;

public sealed record QueueSnapshotItem(string Hash, ulong Size, bool Paused, string Name)
{
    public string Link => $"ed2k://|file|{Name}|{Size}|{Hash}|/";
}

public static class ProfileSnapshot
{
    public static string FormatServers(IEnumerable<ServerItem> servers)
    {
        var lines = new List<string>();
        foreach (var server in servers)
        {
            if (server.Port == 0 || string.IsNullOrWhiteSpace(server.Address)) continue;
            string name = server.Name.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
            lines.Add(server.Address + ":" + server.Port + "\t" + name);
        }
        return string.Join('\n', lines);
    }

    public static IReadOnlyList<ServerDraft> ParseServers(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(line => line.Replace('\t', ' ')).Where(line => line.Trim().Length > 0);
        return ServerListFile.ParseText(string.Join('\n', lines));
    }

    public static string FormatQueue(IEnumerable<DownloadItem> items)
    {
        var lines = new List<string>();
        foreach (var item in items)
        {
            if (item.IsComplete || item.Hash.Length != 32 || item.Name.IndexOfAny(['|', '\r', '\n', '\t']) >= 0) continue;
            lines.Add($"{item.Hash}\t{item.Size}\t{(item.State == 7 ? 1 : 0)}\t{item.Name}");
        }
        return string.Join('\n', lines);
    }

    public static IReadOnlyList<QueueSnapshotItem> ParseQueue(string text)
    {
        var result = new List<QueueSnapshotItem>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string[] parts = raw.Split('\t');
            if (parts.Length != 4 || parts[0].Length != 32 || !ulong.TryParse(parts[1], out ulong size) || parts[2] is not ("0" or "1")) continue;
            if (parts[3].Length == 0 || parts[3].Contains('|')) continue;
            result.Add(new(parts[0], size, parts[2] == "1", parts[3]));
        }
        return result;
    }
}
