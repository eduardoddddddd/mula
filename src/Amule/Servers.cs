using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace AmuleModern.Amule;

public sealed record ServerItem(string Address, ushort Port, string Name, ulong Users, ulong Files, ulong Ping)
{
    public string Endpoint => $"{Address}:{Port}";
    public string UsersText => Users == 0 ? "—" : Users.ToString("N0");
    public string FilesText => Files == 0 ? "—" : Files.ToString("N0");
    public string PingText => Ping == 0 ? "—" : $"{Ping} ms";
    public EcTag ToTag()
    {
        byte[] data = new byte[6];
        IPAddress.Parse(Address).GetAddressBytes().CopyTo(data, 0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), Port);
        return new(0x500, 8, data);
    }
    public static ServerItem FromTag(EcTag tag)
    {
        if (tag.Type != 8 || tag.Data.Length != 6) throw new InvalidDataException("Dirección de servidor EC inválida.");
        string address = new IPAddress(tag.Data.AsSpan(0, 4)).ToString();
        ushort port = BinaryPrimitives.ReadUInt16BigEndian(tag.Data.AsSpan(4));
        string? rawName = null;
        try { rawName = tag.Find(0x501)?.String; } catch (InvalidDataException) { /* name tag malformed */ }
        string name = string.IsNullOrWhiteSpace(rawName) ? address : rawName;
        return new(address, port, name, tag.Find(0x505)?.Number ?? 0, tag.Find(0x507)?.Number ?? 0, tag.Find(0x504)?.Number ?? 0);
    }
}

public sealed record NetworkState(bool Connected, bool Connecting, bool KadConnected, bool KadRunning, ulong? ClientId, ServerItem? Server)
{
    public string Ed2kText => Connected ? $"Conectado · {(ClientId is null ? "ID desconocida" : ClientId < 16777216 ? "LowID" : "HighID")}" : Connecting ? "Conectando…" : "Desconectado";
    public string KadText => KadConnected ? "Conectado" : KadRunning ? "Conectando" : "Inactivo";
    public static NetworkState FromTag(EcTag tag)
    {
        ulong flags = tag.Number;
        var server = tag.Find(0x500);
        return new((flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 16) != 0,
            tag.Find(6)?.Number, server is null ? null : ServerItem.FromTag(server));
    }
}

