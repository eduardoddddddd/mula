using System.Text;
using System.Threading.Channels;
using AmuleModern.Amule;

namespace Mula;

enum View { Descargas, Buscar, Servidores, Compartidos, Registro, Ayuda }
enum Tone { Info, Ok, Warn, Error }

sealed partial class App(Engine engine, bool autoStart)
{
    private static readonly View[] Tabs = [View.Descargas, View.Buscar, View.Servidores, View.Compartidos, View.Registro];
    private readonly Terminal term = new();
    private readonly Channel<TermEvent> events = Channel.CreateUnbounded<TermEvent>();
    private readonly CancellationTokenSource life = new();
    private View view = View.Descargas, beforeHelp = View.Descargas;
    private bool quit;

    // Engine state
    private IReadOnlyList<DownloadItem> downloads = [];
    private readonly Dictionary<string, double> averages = [];
    private readonly SearchStore searches = new(SearchStore.DefaultPath());
    private IReadOnlyList<SearchResult> results => searches.Current?.Results ?? [];
    private string lastKind = "servidor";
    private IReadOnlyList<ServerItem> servers = [];
    private IReadOnlyList<SharedFile> shared = [];
    private string[] log = [];
    private NetworkState? network;
    private BandwidthLimits? limits;
    private ulong speedDown, speedUp;
    private DateTime lastConnectAttempt, lastServers, lastSnapshot;
    private bool guiRunning;
    private int ticks;

    // Selection, kept by key so refreshes do not move the cursor to another row.
    private readonly Dictionary<View, string?> selectedKey = [];
    private readonly Dictionary<View, int> selectedIndex = [];
    private readonly Dictionary<View, int> scrollTop = [];
    private readonly Dictionary<View, HashSet<string>> marks = new() { [View.Descargas] = [], [View.Buscar] = [], [View.Servidores] = [] };
    private int logScroll;

    // Prompt
    private readonly StringBuilder input = new();
    private int cursor, popupIndex;
    private readonly List<string> history = [];
    private int historyIndex = -1;
    private (string Text, Tone Tone) message = ("", Tone.Info);
    private (string Question, Func<Task> Action)? confirm;

    public async Task RunAsync()
    {
        term.Enter();
        try
        {
            _ = Task.Run(ReadKeys);
            _ = Task.Run(Tick);
            Say("Conectando con el motor…", Tone.Info);
            Render();
            await ConnectAsync(initial: true);
            Render();
            while (!quit)
            {
                var next = await events.Reader.ReadAsync(life.Token);
                do
                {
                    if (next is KeyEvent key) await HandleKeyAsync(key.Info);
                    else if (next is TickEvent && ++ticks % 8 == 0) await RefreshAsync();
                }
                while (!quit && events.Reader.TryRead(out next));
                if (!quit) Render();
            }
        }
        finally
        {
            life.Cancel();
            term.Dispose();
            engine.Dispose();
        }
        if (engine.PortInUse()) Console.WriteLine("mula: el motor sigue en marcha. Para pararlo: mula parar (o /motor parar dentro de mula).");
    }

    private void ReadKeys()
    {
#if DEBUG
        // Test hook: MULA_KEYS="abc{enter}{wait}{tab}" types into the app before the real keyboard.
        if (Environment.GetEnvironmentVariable("MULA_KEYS") is { Length: > 0 } script) PlayKeys(script);
#endif
        while (!life.IsCancellationRequested)
        {
            try { events.Writer.TryWrite(new KeyEvent(Console.ReadKey(intercept: true))); }
            catch (InvalidOperationException) { quit = true; events.Writer.TryWrite(new TickEvent()); return; }
        }
    }

#if DEBUG
    private void PlayKeys(string script)
    {
        static ConsoleKeyInfo K(ConsoleKey key, char c = '\0', bool shift = false, bool ctrl = false) => new(c, key, shift, false, ctrl);
        Thread.Sleep(1500);
        for (int i = 0; i < script.Length; i++)
        {
            ConsoleKeyInfo? key = null;
            if (script[i] == '{' && script.IndexOf('}', i) is var end and > 0)
            {
                string token = script[(i + 1)..end];
                i = end;
                key = token switch
                {
                    "enter" => K(ConsoleKey.Enter, '\r'), "tab" => K(ConsoleKey.Tab, '\t'), "s-tab" => K(ConsoleKey.Tab, '\t', true),
                    "up" => K(ConsoleKey.UpArrow), "down" => K(ConsoleKey.DownArrow), "left" => K(ConsoleKey.LeftArrow), "right" => K(ConsoleKey.RightArrow),
                    "esc" => K(ConsoleKey.Escape, '\x1b'), "del" => K(ConsoleKey.Delete), "bs" => K(ConsoleKey.Backspace, '\b'), "space" => K(ConsoleKey.Spacebar, ' '),
                    "c-left" => K(ConsoleKey.LeftArrow, ctrl: true), "c-right" => K(ConsoleKey.RightArrow, ctrl: true), "c-w" => K(ConsoleKey.W, '\x17', ctrl: true),
                    _ => null
                };
                if (token.StartsWith("wait")) { Thread.Sleep(token.Length > 4 ? int.Parse(token[4..]) * 1000 : 1000); continue; }
            }
            else key = new ConsoleKeyInfo(script[i], ConsoleKey.NoName, false, false, false);
            if (key is { } k) events.Writer.TryWrite(new KeyEvent(k));
            Thread.Sleep(60);
        }
    }
#endif

