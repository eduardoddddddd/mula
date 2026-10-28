using System.Text;
using AmuleModern.Amule;
using Mula;

Console.OutputEncoding = new UTF8Encoding(false);
string? profile = null, enginePath = null;
bool autoStart = false;
var rest = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--perfil" when i + 1 < args.Length: profile = args[++i]; break;
        case "--motor" when i + 1 < args.Length: enginePath = args[++i]; break;
        case "--iniciar": autoStart = true; break;
        case "-h" or "--help" or "ayuda":
            Console.WriteLine("""
                mula: consola para el aMule de aMule Modern (pensada para SSH).

                  mula                 abre la interfaz (ssh -t si la lanzas en una sola línea)
                  mula --iniciar       la abre y arranca el motor si está parado, sin preguntar
                  mula estado          red, velocidades y cola, en texto
                  mula add <ed2k://…>  añade uno o varios enlaces
                  mula iniciar         arranca amuled en segundo plano (sobrevive al SSH)
                  mula parar           cierra amuled por EC

                Opciones: --perfil <carpeta>  --motor <amuled.exe>
                Por defecto usa %LOCALAPPDATA%\AmuleModern\.local\desktop y su motor.
                """);
            return 0;
        default: rest.Add(args[i]); break;
    }
}

using var engine = Engine.Locate(profile, enginePath);
try
{
    if (rest.Count == 0)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("mula necesita una terminal interactiva. Por SSH en una línea: ssh -t <host> mula");
            return 2;
        }
        await new App(engine, autoStart).RunAsync();
        return 0;
    }
    switch (rest[0])
    {
        case "iniciar":
            if (await engine.TryConnectAsync()) { Console.WriteLine($"El motor ya está en marcha (aMule {engine.Version})."); return 0; }
            await engine.StartAsync();
            Console.WriteLine($"Motor en marcha (aMule {engine.Version}, EC 127.0.0.1:{engine.Port}).");
            return 0;
        case "parar":
            if (!await engine.TryConnectAsync()) { Console.WriteLine("El motor no está en marcha."); return 0; }
            engine.RememberSnapshots(await engine.Client!.GetServersAsync(), await engine.Client.GetDownloadsAsync());
            await engine.StopAsync();
            Console.WriteLine("Motor parado.");
            return 0;
        case "estado" or "status":
        {
            if (!await engine.TryConnectAsync()) { Console.WriteLine("Motor parado. Arráncalo con: mula iniciar"); return 1; }
            var client = engine.Client!;
            var stats = await client.RequestAsync(new(0x0a, EcTag.Integer(4, 0)));
            var net = NetworkState.FromTag(stats.Find(5) ?? throw new InvalidDataException("Falta el estado de red."));
            Console.WriteLine($"aMule {engine.Version} · eD2k {net.Ed2kText}{(net.Connected && net.Server is { } s ? $" ({s.Name})" : "")} · Kad {net.KadText}");
            Console.WriteLine($"↓ {DownloadItem.FormatBytes(stats.Find(0x201)?.Number ?? 0)}/s  ↑ {DownloadItem.FormatBytes(stats.Find(0x200)?.Number ?? 0)}/s");
            Console.WriteLine($"Completas: {engine.IncomingPath}  ·  Temporales: {engine.TempPath}");
            foreach (var d in (await client.GetDownloadsAsync()).OrderBy(d => d.IsComplete).ThenBy(d => d.Name))
                Console.WriteLine($"  {d.ProgressText,8}  {d.SpeedText,11}  {d.StateText,-12} {TextWidth.Clean(d.Name)}");
            return 0;
        }
        case "add" or "añadir":
        {
            var links = rest.Skip(1).Where(l => l.StartsWith("ed2k://", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (links.Length == 0) { Console.Error.WriteLine("Uso: mula add \"ed2k://|file|…|/\""); return 2; }
            if (!await engine.TryConnectAsync()) { Console.Error.WriteLine("Motor parado. Arráncalo con: mula iniciar"); return 1; }
            foreach (var link in links) await engine.Client!.AddLinkAsync(link);
            Console.WriteLine($"{links.Length} enlace(s) añadido(s).");
            return 0;
        }
        default:
            Console.Error.WriteLine($"Orden desconocida: {rest[0]}. Prueba mula --help");
            return 2;
    }
}
catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or EcCommandException or ArgumentException or TimeoutException or InvalidOperationException)
{
    Console.Error.WriteLine("mula: " + ex.Message);
    return 1;
}
