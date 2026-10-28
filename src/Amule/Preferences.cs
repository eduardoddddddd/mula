namespace AmuleModern.Amule;

public sealed record BandwidthLimits(uint DownloadKib, uint UploadKib)
{
    public const uint MaxKib = 102400;
    public bool DownloadUnlimited => DownloadKib == 0;
    public bool UploadUnlimited => UploadKib == 0;
    public static uint Normalize(ulong raw) => raw == 0 || raw == 0xFFFF ? 0 : checked((uint)raw);
}

public sealed partial class EcClient
{
    public async Task<BandwidthLimits> GetBandwidthAsync(CancellationToken token = default)
    {
        var prefs = await RequestAsync(new(0x3f, EcTag.Integer(0x1000, 4), EcTag.Integer(4, 2)), token);
        var conn = prefs.Find(0x1300);
        return new(ReadLimit(conn, 0x1303), ReadLimit(conn, 0x1304));
    }
    public async Task SetBandwidthAsync(uint downloadKib, uint uploadKib, CancellationToken token = default)
    {
        if (downloadKib > BandwidthLimits.MaxKib || uploadKib > BandwidthLimits.MaxKib)
            throw new ArgumentException($"Los límites deben estar entre 0 y {BandwidthLimits.MaxKib} KiB/s. 0 es ilimitado.");
        await RequestAsync(new(0x40, EcTag.Integer(4, 2), new EcTag(0x1300, 1, [],
            EcTag.Integer(0x130d, 1),
            EcTag.Integer(0x1303, downloadKib, 4),
            EcTag.Integer(0x1304, uploadKib, 4))), token);
    }
    private static uint ReadLimit(EcTag? conn, ushort id)
    {
        var tag = conn?.Find(id);
        if (tag is null) return 0;
        if (tag.Type is 2 or 3 or 4 or 5) return BandwidthLimits.Normalize(tag.Number);
        return 0;
    }
}