public sealed partial class EcClient
{
    public async Task EnableEd2kAsync(CancellationToken token = default)
    {
        // FULL + explicit boolean: preserve all omitted preferences (Kad, autoconnect, ports...).
        await RequestAsync(new(0x40, EcTag.Integer(4, 2), new EcTag(0x1300, 1, [], EcTag.Integer(0x130d, 1))), token);
    }
    public async Task<IReadOnlyList<ServerItem>> GetServersAsync(CancellationToken token = default)
    {
        var packet = await RequestAsync(new(0x2c, EcTag.Integer(4, 2)), token);
        if (packet.Operation != 0x2d) throw new InvalidDataException("Respuesta de servidores inesperada.");
        var result = new List<ServerItem>();
        foreach (var tag in packet.Tags.Where(t => t.Name == 0x500))
        {
            try { result.Add(ServerItem.FromTag(tag)); }
            catch (InvalidDataException) { /* skip a malformed EC server row */ }
        }
        return result;
    }
    public async Task<NetworkState> GetNetworkStateAsync(CancellationToken token = default)
    {
        var packet = await RequestAsync(new(0xb, EcTag.Integer(4, 2)), token);
        return NetworkState.FromTag(packet.Find(5) ?? throw new InvalidDataException("Falta el estado de red."));
    }
    public async Task<ServerItem> AddServerAsync(string address, string portText, string name, CancellationToken token = default)
    {
        address = address.Trim(); name = name.Trim();
        if (!ushort.TryParse(portText.Trim(), out ushort port) || port == 0) throw new ArgumentException("El puerto debe estar entre 1 y 65535.");
        if (string.IsNullOrWhiteSpace(address) || address.Length > 253 || Uri.CheckHostName(address) is UriHostNameType.Unknown or UriHostNameType.IPv6)
            throw new ArgumentException("Introduce una dirección IPv4 o un dominio, sin http:// ni puerto.");
        if (name.Length > 120 || name.Any(char.IsControl)) throw new ArgumentException("El nombre debe tener como máximo 120 caracteres y no contener saltos de línea.");
        IPAddress? ip;
        if (IPAddress.TryParse(address, out var parsed)) ip = parsed;
        else
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try { ip = (await Dns.GetHostAddressesAsync(address, AddressFamily.InterNetwork, deadline.Token)).FirstOrDefault(); }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException) { throw new ArgumentException("No se pudo resolver el dominio a IPv4.", ex); }
        }
        if (ip is null || ip.AddressFamily != AddressFamily.InterNetwork || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.Broadcast) || ip.GetAddressBytes()[0] is 0 or >= 224)
            throw new ArgumentException("La dirección IPv4 del servidor no es válida.");
        // Prefer an empty EC name over copying the IP: aMule keeps a non-empty label and
        // will not replace it with the name announced when the TCP session connects.
        string label = name;
        var item = new ServerItem(ip.ToString(), port, label.Length == 0 ? ip.ToString() : label, 0, 0, 0);
        var existing = (await GetServersAsync(token)).FirstOrDefault(s => s.Endpoint == item.Endpoint);
        if (existing != null)
        {
            if (ShouldReplaceServerName(existing, label))
                return await ReplaceServerNameAsync(existing, label, token);
            return existing; // Idempotent add; do not duplicate the engine's list.
        }
        await RequestAsync(new(0x31, EcTag.Text(0x503, item.Endpoint), EcTag.Text(0x501, label)), token);
        return item;
    }
    private static bool ShouldReplaceServerName(ServerItem existing, string incoming) =>
        incoming.Length > 0
        && !string.Equals(existing.Name, incoming, StringComparison.Ordinal)
        && (string.IsNullOrWhiteSpace(existing.Name)
            || string.Equals(existing.Name, existing.Address, StringComparison.OrdinalIgnoreCase)
            || string.Equals(existing.Name, existing.Endpoint, StringComparison.OrdinalIgnoreCase));
    private async Task<ServerItem> ReplaceServerNameAsync(ServerItem existing, string name, CancellationToken token)
    {
        var network = await GetNetworkStateAsync(token);
        bool wasCurrent = network.Server?.Endpoint == existing.Endpoint && (network.Connected || network.Connecting);
        if (wasCurrent) await DisconnectServerAsync(token);
        await RequestAsync(new(0x30, existing.ToTag()), token);
        await RequestAsync(new(0x31, EcTag.Text(0x503, existing.Endpoint), EcTag.Text(0x501, name)), token);
        var updated = new ServerItem(existing.Address, existing.Port, name, existing.Users, existing.Files, existing.Ping);
        if (wasCurrent) await ConnectServerAsync(updated, token);
        return updated;
    }
    public Task<EcPacket> ConnectServerAsync(ServerItem server, CancellationToken token = default) => RequestAsync(new(0x2f, server.ToTag()), token);
    public async Task<string> GetActivityLogAsync(CancellationToken token = default)
    {
        var reply = await RequestAsync(new(0x35), token);
        if (reply.Operation != 0x38) throw new InvalidDataException("Respuesta de registro inesperada.");
        return reply.Find(0)?.String ?? "";
    }
    public async Task<string> GetServerLogAsync(CancellationToken token = default)
    {
        var reply = await RequestAsync(new(0x37), token);
        if (reply.Operation != 0x3a) throw new InvalidDataException("Respuesta de mensajes de servidor inesperada.");
        return reply.Find(0)?.String ?? "";
    }
    public async Task DisconnectServerAsync(CancellationToken token = default)
    {
        // Pinned aMule disconnects established sessions; it does not cancel connection attempts.
        await RequestAsync(new(0x2e), token);
    }
    public async Task RemoveServerAsync(ServerItem server, CancellationToken token = default)
    {
        var network = await GetNetworkStateAsync(token);
        if (network.Server?.Endpoint == server.Endpoint && (network.Connected || network.Connecting))
            await DisconnectServerAsync(token);
        await RequestAsync(new(0x30, server.ToTag()), token);
    }
    public async Task<(int Added, int Renamed)> ImportServersAsync(IReadOnlyList<ServerDraft> drafts, CancellationToken token = default)
    {
        if (drafts.Count == 0) throw new ArgumentException("La lista no contiene servidores válidos.");
        if (drafts.Count > ServerListFile.MaxServers) throw new ArgumentException($"Como máximo {ServerListFile.MaxServers} servidores por importación.");
        var before = (await GetServersAsync(token)).ToDictionary(s => s.Endpoint, s => s.Name, StringComparer.OrdinalIgnoreCase);
        int added = 0, renamed = 0;
        foreach (var draft in drafts)
        {
            var item = await AddServerAsync(draft.Address, draft.Port, draft.Name, token);
            if (!before.TryGetValue(item.Endpoint, out string? oldName))
            {
                before[item.Endpoint] = item.Name;
                added++;
            }
            else if (draft.Name.Length > 0
                     && string.Equals(item.Name, draft.Name, StringComparison.Ordinal)
                     && !string.Equals(oldName, item.Name, StringComparison.Ordinal))
            {
                before[item.Endpoint] = item.Name;
                renamed++;
            }
        }
        return (added, renamed);
    }
}