using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AmuleModern.Amule;

public sealed partial class EcClient : IDisposable
{
    private readonly TcpClient socket = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    public string ServerVersion { get; private set; } = "";
    public static string Md5Hex(string text) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public async Task ConnectAsync(int port, string passwordHash, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        await socket.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
        socket.NoDelay = true;
        var salt = await RequestAsync(new(0x02, EcTag.Text(0x100, "AmuleModern"), EcTag.Text(0x101, "0.1.0"), EcTag.Integer(2, 0x0204, 2)), deadline.Token);
        if (salt.Operation != 0x4f) throw new InvalidDataException(salt.Find(0)?.String ?? "El motor rechazó la versión EC.");
        string saltHex = (salt.Find(0xb) ?? throw new InvalidDataException("Falta salt EC.")).Number.ToString("X");
        byte[] response = Convert.FromHexString(Md5Hex(passwordHash.ToLowerInvariant() + Md5Hex(saltHex)));
        var auth = await RequestAsync(new(0x50, new EcTag(1, 9, response)), deadline.Token);
        if (auth.Operation != 4) throw new UnauthorizedAccessException("El motor rechazó la autenticación EC.");
        ServerVersion = auth.Find(0x050b)?.String ?? "3.0.1";
    }
    public async Task<EcPacket> RequestAsync(EcPacket packet, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var stream = socket.GetStream();
            await stream.WriteAsync(EcProtocol.Encode(packet), timeout.Token);
            byte[] header = new byte[8];
            await stream.ReadExactlyAsync(header, timeout.Token);
            uint flags = BinaryPrimitives.ReadUInt32BigEndian(header);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            if (flags != 0x20 || length < 3 || length > EcProtocol.MaxPacketBytes)
                throw new InvalidDataException("Cabecera EC inválida o extensión no negociada.");
            byte[] body = new byte[length];
            await stream.ReadExactlyAsync(body, timeout.Token);
            var reply = EcProtocol.DecodeBody(body);
            if (reply.Operation == 5) throw new EcCommandException(reply.Find(0)?.String ?? "El motor rechazó la operación.");
            return reply;
        }
        catch (EcCommandException) { throw; }
        catch { socket.Dispose(); throw; } // Never reuse a stream after partial/ambiguous I/O.
        finally { gate.Release(); }
    }
    public async Task<IReadOnlyList<DownloadItem>> GetDownloadsAsync(CancellationToken token = default)
    {
        // FULL: the partfile tag integer is the session ECID needed to clear completed rows.
        var reply = await RequestAsync(new(0x0d, EcTag.Integer(4, 2)), token);
        if (reply.Operation != 0x1f) throw new InvalidDataException("Respuesta de cola inesperada.");
        return reply.Tags.Where(t => t.Name == 0x300).Select(DownloadItem.FromTag).ToArray();
    }
    public Task<EcPacket> AddLinkAsync(string link, CancellationToken token = default)
    {
        if (!link.StartsWith("ed2k://|file|", StringComparison.OrdinalIgnoreCase) || link.Length > 16384)
            throw new ArgumentException("Introduce un enlace ed2k de archivo válido.");
        return RequestAsync(new(9, EcTag.Text(0, link)), token);
    }
    public void Dispose() => socket.Dispose();
}

public sealed class EcCommandException(string message) : Exception(message);

public sealed record DownloadItem(string Hash, string Name, ulong Size, ulong Done, ulong Speed, ulong Sources, byte State, ulong EcId)
{
    public DownloadDetail Detail { get; init; } = DownloadDetail.None;
    // Smoothed bytes/s supplied by the UI; the engine only reports the instantaneous rate.
    public double AverageSpeed { get; init; }
    public double? EtaSeconds => IsComplete || State == 7 || AverageSpeed < 1 || Done >= Size ? null : (Size - Done) / AverageSpeed;
    public string EtaText => EtaSeconds is double s ? FormatEta(s) : "—";
    public static string FormatEta(double seconds)
    {
        if (seconds >= 30 * 86400) return "> 30 d";
        var span = TimeSpan.FromSeconds(Math.Ceiling(seconds));
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays} d {span.Hours} h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} h {span.Minutes:00} min";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes} min";
        return $"{span.Seconds} s";
    }
    public double Progress => Size == 0 ? 0 : Math.Clamp(100d * Done / Size, 0, 100);
    public string ProgressText => $"{Progress:0.0} %";
    public string SizeText => FormatBytes(Size);
    public string SpeedText => Speed == 0 ? "—" : FormatBytes(Speed) + "/s";
    public bool IsComplete => State == 9;
    public bool CanCancel => State != 9;
    // Values from aMule 3.0.1 Constants.h (not UI list indexes).
    public string StateText => State switch { 0 => Speed > 0 ? "Descargando" : "En espera", 1 => "En espera", 2 => "Por verificar", 3 => "Verificando", 4 => "Error", 5 => "Sin espacio", 7 => "Pausado", 8 => "Completando", 9 => "Completado", 10 => "Reservando espacio", _ => $"Estado {State}" };
    public static string FormatBytes(ulong bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824d:0.0} GB" : bytes >= 1048576 ? $"{bytes / 1048576d:0.0} MB" : bytes >= 1024 ? $"{bytes / 1024d:0.0} KB" : $"{bytes} B";
    public static DownloadItem FromTag(EcTag tag)
    {
        var hash = tag.Find(0x31e) ?? (tag.Type == 9 ? tag : null);
        if (hash is null || hash.Data.Length != 16) throw new InvalidDataException("Descarga sin hash válido.");
        ulong N(ushort name) => tag.Find(name)?.Number ?? 0;
        ulong ecId = tag.Type is 2 or 3 or 4 or 5 ? tag.Number : 0;
        return new(Convert.ToHexString(hash.Data), tag.Find(0x301)?.String ?? "Sin nombre", N(0x303), N(0x306), N(0x307), N(0x30a), (byte)N(0x308), ecId)
        {
            Detail = DownloadDetail.FromTag(tag)
        };
    }
}