    // 250 ms: notices terminal resizes and animates the spinner; data refreshes every 2 s.
    private async Task Tick()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(life.Token)) events.Writer.TryWrite(new TickEvent());
    }

    private void Say(string text, Tone tone) => message = (text, tone);

    private async Task ConnectAsync(bool initial = false)
    {
        lastConnectAttempt = DateTime.UtcNow;
        try
        {
            if (await engine.TryConnectAsync(life.Token))
            {
                Say($"Conectado a aMule {engine.Version} (EC 127.0.0.1:{engine.Port}).", Tone.Ok);
                await RefreshAsync();
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Say(ex.Message, Tone.Error);
            return;
        }
        if (!initial) return;
        if (engine.PortInUse())
            Say("El puerto EC está ocupado pero el motor no responde. Reintentando…", Tone.Warn);
        else if (autoStart)
            await StartEngineAsync();
        else
            Ask("El motor no está en marcha. ¿Arrancarlo ahora en segundo plano?", StartEngineAsync);
    }

    private async Task StartEngineAsync()
    {
        Say("Arrancando amuled… (restaura servidores y cola del perfil)", Tone.Info);
        Render();
        try
        {
            await engine.StartAsync(life.Token);
            Say($"Motor en marcha (aMule {engine.Version}). Seguirá activo aunque cierres mula o la sesión SSH.", Tone.Ok);
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Say(ex.Message, Tone.Error); }
    }

    private async Task RefreshAsync()
    {
        guiRunning = Engine.GuiRunning;
        if (engine.Client is not { } client)
        {
            if (DateTime.UtcNow - lastConnectAttempt > TimeSpan.FromSeconds(5)) await ConnectAsync();
            return;
        }
        try
        {
            var stats = await client.RequestAsync(new(0x0a, EcTag.Integer(4, 0)), life.Token);
            speedDown = stats.Find(0x201)?.Number ?? 0;
            speedUp = stats.Find(0x200)?.Number ?? 0;
            if (stats.Find(5) is { } net) network = NetworkState.FromTag(net);
            downloads = Smooth(await client.GetDownloadsAsync(life.Token));
            bool serversDue = DateTime.UtcNow - lastServers > TimeSpan.FromSeconds(15);
            if (view == View.Servidores || serversDue)
            {
                servers = Sort(await client.GetServersAsync(life.Token));
                lastServers = DateTime.UtcNow;
            }
            if (view == View.Compartidos) shared = [.. (await client.GetSharedFilesAsync(life.Token)).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)];
            if (view == View.Registro) log = (await client.GetActivityLogAsync(life.Token)).Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (SearchActive && searches.LiveTab is { } live)
            {
                var fresh = (await client.GetSearchResultsAsync(life.Token)).OrderByDescending(r => r.Sources).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                // Never let an empty engine list (restart, another client's search) wipe a tab.
                if (fresh.Count > 0 && (fresh.Count != live.Results.Count || fresh.Sum(r => (long)r.Sources) != live.Results.Sum(r => (long)r.Sources)))
                {
                    live.Results = fresh;
                    searches.Save();
                }
            }
            if (DateTime.UtcNow - lastSnapshot > TimeSpan.FromSeconds(15))
            {
                lastSnapshot = DateTime.UtcNow;
                try { engine.RememberSnapshots(servers, downloads); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !life.IsCancellationRequested)
        {
            if (!client.IsConnected)
            {
                engine.Drop();
                network = null; speedDown = speedUp = 0;
                Say("Se perdió la conexión con el motor. Reintentando cada 5 s… (/motor iniciar si se paró)", Tone.Warn);
            }
            else Say(ex.Message, Tone.Error);
        }
    }

    private bool SearchActive => searches.LiveTab is { } live && DateTime.UtcNow - live.Started < TimeSpan.FromSeconds(90);

    private IReadOnlyList<DownloadItem> Smooth(IReadOnlyList<DownloadItem> items)
    {
        var seen = new HashSet<string>();
        var list = new List<DownloadItem>(items.Count);
        foreach (var item in items)
        {
            seen.Add(item.Hash);
            double avg = averages.TryGetValue(item.Hash, out double old) ? old * 0.7 + item.Speed * 0.3 : item.Speed;
            if (item.State == 7 || item.IsComplete) avg = 0;
            averages[item.Hash] = avg;
            list.Add(item with { AverageSpeed = avg });
        }
        foreach (var gone in averages.Keys.Where(k => !seen.Contains(k)).ToArray()) averages.Remove(gone);
        return [.. list.OrderBy(d => d.IsComplete).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private IReadOnlyList<ServerItem> Sort(IReadOnlyList<ServerItem> items)
    {
        string? current = network?.Server?.Endpoint;
        return [.. items.OrderByDescending(s => s.Endpoint == current).ThenByDescending(s => s.Users).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private void Ask(string question, Func<Task> action) => confirm = (question, action);

    // ---------- Keyboard ----------

    private async Task HandleKeyAsync(ConsoleKeyInfo key)
    {
        bool ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        if (confirm is { } pending)
        {
            confirm = null;
            if (key.KeyChar is 's' or 'S' or 'y' or 'Y' || key.Key == ConsoleKey.Enter) await pending.Action();
            else Say("Cancelado.", Tone.Info);
            return;
        }
        bool empty = input.Length == 0;
        var suggestions = Suggestions();
        if (ctrl && key.Key == ConsoleKey.C || key.KeyChar == '\x03')
        {
            if (!empty) { ClearInput(); return; }
            quit = true; return;
        }
        if (ctrl && key.Key == ConsoleKey.D || key.KeyChar == '\x04') { if (empty) quit = true; return; }
        if (ctrl && key.Key == ConsoleKey.L || key.KeyChar == '\x0c') { term.Invalidate(); return; }
        if (ctrl && key.Key == ConsoleKey.U || key.KeyChar == '\x15') { input.Remove(0, cursor); cursor = 0; return; }
        if (ctrl && key.Key == ConsoleKey.A || key.KeyChar == '\x01') { cursor = 0; return; }
        if (ctrl && key.Key == ConsoleKey.E || key.KeyChar == '\x05') { cursor = input.Length; return; }
        if (ctrl && key.Key == ConsoleKey.W || key.KeyChar == '\x17') { if (empty && view == View.Buscar) await CloseSearchTabAsync(); else DeleteWord(); return; }
        if (view == View.Buscar && ctrl && key.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow) { SwitchSearchTab(key.Key == ConsoleKey.LeftArrow ? -1 : 1); return; }
        if (view == View.Buscar && key.Modifiers.HasFlag(ConsoleModifiers.Alt) && key.KeyChar is >= '1' and <= '9') { SelectSearchTab(key.KeyChar - '1'); return; }
        if (ctrl && key.Key == ConsoleKey.P || key.KeyChar == '\x10') { await TogglePauseAsync(); return; }

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                if (suggestions.Count > 0 && !input.ToString().Contains(' '))
                {
                    var chosen = suggestions[Math.Clamp(popupIndex, 0, suggestions.Count - 1)];
                    SetInput("/" + chosen.Name + (chosen.Args.Length > 0 && !chosen.ArgsOptional ? " " : ""));
                    if (chosen.Args.Length > 0 && !chosen.ArgsOptional) return;
                }
                await SubmitAsync();
                return;
            case ConsoleKey.Tab:
                if (suggestions.Count > 0)
                {
                    var chosen = suggestions[Math.Clamp(popupIndex, 0, suggestions.Count - 1)];
                    SetInput("/" + chosen.Name + (chosen.Args.Length > 0 ? " " : ""));
                }
                else SwitchView(key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? -1 : 1);
                return;
            case ConsoleKey.Escape:
                if (!empty) ClearInput();
                else if (view == View.Ayuda) view = beforeHelp;
                else if (marks.TryGetValue(view, out var set) && set.Count > 0) { set.Clear(); Say("Selección vaciada.", Tone.Info); }
                return;
            case ConsoleKey.UpArrow:
                if (suggestions.Count > 0) { popupIndex = (popupIndex - 1 + suggestions.Count) % suggestions.Count; return; }
                if (key.Modifiers.HasFlag(ConsoleModifiers.Alt) || (!empty && input[0] != '/')) { History(-1); return; }
                Move(-1); return;
            case ConsoleKey.DownArrow:
                if (suggestions.Count > 0) { popupIndex = (popupIndex + 1) % suggestions.Count; return; }
                if (key.Modifiers.HasFlag(ConsoleModifiers.Alt) || historyIndex >= 0) { History(1); return; }
                Move(1); return;
            case ConsoleKey.PageUp: Move(-Math.Max(1, ListHeight - 1)); return;
            case ConsoleKey.PageDown: Move(Math.Max(1, ListHeight - 1)); return;
            case ConsoleKey.Home: if (empty) Move(int.MinValue / 2); else cursor = 0; return;
            case ConsoleKey.End: if (empty) Move(int.MaxValue / 2); else cursor = input.Length; return;
            case ConsoleKey.LeftArrow:
                if (empty) SwitchView(-1); else if (cursor > 0) cursor--;
                return;
            case ConsoleKey.RightArrow:
                if (empty) SwitchView(1); else if (cursor < input.Length) cursor++;
                return;
            case ConsoleKey.Backspace:
                if (cursor > 0) { input.Remove(--cursor, 1); popupIndex = 0; }
                return;
            case ConsoleKey.Delete:
                if (!empty) { if (cursor < input.Length) input.Remove(cursor, 1); }
                else await DeleteSelectedAsync();
                return;
        }
        char c = key.KeyChar;
        if (c == '\0' || char.IsControl(c)) return;
        if (empty && c == ' ') { ToggleMark(); return; }
        if (empty && c == '?') { ToggleHelp(); return; }
        input.Insert(cursor++, c);
        popupIndex = 0;
        historyIndex = -1;
    }

    private void SetInput(string text) { input.Clear().Append(text); cursor = input.Length; popupIndex = 0; }
    private void ClearInput() { input.Clear(); cursor = 0; popupIndex = 0; historyIndex = -1; }

    private void DeleteWord()
    {
        int start = cursor;
        while (start > 0 && input[start - 1] == ' ') start--;
        while (start > 0 && input[start - 1] != ' ') start--;
        input.Remove(start, cursor - start);
        cursor = start;
    }

    private void History(int direction)
    {
        if (history.Count == 0) return;
        if (historyIndex < 0) historyIndex = history.Count;
        historyIndex = Math.Clamp(historyIndex + direction, 0, history.Count);
        if (historyIndex == history.Count) { ClearInput(); return; }
        SetInput(history[historyIndex]);
    }

    private void SwitchView(int direction)
    {
        int at = Array.IndexOf(Tabs, view == View.Ayuda ? beforeHelp : view);
        view = Tabs[(at + direction + Tabs.Length) % Tabs.Length];
        _ = RefreshSoonAsync();
    }

    private void ShowView(View target)
    {
        view = target;
        _ = RefreshSoonAsync();
    }

    // Loads the new view's data on the next loop iteration instead of waiting up to 2 s.
    private Task RefreshSoonAsync()
    {
        ticks = 7;
        events.Writer.TryWrite(new TickEvent());
        return Task.CompletedTask;
    }

    private void SwitchSearchTab(int direction)
    {
        if (searches.Tabs.Count < 2) return;
        SelectSearchTab((searches.Active + direction + searches.Tabs.Count) % searches.Tabs.Count);
    }

    // Each tab remembers its own cursor; marks belong to the tab being left.
    private void SelectSearchTab(int index)
    {
        if (index < 0 || index >= searches.Tabs.Count || index == searches.Active) return;
        if (searches.Current is { } old) old.SelectedKey = selectedKey.GetValueOrDefault(View.Buscar);
        searches.Active = index;
        selectedKey[View.Buscar] = searches.Current!.SelectedKey;
        selectedIndex[View.Buscar] = 0;
        scrollTop[View.Buscar] = 0;
        marks[View.Buscar].Clear();
    }

    private async Task CloseSearchTabAsync()
    {
        if (searches.Current is not { } tab) return;
        if (tab.Live && engine.Client is { } client)
        {
            try { await client.StopSearchAsync(life.Token); } catch (EcCommandException) { }
        }
        searches.Close(searches.Active);
        selectedKey[View.Buscar] = searches.Current?.SelectedKey;
        marks[View.Buscar].Clear();
        Say($"Pestaña «{tab.Query}» cerrada.", Tone.Info);
    }

    private void ToggleHelp()
    {
        if (view == View.Ayuda) view = beforeHelp;
        else { beforeHelp = view; view = View.Ayuda; helpScroll = 0; }
    }

    private async Task SubmitAsync()
    {
        string text = input.ToString().Trim();
        ClearInput();
        if (text.Length == 0) { await DefaultActionAsync(); return; }
        if (history.Count == 0 || history[^1] != text) history.Add(text);
        if (text.StartsWith('/')) { await RunCommandAsync(text[1..]); return; }
        if (text.StartsWith("ed2k://", StringComparison.OrdinalIgnoreCase)) { await AddLinksAsync(text); return; }
        await SearchAsync(text, lastKind);
    }
}
