namespace AmuleModern.Amule;

public sealed record SearchResult(string Hash, string Name, ulong Size, ulong Sources, ulong CompleteSources)
{
    public string SizeText => DownloadItem.FormatBytes(Size);
    public static SearchResult FromTag(EcTag tag)
    {
        var hash = tag.Find(0x31e);
        if (hash is null || hash.Data.Length != 16) throw new InvalidDataException("Resultado sin hash válido.");
        return new(Convert.ToHexString(hash.Data), tag.Find(0x301)?.String ?? "Sin nombre",
            tag.Find(0x303)?.Number ?? 0, tag.Find(0x30a)?.Number ?? 0, tag.Find(0x30d)?.Number ?? 0);
    }
}

public sealed partial class EcClient
{
    public async Task StartSearchAsync(string query, bool global = false, bool kad = false, CancellationToken token = default)
    {
        query = query.Trim();
        if (query.Length is < 1 or > 512 || query.Any(char.IsControl)) throw new ArgumentException("Escribe un término de búsqueda de hasta 512 caracteres.");
        var network = await GetNetworkStateAsync(token);
        if (kad)
        {
            if (!network.KadConnected) throw new ArgumentException("Kad no está conectado. Actívalo en Ajustes y espera a que el estado sea Conectado.");
        }
        else if (!network.Connected) throw new ArgumentException("Conecta primero a un servidor desde Servidores.");
        await RequestAsync(new(0x27), token);
        var type = EcTag.Integer(0x701, kad ? 2ul : global ? 1ul : 0ul, 4);
        await RequestAsync(new(0x26, type with { Children = [EcTag.Text(0x702, query), EcTag.Text(0x705, "")] }), token);
    }
    public async Task<IReadOnlyList<SearchResult>> GetSearchResultsAsync(CancellationToken token = default)
    {
        var reply = await RequestAsync(new(0x28, EcTag.Integer(4, 2)), token);
        if (reply.Operation != 0x28) throw new InvalidDataException("Respuesta de búsqueda inesperada.");
        return reply.Tags.Where(t => t.Name == 0x700).Select(SearchResult.FromTag).ToArray();
    }
    public Task<EcPacket> StopSearchAsync(CancellationToken token = default) => RequestAsync(new(0x27), token);
    public async Task DownloadSearchResultAsync(string hash, CancellationToken token = default)
    {
        await RequestAsync(new(0x2a, new EcTag(0x700, 9, Convert.FromHexString(hash), EcTag.Integer(0x1101, 0))), token);
        if (!(await GetDownloadsAsync(token)).Any(d => d.Hash == hash))
            throw new EcCommandException("El motor no añadió el resultado a la cola. Repite la búsqueda.");
    }
}
