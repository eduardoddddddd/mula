using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AmuleModern.Amule;

namespace Mula;

// The amuled of the aMule Modern profile: attach over EC, or start/stop it headless.
sealed class Engine(string profilePath, string enginePath) : IDisposable
{
    public string ProfilePath { get; } = profilePath;
    public string EnginePath { get; } = enginePath;
    public int Port { get; private set; }
    // Read from the shared amule.conf: the same folders aMule Modern uses.
    public string IncomingPath { get; private set; } = "";
    public string TempPath { get; private set; } = "";
    public EcClient? Client { get; private set; }
    public string Version => Client?.ServerVersion ?? "";
    private string hash = "";

    public static Engine Locate(string? profile, string? engine)
    {
        string root = InstallRoot();
        profile ??= Environment.GetEnvironmentVariable("MULA_PROFILE") ?? Path.Combine(root, ".local", "desktop");
        engine ??= Environment.GetEnvironmentVariable("MULA_ENGINE") ?? Path.Combine(root, "engine", "bin", OperatingSystem.IsWindows() ? "amuled.exe" : "amuled");
        return new(Path.GetFullPath(profile), Path.GetFullPath(engine));
    }

    // An install made from inside an MSIX app (e.g. a Claude desktop session) lands in that
    // package's LocalCache; SSH and WMI only see it there, not under %LOCALAPPDATA%.
    private static string InstallRoot()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>();
        string packages = Path.Combine(local, "Packages");
        if (Directory.Exists(packages))
        {
            try
            {
                candidates.AddRange(Directory.EnumerateDirectories(packages)
                    .Select(p => Path.Combine(p, "LocalCache", "Local", "AmuleModern"))
                    .Where(Directory.Exists));
            }
            catch (UnauthorizedAccessException) { }
        }
        candidates.Add(Path.Combine(local, "AmuleModern"));
        static DateTime Stamp(string root)
        {
            string conf = Path.Combine(root, ".local", "desktop", "amule.conf");
            return File.Exists(conf) ? File.GetLastWriteTimeUtc(conf) : DateTime.MinValue;
        }
        return candidates.OrderByDescending(Stamp).First();
    }

    public string ConfigPath =>Path.Combine(ProfilePath, "amule.conf");

    private void LoadConfig()
    {
        if (!File.Exists(ConfigPath))
            throw new FileNotFoundException($"No existe {ConfigPath}. Abre aMule Modern una vez para crear el perfil, o indica otro con --perfil.");
        var lines = File.ReadAllLines(ConfigPath);
        string Value(string key) => lines.FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..] ?? "";
        if (!int.TryParse(Value("ECPort"), out int port)) throw new InvalidDataException("amule.conf no tiene ECPort.");
        Port = port;
        hash = Value("ECPassword");
        if (hash.Length != 32) throw new InvalidDataException("amule.conf no tiene una contraseña EC válida.");
        IncomingPath = Value("IncomingDir").Replace('/', Path.DirectorySeparatorChar);
        TempPath = Value("TempDir").Replace('/', Path.DirectorySeparatorChar);
    }

    // False when nothing listens on the EC port; other failures (auth, version) throw.
    public async Task<bool> TryConnectAsync(CancellationToken token = default)
    {
        LoadConfig();
        Drop();
        var client = new EcClient();
        try
        {
            await client.ConnectAsync(Port, hash, token);
            Client = client;
            return true;
        }
        catch (SocketException) { client.Dispose(); return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { client.Dispose(); return false; }
        catch { client.Dispose(); throw; }
    }

    public void Drop()
    {
        Client?.Dispose();
        Client = null;
    }

    public bool PortInUse()
    {
        try
        {
            var probe = new TcpListener(IPAddress.Loopback, Port);
            probe.Start(); probe.Stop();
            return false;
        }
        catch (SocketException) { return true; }
    }

    public static bool GuiRunning => Process.GetProcessesByName("AmuleModern").Length > 0;
    public static bool EngineProcessRunning => Process.GetProcessesByName("amuled").Length > 0;

    public async Task StartAsync(CancellationToken token = default)
    {
        LoadConfig();
        if (Client is not null) throw new InvalidOperationException("El motor ya está en marcha.");
        if (!File.Exists(EnginePath)) throw new FileNotFoundException($"No encuentro el motor en {EnginePath}. Usa --motor.");
        if (PortInUse()) throw new IOException($"El puerto EC {Port} ya está ocupado: hay otro amuled de este perfil arrancando o colgado.");
        await LaunchDetachedAsync(EnginePath, $"-c \"{ProfilePath}\"", Path.GetDirectoryName(EnginePath)!, token);
        for (int attempt = 0; attempt < 75; attempt++)
        {
            await Task.Delay(200, token);
            if (await TryConnectAsync(token))
            {
                // Same as aMule Modern: eD2k on, autoconnect off, Kad back if it was enabled.
                await Client!.EnableEd2kAsync(token);
                await Client.ResumeKadAsync(token);
                await RestoreServersAsync(token);
                await RestoreQueueAsync(token);
                return;
            }
        }
        throw new TimeoutException("amuled no abrió la conexión EC a tiempo. Revisa el logfile del perfil.");
    }

    public async Task StopAsync(CancellationToken token = default)
    {
        var client = Client ?? throw new InvalidOperationException("El motor no está en marcha.");
        try { await client.RequestAsync(new(8), token); }
        catch (Exception) when (!client.IsConnected) { /* amuled may close the socket while answering */ }
        Drop();
        for (int attempt = 0; attempt < 75 && PortInUse(); attempt++) await Task.Delay(200, token);
        if (PortInUse()) throw new TimeoutException("El motor recibió la orden pero sigue escuchando. Espera unos segundos.");
    }

    // Same files as aMule Modern: amuled loads server.met itself; these copies recover the
    // list and the queue when the engine lost them, and keep the GUI's copy current.
    private string SnapshotPath(string name) => Path.Combine(ProfilePath, name);

    public async Task<int> RestoreServersAsync(CancellationToken token = default)
    {
        var client = Client ?? throw new InvalidOperationException("El motor no está en marcha.");
        string path = SnapshotPath("server-snapshot.txt");
        if (!File.Exists(path)) return 0;
        var wanted = ProfileSnapshot.ParseServers(await File.ReadAllTextAsync(path, token));
        if (wanted.Count == 0) return 0;
        var present = (await client.GetServersAsync(token)).Select(s => s.Endpoint).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = wanted.Where(s => !present.Contains(s.Address + ":" + s.Port)).ToArray();
        if (missing.Length == 0) return 0;
        return (await client.ImportServersAsync(missing, token)).Added;
    }

    private async Task RestoreQueueAsync(CancellationToken token)
    {
        var client = Client!;
        string path = SnapshotPath("queue-snapshot.txt");
        if (!File.Exists(path)) return;
        var wanted = ProfileSnapshot.ParseQueue(await File.ReadAllTextAsync(path, token));
        if (wanted.Count == 0) return;
        var present = (await client.GetDownloadsAsync(token)).Select(d => d.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in wanted)
        {
            if (!present.Contains(item.Hash))
            {
                try { await client.AddLinkAsync(item.Link, token); }
                catch (EcCommandException) { /* el parcial ya está en la cola o el enlace no cabe */ }
            }
            if (item.Paused)
            {
                try { await client.PauseAsync(item.Hash, true, token); }
                catch (EcCommandException) { }
            }
        }
    }

    // Skipped while the GUI is open: it writes the same files on its own timer.
    public void RememberSnapshots(IReadOnlyList<ServerItem> servers, IReadOnlyList<DownloadItem> downloads)
    {
        if (GuiRunning) return;
        if (servers.Count > 0) WriteSnapshot("server-snapshot.txt", ProfileSnapshot.FormatServers(servers));
        WriteSnapshot("queue-snapshot.txt", ProfileSnapshot.FormatQueue(downloads));
    }

    private void WriteSnapshot(string name, string text)
    {
        string path = SnapshotPath(name), temp = path + ".mula.tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }

    // A child of the SSH session dies with it; WMI creates the process outside that job.
    private static async Task LaunchDetachedAsync(string exe, string args, string workingDirectory, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
        {
            var unix = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
            unix.ArgumentList.Add("-c"); unix.ArgumentList.Add($"nohup \"{exe}\" {args} >/dev/null 2>&1 &");
            using var sh = Process.Start(unix) ?? throw new IOException("No se pudo lanzar amuled.");
            await sh.WaitForExitAsync(token);
            return;
        }
        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
        string script = $"$ProgressPreference = 'SilentlyContinue'; $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{{ CommandLine = {Quote($"\"{exe}\" {args}")}; CurrentDirectory = {Quote(workingDirectory)} }}; exit [int]$r.ReturnValue";
        // ReturnValue 8/9: WMI cannot see the path (typically an MSIX-virtualized folder).
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("No se pudo lanzar PowerShell.");
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(deadline.Token);
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0)
            throw new IOException(process.ExitCode is 8 or 9
                ? $"Windows no ve {exe} fuera de esta sesión (¿carpeta virtualizada?). Usa --motor y --perfil con la ruta real."
                : $"Win32_Process.Create falló ({process.ExitCode}). {stderr.Result.Trim()}");
    }

    public void Dispose() => Drop();
}
