namespace AmuleModern.Amule;

public sealed partial class EcClient
{
    public Task PauseAsync(string hash, bool paused, CancellationToken token = default) => PauseAsync([hash], paused, token);
    public Task PauseAsync(IReadOnlyList<string> hashes, bool paused, CancellationToken token = default)
    {
        var tags = PartFileTags(hashes);
        return RequestAsync(new((byte)(paused ? 0x19 : 0x1a), tags), token);
    }
    public async Task CancelDownloadsAsync(IReadOnlyList<string> hashes, CancellationToken token = default)
    {
        var tags = PartFileTags(hashes);
        await RequestAsync(new(0x1d, tags), token);
        var remaining = (await GetDownloadsAsync(token)).Select(d => d.Hash).ToHashSet();
        var leftover = hashes.Where(remaining.Contains).ToArray();
        if (leftover.Length > 0) throw new EcCommandException("El motor no canceló " + leftover.Length + " descarga(s).");
    }
    public Task ClearCompletedAsync(IReadOnlyList<ulong> ecIds, CancellationToken token = default)
    {
        if (ecIds.Count == 0) throw new ArgumentException("No hay descargas completadas seleccionadas.");
        var tags = ecIds.Select(id => EcTag.Integer(0x000f, id, 4)).ToArray();
        return RequestAsync(new(0x53, tags), token);
    }
    private static EcTag[] PartFileTags(IReadOnlyList<string> hashes)
    {
        if (hashes.Count == 0) throw new ArgumentException("Selecciona al menos una descarga.");
        return hashes.Select(hash =>
        {
            if (hash.Length != 32) throw new ArgumentException("Hash de descarga inválido.");
            return new EcTag(0x300, 9, Convert.FromHexString(hash));
        }).ToArray();
    }
}
