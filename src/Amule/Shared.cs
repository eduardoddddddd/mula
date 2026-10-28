namespace AmuleModern.Amule;

public sealed record SharedFile(string Hash, string Name, ulong Size, string Path, string Ed2kLink, ulong Requests, ulong Uploaded, ulong Queue)
{
    public string SizeText => DownloadItem.FormatBytes(Size);
    public string FolderText
    {
        get
        {
            try { return string.IsNullOrWhiteSpace(Path) ? "—" : System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } folder ? folder : Path; }
            catch (Exception) { return Path; }
        }
    }
    public static SharedFile FromTag(EcTag tag)
    {
        var hash = tag.Find(0x31e);
        if (hash is null && tag.Type == 9 && tag.Data.Length == 16) hash = tag;
        if (hash is null || hash.Data.Length != 16) throw new InvalidDataException("Compartido sin hash válido.");
        string path = "";
        try { path = tag.Find(0x408)?.String ?? ""; } catch (InvalidDataException) { }
        string name = "Sin nombre";
        try { name = tag.Find(0x301)?.String ?? (string.IsNullOrWhiteSpace(path) ? name : System.IO.Path.GetFileName(path)); }
        catch (InvalidDataException) { if (!string.IsNullOrWhiteSpace(path)) name = System.IO.Path.GetFileName(path); }
        string link = "";
        try { link = tag.Find(0x30e)?.String ?? ""; } catch (InvalidDataException) { }
        ulong N(ushort id) { try { return tag.Find(id)?.Number ?? 0; } catch (InvalidDataException) { return 0; } }
        return new(Convert.ToHexString(hash.Data), name, N(0x303), path, link, N(0x403), N(0x401), N(0x40c));
    }
}

public sealed partial class EcClient
{
    public async Task<IReadOnlyList<SharedFile>> GetSharedFilesAsync(CancellationToken token = default)
    {
        var reply = await RequestAsync(new(0x10, EcTag.Integer(4, 2)), token);
        if (reply.Operation != 0x22) throw new InvalidDataException("Respuesta de compartidos inesperada.");
        return reply.Tags.Where(t => t.Name == 0x400).Select(SharedFile.FromTag).ToArray();
    }
    public Task ReloadSharedFilesAsync(CancellationToken token = default) => RequestAsync(new(0x23), token);
    public async Task<bool> GetKadEnabledAsync(CancellationToken token = default)
    {
        var prefs = await RequestAsync(new(0x3f, EcTag.Integer(0x1000, 4), EcTag.Integer(4, 2)), token);
        var kad = prefs.Find(0x1300)?.Find(0x130e);
        if (kad is null) return false;
        if (kad.Type is 2 or 3 or 4 or 5) return kad.Number != 0;
        return true;
    }
    public async Task SetKadEnabledAsync(bool enabled, CancellationToken token = default)
    {
        // Keep eD2k on and autoconnect off; only the Kad flag changes.
        await RequestAsync(new(0x40, EcTag.Integer(4, 2), new EcTag(0x1300, 1, [],
            EcTag.Integer(0x130d, 1),
            EcTag.Integer(0x130e, enabled ? 1ul : 0ul))), token);
        if (enabled) await RequestAsync(new(0x48), token);
        else await RequestAsync(new(0x49), token);
    }
    public async Task ResumeKadAsync(CancellationToken token = default)
    {
        // amuled starts Kad only from the autoconnect path. This app keeps
        // autoconnect off, so an enabled preference must be started explicitly.
        if (await GetKadEnabledAsync(token)) await RequestAsync(new(0x48), token);
    }
}
