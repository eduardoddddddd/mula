using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace AmuleModern.Amule;

public sealed record ServerDraft(string Address, string Port, string Name);

public static class ServerListFile
{
    public const int MaxServers = 200;
    public const int MaxBytes = 2 * 1024 * 1024;
    private static readonly Regex Ed2kServer = new(@"^ed2k://\|server\|([^|]+)\|(\d+)\|/?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HostPort = new(@"^(?<host>[A-Za-z0-9.-]{1,253}):(?<port>\d{1,5})(?:\s+(?<name>.+))?$", RegexOptions.CultureInvariant);
    public static IReadOnlyList<ServerDraft> Parse(byte[] data)
    {
        if (data.Length is 0 or > MaxBytes) throw new ArgumentException("La lista de servidores está vacía o supera 2 MiB.");
        if (data[0] is 0x0E or 0xE0) return ParseMet(data);
        string text = Encoding.UTF8.GetString(data).Trim('\uFEFF');
        if (text.Contains('\0')) throw new ArgumentException("El archivo no es una lista de servidores de texto ni un server.met reconocible.");
        return ParseText(text);
    }
    public static IReadOnlyList<ServerDraft> ParseText(string text)
    {
        var result = new List<ServerDraft>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line.StartsWith("//", StringComparison.Ordinal)) continue;
            ServerDraft? item = null;
            var ed2k = Ed2kServer.Match(line);
            if (ed2k.Success) item = new(ed2k.Groups[1].Value.Trim(), ed2k.Groups[2].Value, "");
            else
            {
                var match = HostPort.Match(line);
                if (match.Success) item = new(match.Groups["host"].Value, match.Groups["port"].Value, match.Groups["name"].Value.Trim());
                else
                {
                    string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && parts[1].All(char.IsDigit))
                        item = new(parts[0], parts[1], parts.Length > 2 ? string.Join(' ', parts.Skip(2)) : "");
                }
            }
            if (item is null) throw new ArgumentException("Línea de servidor no reconocida: " + Truncate(line));
            string key = item.Address + ":" + item.Port;
            if (!seen.Add(key)) continue;
            result.Add(item);
            if (result.Count > MaxServers) throw new ArgumentException($"Como máximo {MaxServers} servidores por importación.");
        }
        if (result.Count == 0) throw new ArgumentException("La lista no contiene servidores válidos.");
        return result;
    }
    public static async Task<byte[]> DownloadAsync(string url, CancellationToken token = default)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler);
        return await DownloadAsync(url, http, TimeSpan.FromSeconds(30), token);
    }
    // Injectable transport permits deterministic HTTP failure/stream tests without public servers.
    public const string ExampleUrl = "https://upd.emule-security.org/server.met";
    public static async Task<byte[]> DownloadAsync(string url, HttpClient http, TimeSpan timeout, CancellationToken token = default)
    {
        Uri uri = ValidateUrl(url);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        for (int hop = 0; hop < 5; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("AmuleModern/1.0");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location ?? throw new ArgumentException("La URL redirige sin destino.");
                uri = ValidateUrl((location.IsAbsoluteUri ? location : new Uri(uri, location)).AbsoluteUri);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new ArgumentException($"El servidor respondió {(int)response.StatusCode}. Prueba {ExampleUrl}");
            if (response.Content.Headers.ContentLength is > MaxBytes) throw new ArgumentException("La lista remota supera 2 MiB.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            byte[] body = await ReadBoundedAsync(stream, deadline.Token);
            string media = response.Content.Headers.ContentType?.MediaType ?? "";
            if (media.Contains("html", StringComparison.OrdinalIgnoreCase) || LooksLikeHtml(body))
                throw new ArgumentException($"Esa URL es una página web, no un server.met. Usa un enlace directo al fichero, p. ej. {ExampleUrl}");
            return body;
        }
        throw new ArgumentException("Demasiadas redirecciones al descargar la lista.");
    }
    public static Uri ValidateUrl(string url)
    {
        url = (url ?? "").Trim().Trim('"', '\'', '<', '>', '«', '»');
        if (url.Length == 0) throw new ArgumentException($"Pega la URL de un server.met, p. ej. {ExampleUrl}");
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException($"Usa una URL http o https, sin usuario ni contraseña. Ejemplo: {ExampleUrl}");
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(uri.Host, out var ip) && (IPAddress.IsLoopback(ip) || ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6))
            throw new ArgumentException("No se importan listas desde localhost ni IPv6.");
        return uri;
    }
    private static bool LooksLikeHtml(byte[] body)
    {
        if (body.Length < 15) return false;
        string head = Encoding.ASCII.GetString(body.AsSpan(0, Math.Min(body.Length, 64))).TrimStart().ToLowerInvariant();
        return head.StartsWith("<!doctype html", StringComparison.Ordinal) || head.StartsWith("<html", StringComparison.Ordinal);
    }
    public static async Task<byte[]> ReadFileAsync(string path, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        if (stream.Length > MaxBytes) throw new ArgumentException("La lista supera 2 MiB.");
        return await ReadBoundedAsync(stream, deadline.Token);
    }
    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken token)
    {
        using var data = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, MaxBytes + 1L - data.Length)), token);
            if (count == 0) return data.ToArray();
            if (data.Length + count > MaxBytes) throw new ArgumentException("La lista supera 2 MiB.");
            data.Write(buffer, 0, count);
        }
    }
    private static IReadOnlyList<ServerDraft> ParseMet(byte[] data)
    {
        int offset = 1;
        if (data.Length < 5) throw new ArgumentException("server.met truncado.");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)); offset += 4;
        if (count is 0 or > MaxServers) throw new ArgumentException("server.met sin servidores o con demasiadas entradas.");
        var result = new List<ServerDraft>((int)count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (uint i = 0; i < count; i++)
        {
            Need(data, offset, 10);
            var address = new IPAddress(data.AsSpan(offset, 4)); offset += 4;
            ushort port = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)); offset += 2;
            uint tags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)); offset += 4;
            if (tags > 64) throw new ArgumentException("server.met con etiquetas excesivas.");
            string serverName = "";
            for (uint t = 0; t < tags; t++)
            {
                var tag = ReadMetTag(data, offset);
                offset = tag.Next;
                if (tag.IsServerName && tag.Text.Length > 0 && serverName.Length == 0)
                    serverName = tag.Text.Length > 120 ? tag.Text[..120] : tag.Text;
            }
            if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast) || address.GetAddressBytes()[0] >= 224)
                continue;
            if (port == 0) continue;
            string key = address + ":" + port;
            if (!seen.Add(key)) continue;
            if (serverName.Any(char.IsControl)) serverName = "";
            result.Add(new(address.ToString(), port.ToString(), serverName.Trim()));
        }
        // Some lists pad or append extras after the declared entries; ignore trailing bytes.
        if (result.Count == 0) throw new ArgumentException("server.met no contiene IPv4 públicas utilizables.");
        return result;
    }
    private readonly record struct MetTag(int Next, bool IsServerName, string Text);
    private static MetTag ReadMetTag(byte[] data, int offset)
    {
        Need(data, offset, 1);
        byte type = data[offset++];
        bool namedById = (type & 0x80) != 0;
        type &= 0x7F;
        bool isServerName = false;
        if (namedById)
        {
            Need(data, offset, 1);
            byte id = data[offset++];
            // ST_SERVERNAME = 0x01 in eMule/aMule server.met
            isServerName = id == 0x01;
        }
        else
        {
            Need(data, offset, 2);
            ushort nameLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)); offset += 2;
            Need(data, offset, nameLen);
            // Real lists often store the id as a 1-byte name ("\x01") instead of the 0x80 form.
            isServerName = nameLen == 1 && data[offset] == 0x01;
            offset += nameLen;
        }
        switch (type)
        {
            case 1: Need(data, offset, 16); return new(offset + 16, false, "");
            case 2:
                Need(data, offset, 2);
                ushort text = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)); offset += 2;
                Need(data, offset, text);
                string value = text == 0 ? "" : Encoding.UTF8.GetString(data, offset, text);
                return new(offset + text, isServerName, value);
            case 3:
            case 4: Need(data, offset, 4); return new(offset + 4, false, "");
            case 5:
            case 0x12: Need(data, offset, 1); return new(offset + 1, false, "");
            case 0x11: Need(data, offset, 2); return new(offset + 2, false, "");
            case 0x13: Need(data, offset, 8); return new(offset + 8, false, "");
            case 0x07:
            case 0x20:
                Need(data, offset, 4);
                uint blob = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)); offset += 4;
                if (blob > 4096) throw new ArgumentException("Etiqueta server.met demasiado grande.");
                Need(data, offset, (int)blob); return new(offset + (int)blob, false, "");
            default: throw new ArgumentException($"Etiqueta server.met desconocida (0x{type:X2}).");
        }
    }
    private static void Need(byte[] data, int offset, int count)
    {
        if (count < 0 || offset < 0 || offset > data.Length - count) throw new ArgumentException("server.met truncado.");
    }
    private static string Truncate(string line) => line.Length <= 80 ? line : line[..80] + "…";
}
