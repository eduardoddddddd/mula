using AmuleModern.Amule;

namespace Mula;

sealed record Command(string Name, string Args, string Help, Func<string, Task> Run, bool ArgsOptional = true, string[]? Aliases = null);

sealed partial class App
{
    private IReadOnlyList<Command>? commands;
    private IReadOnlyList<Command> Commands => commands ??=
    [
        new("buscar", "<texto>", "Buscar en el servidor conectado", a => SearchAsync(a, "servidor"), false),
        new("global", "<texto>", "Búsqueda global en eD2k", a => SearchAsync(a, "global"), false),
        new("kad", "<texto> | on | off", "Buscar en Kad, o activar/desactivar Kad", KadAsync, false),
        new("parar", "", "Detener la búsqueda en curso", _ => StopSearchAsync()),
        new("rebuscar", "", "Relanzar en vivo la pestaña de búsqueda actual", _ => SearchAgainAsync()),
        new("cerrar", "", "Cerrar la pestaña de búsqueda actual (Ctrl+W)", _ => CloseSearchTabAsync()),
        new("add", "<ed2k://…>", "Añadir enlaces (o pégalos sin más)", AddLinksAsync, false, ["añadir", "enlace"]),
        new("pausa", "", "Pausar la selección", _ => PauseAsync(true), Aliases: ["pausar"]),
        new("reanudar", "", "Reanudar la selección", _ => PauseAsync(false)),
        new("cancelar", "", "Cancelar la selección (borra los temporales)", _ => DeleteSelectedAsync(View.Descargas)),
        new("limpiar", "", "Quitar completados de la lista (conserva el archivo)", _ => ClearCompletedAsync()),
        new("conectar", "[ip:puerto]", "Conectar al servidor seleccionado o al indicado", ConnectServerAsync),
        new("desconectar", "", "Cerrar la sesión eD2k", _ => Run("Desconectando…", async c => { await c.DisconnectServerAsync(life.Token); return "Desconectado de eD2k."; })),
        new("servidor", "<ip:puerto> [nombre]", "Añadir un servidor", AddServerAsync, false),
        new("quitar", "", "Quitar el servidor seleccionado", _ => DeleteSelectedAsync(View.Servidores)),
        new("restaurar", "", "Recargar la lista de servidores de aMule Modern", _ => RestoreServersAsync()),
        new("importar", "[url | ruta]", "Importar server.met o lista de texto", ImportAsync),
        new("limites", "[bajada subida]", "Ver o fijar límites en KiB/s (0 = sin límite)", LimitsAsync),
        new("recargar", "", "Releer las carpetas compartidas", _ => Run("Recargando compartidos…", async c => { await c.ReloadSharedFilesAsync(life.Token); return "Compartidos recargados."; })),
        new("motor", "[iniciar | parar | estado]", "Arrancar, parar o ver el motor amuled", MotorAsync),
        new("carpetas", "", "Ver dónde se guardan las descargas (las de aMule Modern)", _ =>
        {
            Say($"Completas: {engine.IncomingPath} · Temporales: {engine.TempPath}", Tone.Info);
            return Task.CompletedTask;
        }),
        new("ayuda", "", "Teclas y órdenes", _ => { ToggleHelp(); return Task.CompletedTask; }, Aliases: ["help"]),
        new("salir", "", "Cerrar mula (el motor sigue)", _ => { quit = true; return Task.CompletedTask; }, Aliases: ["exit", "quit"]),
    ];

    private List<Command> Suggestions()
    {
        if (confirm is not null || input.Length == 0 || input[0] != '/') return [];
        string text = input.ToString(1, input.Length - 1);
        if (text.Contains(' ')) return [];
        return [.. Commands.Where(c => c.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)
            || c.Aliases?.Any(a => a.StartsWith(text, StringComparison.OrdinalIgnoreCase)) == true).Take(10)];
    }

    private async Task RunCommandAsync(string text)
    {
        int space = text.IndexOf(' ');
        string name = space < 0 ? text : text[..space], args = space < 0 ? "" : text[(space + 1)..].Trim();
        var command = Commands.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || c.Aliases?.Contains(name, StringComparer.OrdinalIgnoreCase) == true)
            ?? Commands.Where(c => c.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray() switch { [var only] => only, _ => null };
        if (command is null) { Say($"Orden desconocida: /{name}. Escribe / para ver la lista.", Tone.Error); return; }
        if (!command.ArgsOptional && args.Length == 0) { Say($"Uso: /{command.Name} {command.Args}", Tone.Warn); return; }
        await command.Run(args);
    }

    // Runs one engine action and reports the outcome in the message line.
    private async Task Run(string busy, Func<EcClient, Task<string?>> action)
    {
        if (engine.Client is not { } client) { Say("El motor no está conectado. Usa /motor iniciar.", Tone.Error); return; }
        Say(busy, Tone.Info);
        Render();
        try
        {
            string? done = await action(client);
            if (done is not null) Say(done, Tone.Ok);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !life.IsCancellationRequested)
        {
            Say(ex.Message, Tone.Error);
            if (!client.IsConnected) engine.Drop();
        }
        await RefreshAsync();
    }

    private Task DefaultActionAsync() => view switch
    {
        View.Descargas => TogglePauseAsync(),
        View.Buscar => DownloadResultsAsync(),
        View.Servidores => ConnectServerAsync(""),
        _ => Task.CompletedTask
    };

    // ---------- Descargas ----------

    private async Task AddLinksAsync(string text)
    {
        var links = text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("ed2k://", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (links.Length == 0) { Say("No veo ningún enlace ed2k://.", Tone.Warn); return; }
        await Run("Añadiendo…", async c =>
        {
            foreach (var link in links) await c.AddLinkAsync(link, life.Token);
            return links.Length == 1 ? "Enlace añadido a la cola." : $"{links.Length} enlaces añadidos a la cola.";
        });
    }

    private Task TogglePauseAsync()
    {
        if (view != View.Descargas) return Task.CompletedTask;
        var targets = Targets(View.Descargas);
        var items = downloads.Where(d => targets.Contains(d.Hash) && !d.IsComplete).ToList();
        if (items.Count == 0) { Say("Nada que pausar o reanudar.", Tone.Info); return Task.CompletedTask; }
        return PauseAsync(!items.All(d => d.State == 7));
    }

    private Task PauseAsync(bool pause)
    {
        var targets = Targets(View.Descargas);
        var hashes = downloads.Where(d => targets.Contains(d.Hash) && !d.IsComplete).Select(d => d.Hash).ToList();
        if (hashes.Count == 0) { Say("Selecciona una descarga incompleta.", Tone.Warn); return Task.CompletedTask; }
        return Run(pause ? "Pausando…" : "Reanudando…", async c =>
        {
            await c.PauseAsync(hashes, pause, life.Token);
            marks[View.Descargas].Clear();
            return $"{hashes.Count} descarga(s) {(pause ? "pausada(s)" : "reanudada(s)")}.";
        });
    }

    private Task ClearCompletedAsync()
    {
        var marked = marks[View.Descargas];
        var done = downloads.Where(d => d.IsComplete && (marked.Count == 0 || marked.Contains(d.Hash))).ToList();
        if (done.Count == 0) { Say("No hay completados que quitar.", Tone.Info); return Task.CompletedTask; }
        return Run("Quitando completados…", async c =>
        {
            await c.ClearCompletedAsync([.. done.Select(d => d.EcId)], life.Token);
            return $"{done.Count} completado(s) quitado(s) de la lista. Los archivos siguen en Incoming.";
        });
    }

    private Task DeleteSelectedAsync() => view is View.Descargas or View.Servidores ? DeleteSelectedAsync(view) : Task.CompletedTask;

    private Task DeleteSelectedAsync(View target)
    {
        var keys = Targets(target);
        if (target == View.Descargas)
        {
            var items = downloads.Where(d => keys.Contains(d.Hash) && d.CanCancel).ToList();
            if (items.Count == 0) { Say("Selecciona una descarga incompleta (los completados se quitan con /limpiar).", Tone.Warn); return Task.CompletedTask; }
            string what = items.Count == 1 ? $"«{TextWidth.Fit(items[0].Name, 50).TrimEnd()}»" : $"{items.Count} descargas";
            Ask($"¿Cancelar {what} y borrar sus temporales?", () => Run("Cancelando…", async c =>
            {
                await c.CancelDownloadsAsync([.. items.Select(d => d.Hash)], life.Token);
                marks[View.Descargas].Clear();
                return $"{items.Count} descarga(s) cancelada(s).";
            }));
        }
        else
        {
            var items = servers.Where(s => keys.Contains(s.Endpoint)).ToList();
            if (items.Count == 0) { Say("Selecciona un servidor en la vista Servidores.", Tone.Warn); return Task.CompletedTask; }
            Ask($"¿Quitar {(items.Count == 1 ? items[0].Name : items.Count + " servidores")} de la lista?", () => Run("Quitando…", async c =>
            {
                foreach (var s in items) await c.RemoveServerAsync(s, life.Token);
                marks[View.Servidores].Clear();
                lastServers = DateTime.MinValue;
                return $"{items.Count} servidor(es) quitado(s).";
            }));
        }
        return Task.CompletedTask;
    }

    // ---------- Buscar ----------

    private async Task SearchAsync(string text, string kind)
    {
        await Run($"Buscando «{text}»…", async c =>
        {
            await c.StartSearchAsync(text, global: kind == "global", kad: kind == "kad", token: life.Token);
            if (searches.Current is { } old) old.SelectedKey = selectedKey.GetValueOrDefault(View.Buscar);
            int before = searches.Tabs.Count;
            searches.Start(text, kind);
            lastKind = kind;
            marks[View.Buscar].Clear();
            selectedKey[View.Buscar] = null; selectedIndex[View.Buscar] = 0; scrollTop[View.Buscar] = 0;
            view = View.Buscar;
            string frozen = before > 0 && searches.Tabs.Count > 1 ? " Las otras pestañas quedan congeladas con sus resultados." : "";
            return $"Búsqueda {kind} lanzada.{frozen}";
        });
    }

    private Task StopSearchAsync() => Run("Deteniendo búsqueda…", async c =>
    {
        await c.StopSearchAsync(life.Token);
        if (searches.LiveTab is { } live) { live.Live = false; searches.Save(); }
        return "Búsqueda detenida. Sus resultados se quedan en la pestaña.";
    });

    private Task SearchAgainAsync()
    {
        if (searches.Current is { } tab) return SearchAsync(tab.Query, tab.Kind);
        Say("No hay ninguna pestaña de búsqueda.", Tone.Info);
        return Task.CompletedTask;
    }

    private Task KadAsync(string args)
    {
        string a = args.ToLowerInvariant();
        if (a is "on" or "activar" or "off" or "desactivar")
        {
            bool on = a is "on" or "activar";
            return Run(on ? "Activando Kad…" : "Desactivando Kad…", async c =>
            {
                await c.SetKadEnabledAsync(on, life.Token);
                return on ? "Kad activado. Tarda un poco en conectar." : "Kad desactivado.";
            });
        }
        return SearchAsync(args, "kad");
    }

    private Task DownloadResultsAsync()
    {
        var keys = Targets(View.Buscar);
        var queued = downloads.Select(d => d.Hash).ToHashSet();
        var wanted = results.Where(r => keys.Contains(r.Hash) && !queued.Contains(r.Hash)).ToList();
        if (wanted.Count == 0) { Say(keys.Count == 0 ? "No hay resultados seleccionados." : "Ya está en la cola.", Tone.Info); return Task.CompletedTask; }
        bool live = searches.Current?.Live == true;
        return Run("Añadiendo a la cola…", async c =>
        {
            foreach (var r in wanted)
            {
                // A frozen tab is no longer in the engine's result list: queue it by link.
                if (live)
                {
                    try { await c.DownloadSearchResultAsync(r.Hash, life.Token); continue; }
                    catch (EcCommandException) { }
                }
                if (r.Name.Contains('|')) throw new EcCommandException($"«{r.Name}» tiene un nombre que no cabe en un enlace ed2k; usa /rebuscar.");
                await c.AddLinkAsync(SearchTab.Link(r), life.Token);
            }
            marks[View.Buscar].Clear();
            return wanted.Count == 1 ? $"«{TextWidth.Fit(wanted[0].Name, 60).TrimEnd()}» en cola." : $"{wanted.Count} archivos en cola.";
        });
    }

    // ---------- Servidores ----------

    private Task ConnectServerAsync(string args)
    {
        ServerItem? server;
        if (args.Length > 0)
        {
            server = servers.FirstOrDefault(s => s.Endpoint == args || s.Name.Contains(args, StringComparison.OrdinalIgnoreCase));
            if (server is null && args.Contains(':'))
                return Run("Añadiendo y conectando…", async c =>
                {
                    int colon = args.LastIndexOf(':');
                    var added = await c.AddServerAsync(args[..colon], args[(colon + 1)..], "", life.Token);
                    await c.ConnectServerAsync(added, life.Token);
                    lastServers = DateTime.MinValue;
                    return $"Conectando a {added.Endpoint}…";
                });
        }
        else
        {
            int index = Selected(View.Servidores);
            server = index >= 0 ? servers[index] : null;
        }
        if (server is null) { Say(servers.Count == 0 ? "No hay servidores. Prueba /restaurar o /importar." : "No encuentro ese servidor.", Tone.Warn); return Task.CompletedTask; }
        return Run($"Conectando a {server.Name}…", async c =>
        {
            await c.ConnectServerAsync(server, life.Token);
            return $"Conectando a {server.Name} ({server.Endpoint})… el estado aparece arriba.";
        });
    }

    private Task AddServerAsync(string args)
    {
        string[] parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        int colon = parts[0].LastIndexOf(':');
        if (colon <= 0) { Say("Uso: /servidor ip:puerto [nombre]", Tone.Warn); return Task.CompletedTask; }
        return Run("Añadiendo servidor…", async c =>
        {
            var added = await c.AddServerAsync(parts[0][..colon], parts[0][(colon + 1)..], parts.Length > 1 ? parts[1] : "", life.Token);
            lastServers = DateTime.MinValue;
            return $"Servidor {added.Name} ({added.Endpoint}) en la lista.";
        });
    }

    private Task RestoreServersAsync() => Run("Cargando la lista de aMule Modern…", async _ =>
    {
        int added = await engine.RestoreServersAsync(life.Token);
        lastServers = DateTime.MinValue;
        ShowView(View.Servidores);
        return added == 0 ? "La lista ya coincide con la de aMule Modern." : $"{added} servidor(es) recuperado(s) de aMule Modern.";
    });

    private Task ImportAsync(string args)
    {
        string source = args.Length == 0 ? ServerListFile.ExampleUrl : args.Trim('"');
        return Run($"Importando {source}…", async c =>
        {
            byte[] data = source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? await ServerListFile.DownloadAsync(source, life.Token)
                : new FileInfo(source).Length > ServerListFile.MaxBytes ? throw new ArgumentException("El archivo es demasiado grande.") : await File.ReadAllBytesAsync(source, life.Token);
            var (added, renamed) = await c.ImportServersAsync(ServerListFile.Parse(data), life.Token);
            lastServers = DateTime.MinValue;
            ShowView(View.Servidores);
            return $"Importados: {added} nuevos, {renamed} renombrados.";
        });
    }

    // ---------- Ajustes y motor ----------

    private Task LimitsAsync(string args)
    {
        string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return Run("Leyendo límites…", async c =>
            {
                limits = await c.GetBandwidthAsync(life.Token);
                string Text(uint v) => v == 0 ? "sin límite" : $"{v} KiB/s";
                return $"Bajada {Text(limits.DownloadKib)} · subida {Text(limits.UploadKib)}.";
            });
        if (parts.Length != 2 || !uint.TryParse(parts[0], out uint down) || !uint.TryParse(parts[1], out uint up))
        {
            Say("Uso: /limites <bajada> <subida> en KiB/s, 0 = sin límite.", Tone.Warn);
            return Task.CompletedTask;
        }
        return Run("Aplicando límites…", async c =>
        {
            await c.SetBandwidthAsync(down, up, life.Token);
            return $"Límites: bajada {(down == 0 ? "sin límite" : down + " KiB/s")}, subida {(up == 0 ? "sin límite" : up + " KiB/s")}.";
        });
    }

    private async Task MotorAsync(string args)
    {
        switch (args.ToLowerInvariant())
        {
            case "iniciar" or "start" or "arrancar":
                if (engine.Client is not null) { Say("El motor ya está en marcha.", Tone.Info); return; }
                await StartEngineAsync();
                return;
            case "parar" or "stop" or "detener":
                if (engine.Client is null) { Say("El motor no está conectado.", Tone.Info); return; }
                string warning = guiRunning ? " aMule Modern está abierto y lo verá como una caída." : "";
                Ask($"¿Parar amuled? Las descargas se detienen hasta que lo vuelvas a arrancar.{warning}", async () =>
                {
                    Say("Parando el motor…", Tone.Info);
                    Render();
                    try
                    {
                        try { engine.RememberSnapshots(await engine.Client!.GetServersAsync(life.Token), await engine.Client.GetDownloadsAsync(life.Token)); }
                        catch (Exception) { /* la copia no impide parar */ }
                        await engine.StopAsync(life.Token);
                        network = null; speedDown = speedUp = 0;
                        lastConnectAttempt = DateTime.UtcNow.AddMinutes(10); // no reconectar solo
                        Say("Motor parado. /motor iniciar lo vuelve a arrancar.", Tone.Ok);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { Say(ex.Message, Tone.Error); }
                });
                return;
            default:
                string state = engine.Client is not null ? $"conectado, aMule {engine.Version}" : engine.PortInUse() ? "puerto ocupado, sin respuesta" : "parado";
                Say($"Motor {state} · EC 127.0.0.1:{engine.Port} · perfil {engine.ProfilePath}", Tone.Info);
                return;
        }
    }
}
