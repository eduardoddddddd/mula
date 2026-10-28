using System.Text;
using AmuleModern.Amule;

namespace Mula;

sealed record Col<T>(string Title, int Width, bool Right, int Drop, Func<T, string> Text, Func<T, string>? Style = null, Action<Line, T, int>? Custom = null);

sealed partial class App
{
    private const string Spinner = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";
    private int listHeight = 10, inputOffset;
    private string position = "";
    private int helpScroll;
    private int ListHeight => listHeight;

    // ---------- Selection ----------

    private int Count(View v) => v switch
    {
        View.Descargas => downloads.Count, View.Buscar => results.Count, View.Servidores => servers.Count,
        View.Compartidos => shared.Count, _ => 0
    };

    private string KeyAt(View v, int i) => v switch
    {
        View.Descargas => downloads[i].Hash, View.Buscar => results[i].Hash, View.Servidores => servers[i].Endpoint,
        View.Compartidos => shared[i].Hash, _ => i.ToString()
    };

    private int Selected(View v)
    {
        int count = Count(v);
        if (count == 0) return -1;
        if (selectedKey.GetValueOrDefault(v) is string key)
            for (int i = 0; i < count; i++)
                if (KeyAt(v, i) == key) { selectedIndex[v] = i; return i; }
        int index = Math.Clamp(selectedIndex.GetValueOrDefault(v), 0, count - 1);
        selectedIndex[v] = index;
        selectedKey[v] = KeyAt(v, index);
        return index;
    }

    private void Move(int delta)
    {
        if (view == View.Ayuda) { helpScroll = Math.Max(0, helpScroll + delta); return; }
        if (view == View.Registro) { logScroll = Math.Clamp(logScroll - delta, 0, Math.Max(0, log.Length - 1)); return; }
        int count = Count(view);
        if (count == 0) return;
        int index = (int)Math.Clamp((long)Selected(view) + delta, 0, count - 1);
        selectedIndex[view] = index;
        selectedKey[view] = KeyAt(view, index);
    }

    private void ToggleMark()
    {
        if (!marks.TryGetValue(view, out var set) || Selected(view) is not (>= 0 and var index)) return;
        string key = KeyAt(view, index);
        if (!set.Remove(key)) set.Add(key);
        Move(1);
    }

    // Marked rows when there are any, otherwise the row under the cursor.
    private List<string> Targets(View v)
    {
        var keys = Enumerable.Range(0, Count(v)).Select(i => KeyAt(v, i)).ToList();
        if (marks.TryGetValue(v, out var set) && set.Count > 0)
        {
            var chosen = keys.Where(set.Contains).ToList();
            if (chosen.Count > 0) return chosen;
        }
        int index = Selected(v);
        return index < 0 ? [] : [keys[index]];
    }

    // ---------- Frame ----------

    private void Render()
    {
        var (width, height) = Terminal.Size;
        int w = width - 1;
        var lines = new List<Line> { Header(w), TabLine(w), new Line().Add(new string('─', w), Theme.Muted) };
        int body = Math.Max(1, height - 3 - 5);
        var bodyLines = Body(w, body);
        while (bodyLines.Count < body) bodyLines.Add(new Line());
        if (bodyLines.Count > body) bodyLines.RemoveRange(body, bodyLines.Count - body);
        var suggestions = Suggestions();
        int shown = Math.Min(suggestions.Count, Math.Max(0, body - 1));
        for (int i = 0; i < shown; i++)
        {
            var s = suggestions[i];
            bool on = i == Math.Clamp(popupIndex, 0, suggestions.Count - 1);
            var line = new Line().Add(on ? " ❯ " : "   ", Theme.Accent)
                .Cell("/" + s.Name + (s.Args.Length > 0 ? " " + s.Args : ""), Math.Min(34, w / 2), on ? Theme.AccentBold : Theme.Plain)
                .Add(" " + s.Help, Theme.Muted);
            bodyLines[body - shown + i] = line;
        }
        lines.AddRange(bodyLines);
        lines.Add(MessageLine());
        var (box, cursorCol) = InputBox(w);
        lines.AddRange(box);
        lines.Add(Footer(w));
        term.Draw(lines, width, height, confirm is null ? (height - 3, cursorCol) : null);
    }

    private Line Header(int w)
    {
        var left = new Line().Add(" ◆ mula", Theme.AccentBold);
        if (engine.Client is null) left.Add("  motor parado", Theme.Error);
        else left.Add($"  aMule {engine.Version}", Theme.Muted);
        if (guiRunning) left.Add("  · GUI abierta", Theme.Warn);
        var right = new Line();
        if (engine.Client is not null && network is { } net)
        {
            string ed2kColor = net.Connected ? Theme.Ok : net.Connecting ? Theme.Warn : Theme.Error;
            right.Add("eD2k ", Theme.Muted).Add("●", ed2kColor).Add(" " + net.Ed2kText.Replace("Conectado · ", ""));
            if (net.Connected && net.Server is { } server) right.Add(" · " + TextWidth.Fit(server.Name, 22).TrimEnd(), Theme.Muted);
            right.Add("   Kad ", Theme.Muted).Add(net.KadConnected ? "●" : "○", net.KadConnected ? Theme.Ok : net.KadRunning ? Theme.Warn : Theme.Muted);
            right.Add("   ↓ ", Theme.Info).Add(DownloadItem.FormatBytes(speedDown) + "/s");
            right.Add("  ↑ ", Theme.Accent).Add(DownloadItem.FormatBytes(speedUp) + "/s ");
        }
        return left.Right(right, w);
    }

    private Line TabLine(int w)
    {
        var line = new Line().Add(" ");
        View active = view == View.Ayuda ? beforeHelp : view;
        foreach (var tab in Tabs)
        {
            string label = tab switch
            {
                View.Descargas => $"Descargas {downloads.Count}",
                View.Buscar => SearchActive ? $"Buscar {Spinner[ticks % Spinner.Length]} {searches.LiveTab!.Results.Count}" : searches.Tabs.Count > 0 ? $"Buscar ({searches.Tabs.Count})" : "Buscar",
                View.Servidores => $"Servidores {servers.Count}",
                _ => tab.ToString()
            };
            line.Add(label, tab == active && view != View.Ayuda ? Theme.AccentBold + Ansi.Underline : Theme.Muted).Add("   ");
        }
        return line.Right(new Line().Add(view == View.Ayuda ? "Esc vuelve " : "? ayuda ", Theme.Muted), w);
    }

    private Line MessageLine()
    {
        var (text, tone) = message;
        if (text.Length == 0) return new Line();
        var (icon, color) = tone switch
        {
            Tone.Ok => ("✓", Theme.Ok), Tone.Warn => ("!", Theme.Warn), Tone.Error => ("✗", Theme.Error), _ => ("›", Theme.Info)
        };
        return new Line().Add(" " + icon + " ", color).Add(text, tone == Tone.Error ? Theme.Error : Theme.Plain);
    }

    private (List<Line> Lines, int CursorCol) InputBox(int w)
    {
        int inner = Math.Max(4, w - 6);
        string border = confirm is null ? Theme.Muted : Theme.Warn;
        var top = new Line().Add("╭" + new string('─', w - 2) + "╮", border);
        var bottom = new Line().Add("╰" + new string('─', w - 2) + "╯", border);
        var middle = new Line().Add("│ ", border);
        int cursorCol = 4;
        if (confirm is { } pending)
        {
            middle.Add("? ", Theme.Warn).Cell(pending.Question + "  (s/n)", inner, Theme.Warn);
        }
        else
        {
            middle.Add("› ", Theme.AccentBold);
            string text = input.ToString();
            if (text.Length == 0)
            {
                middle.Cell(Placeholder(), inner, Theme.Muted);
                inputOffset = 0;
            }
            else
            {
                if (cursor < inputOffset) inputOffset = cursor;
                while (inputOffset < cursor && TextWidth.Of(text[inputOffset..cursor]) > inner - 1) inputOffset++;
                var visible = new StringBuilder();
                int used = 0;
                for (int i = inputOffset; i < text.Length; i++)
                {
                    int cw = TextWidth.Of(text[i].ToString());
                    if (used + cw > inner) break;
                    visible.Append(text[i]); used += cw;
                }
                middle.Cell(visible.ToString(), inner);
                cursorCol = 4 + TextWidth.Of(text[inputOffset..cursor]);
            }
        }
        middle.Add(" │", border);
        return ([top, middle, bottom], cursorCol);
    }

    private string Placeholder() => view switch
    {
        View.Buscar => "Escribe y Enter para buscar · /global · /kad · Enter vacío descarga la fila",
        View.Servidores => "Enter vacío conecta · /importar · /restaurar · /servidor ip:puerto",
        View.Compartidos => "/recargar relee las carpetas compartidas",
        View.Registro => "↑↓ desplazan el registro del motor",
        _ => "Pega un ed2k://, escribe para buscar o / para ver comandos"
    };

    private Line Footer(int w)
    {
        string hints = view switch
        {
            View.Descargas => "Tab vista · ↑↓ mover · Espacio marca · Enter pausa/reanuda · Supr cancela · /limpiar",
            View.Buscar => "Tab vista · ↑↓ mover · Espacio marca · Enter descarga · Ctrl+←→ pestañas · Ctrl+W cierra",
            View.Servidores => "Tab vista · ↑↓ mover · Enter conecta · Supr quita",
            _ => "Tab vista · ↑↓ mover · / comandos"
        };
        return new Line().Add(" " + hints, Theme.Muted).Right(new Line().Add("Ctrl+C salir ", Theme.Muted), w);
    }

    // ---------- Bodies ----------

    private List<Line> Body(int w, int height)
    {
        if (view == View.Ayuda)
        {
            var help = HelpBody(w);
            helpScroll = Math.Clamp(helpScroll, 0, Math.Max(0, help.Count - height));
            var page = help.Skip(helpScroll).Take(height).ToList();
            if (help.Count > height && page.Count == height)
                page[^1] = new Line().Add(helpScroll + height < help.Count ? "  ↓ más abajo" : "", Theme.Muted);
            return page;
        }
        if (view == View.Registro) return LogBody(w, height);
        if (engine.Client is null)
        {
            var lines = Enumerable.Range(0, height / 2 - 1).Select(_ => new Line()).ToList();
            lines.Add(new Line().Add("   El motor no está conectado.", Theme.Warn));
            lines.Add(new Line().Add("   /motor iniciar lo arranca en segundo plano con tu perfil de aMule Modern.", Theme.Muted));
            return lines;
        }
        int detail = height >= 16 && Count(view) > 0 ? 5 : 0;
        var list = new List<Line>();
        switch (view)
        {
            case View.Descargas: DownloadTable(list, w, height - detail); break;
            case View.Buscar: SearchTable(list, w, height - detail); break;
            case View.Servidores: ServerTable(list, w, height - detail); break;
            case View.Compartidos: SharedTable(list, w, height - detail); break;
        }
        while (list.Count < height - detail) list.Add(new Line());
        if (detail > 0)
        {
            list.Add(new Line().Add(" " + new string('┄', Math.Max(0, w - 4 - position.Length)) + position + "┄┄", Theme.Muted));
            list.AddRange(Detail(w).Take(detail - 1));
        }
        return list;
    }

    private void Table<T>(List<Line> output, int w, int height, View v, IReadOnlyList<T> rows, Func<T, string> key, Col<T>[] columns, string empty)
    {
        var active = columns.ToList();
        int Fixed() => 3 + active.Where(c => c.Width > 0).Sum(c => c.Width + 1);
        while (w - Fixed() < 24 && active.Any(c => c.Drop > 0)) active.Remove(active.Where(c => c.Drop > 0).MinBy(c => c.Drop)!);
        int flex = Math.Max(8, w - Fixed() - 1);
        var header = new Line().Add("   ");
        foreach (var c in active) header.Cell(c.Title, c.Width == 0 ? flex : c.Width, Theme.Muted, c.Right).Add(" ");
        output.Add(header);
        listHeight = Math.Max(1, height - 1);
        position = "";
        if (rows.Count == 0)
        {
            for (int i = 0; i < listHeight / 2 - 1; i++) output.Add(new Line());
            output.Add(new Line().Add("   " + empty, Theme.Muted));
            return;
        }
        int selected = Selected(v);
        int top = scrollTop.GetValueOrDefault(v);
        if (selected < top) top = selected;
        if (selected >= top + listHeight) top = selected - listHeight + 1;
        top = Math.Clamp(top, 0, Math.Max(0, rows.Count - listHeight));
        scrollTop[v] = top;
        var set = marks.GetValueOrDefault(v);
        for (int r = top; r < Math.Min(rows.Count, top + listHeight); r++)
        {
            var row = rows[r];
            bool on = r == selected;
            var line = new Line().Add(on ? "❯" : " ", Theme.AccentBold).Add(set?.Contains(key(row)) == true ? "●" : " ", Theme.Accent).Add(" ");
            foreach (var c in active)
            {
                int cw = c.Width == 0 ? flex : c.Width;
                if (c.Custom is not null) c.Custom(line, row, cw);
                else line.Cell(c.Text(row), cw, (c.Width == 0 && on ? Ansi.Bold : "") + (c.Style?.Invoke(row) ?? ""), c.Right);
                line.Add(" ");
            }
            output.Add(line);
        }
        position = rows.Count > listHeight ? $" {selected + 1}/{rows.Count} " : "";
    }

    private static void Bar(Line line, double percent, int width, string color)
    {
        const string eighths = "▏▎▍▌▋▊▉";
        double filled = Math.Clamp(percent, 0, 100) / 100 * width;
        int full = (int)filled, part = (int)((filled - full) * 8);
        var sb = new StringBuilder(new string('█', full));
        if (full < width && part > 0) { sb.Append(eighths[part - 1]); full++; }
        line.Add(sb.ToString(), color).Add(new string('░', width - full), Theme.Muted);
    }

    private static string StateColor(DownloadItem d) => d.State switch
    {
        9 => Theme.Ok, 7 => Theme.Muted, 4 or 5 => Theme.Error, 0 when d.Speed > 0 => Theme.Info, _ => Theme.Plain
    };

    private void DownloadTable(List<Line> output, int w, int height) => Table(output, w, height, View.Descargas, downloads, d => d.Hash,
    [
        new("Nombre", 0, false, 0, d => d.Name),
        new("Tamaño", 9, true, 3, d => d.SizeText),
        new("Progreso", 17, false, 5, d => "", Custom: (line, d, cw) =>
        {
            Bar(line, d.Progress, cw - 7, d.IsComplete ? Theme.Ok : d.State == 7 ? Theme.Muted : Theme.Accent);
            line.Cell(d.Progress.ToString("0.0") + "%", 7, Theme.Plain, right: true);
        }),
        new("Velocidad", 11, true, 4, d => d.SpeedText, d => d.Speed > 0 ? Theme.Info : Theme.Muted),
        new("Fuentes", 7, true, 1, d => d.Sources.ToString()),
        new("Restante", 10, true, 2, d => d.EtaText, _ => Theme.Muted),
        new("Estado", 12, false, 6, d => d.StateText, StateColor),
    ], "Cola vacía. Pega un enlace ed2k:// o escribe algo para buscar.");

    private void SearchTable(List<Line> output, int w, int height)
    {
        var strip = new Line().Add(" ");
        if (searches.Tabs.Count == 0) strip.Add("Sin búsquedas todavía. Escribe abajo y pulsa Enter.", Theme.Muted);
        for (int i = 0; i < searches.Tabs.Count; i++)
        {
            var tab = searches.Tabs[i];
            bool on = i == searches.Active;
            string live = tab.Live && SearchActive ? $" {Spinner[ticks % Spinner.Length]}" : tab.Live ? " ●" : "";
            string label = $" {i + 1} {TextWidth.Fit(tab.Query, 18).TrimEnd()}{live} {tab.Results.Count} ";
            if (strip.Width + TextWidth.Of(label) + 3 > w) { strip.Add(" …", Theme.Muted); break; }
            strip.Add(on ? "▕" : " ", Theme.Muted).Add(label, on ? Theme.AccentBold + Ansi.Underline : tab.Live ? Theme.Plain : Theme.Muted).Add(on ? "▏" : " ", Theme.Muted);
        }
        output.Add(strip);
        var status = new Line().Add(" ");
        if (searches.Current is { } current)
        {
            status.Add("«" + current.Query + "»", Theme.Strong).Add($"  {current.Kind} · {current.Results.Count} resultados", Theme.Muted);
            if (current.Live && SearchActive) status.Add($"  {Spinner[ticks % Spinner.Length]} buscando…", Theme.Accent);
            else if (current.Live) status.Add("  · en vivo", Theme.Ok);
            else status.Add($"  · congelada {Ago(current.Started)} · /rebuscar la relanza", Theme.Muted);
            if (searches.Tabs.Count > 1) status.Right(new Line().Add("Ctrl+←→ o Alt+1…9 pestañas · Ctrl+W cierra ", Theme.Muted), w);
        }
        output.Add(status);
        var queued = downloads.Select(d => d.Hash).ToHashSet();
        Table(output, w, height - 2, View.Buscar, results, r => r.Hash,
        [
            new("Nombre", 0, false, 0, r => r.Name, r => queued.Contains(r.Hash) ? Theme.Ok : ""),
            new("Tamaño", 9, true, 2, r => r.SizeText),
            new("Fuentes", 8, true, 3, r => r.Sources.ToString(), r => r.Sources >= 10 ? Theme.Ok : r.Sources >= 3 ? Theme.Plain : Theme.Muted),
            new("Completas", 9, true, 1, r => r.CompleteSources.ToString(), _ => Theme.Muted),
            new("", 2, false, 4, r => queued.Contains(r.Hash) ? "✓" : "", _ => Theme.Ok),
        ], searches.Current is null ? "Escribe abajo lo que buscas y pulsa Enter." : searches.Current.Live && SearchActive ? "Esperando resultados…" : "Sin resultados.");
    }

    private static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        return span.TotalMinutes < 1 ? "hace un momento" : span.TotalHours < 1 ? $"hace {(int)span.TotalMinutes} min"
            : span.TotalDays < 1 ? $"hace {(int)span.TotalHours} h" : $"hace {(int)span.TotalDays} d";
    }

    private void ServerTable(List<Line> output, int w, int height)
    {
        string? current = network?.Server?.Endpoint;
        bool connected = network?.Connected == true;
        Table(output, w, height, View.Servidores, servers, s => s.Endpoint,
        [
            new("", 1, false, 0, s => s.Endpoint == current ? "●" : "", _ => connected ? Theme.Ok : Theme.Warn),
            new("Nombre", 0, false, 0, s => s.Name),
            new("Dirección", 21, false, 3, s => s.Endpoint, _ => Theme.Muted),
            new("Usuarios", 9, true, 2, s => s.UsersText),
            new("Archivos", 12, true, 1, s => s.FilesText, _ => Theme.Muted),
            new("Ping", 7, true, 4, s => s.PingText, _ => Theme.Muted),
        ], "Sin servidores. /restaurar recupera los de aMule Modern; /importar trae una lista.");
    }

    private void SharedTable(List<Line> output, int w, int height) => Table(output, w, height, View.Compartidos, shared, s => s.Hash,
    [
        new("Nombre", 0, false, 0, s => s.Name),
        new("Tamaño", 9, true, 3, s => s.SizeText),
        new("Peticiones", 10, true, 1, s => s.Requests.ToString(), _ => Theme.Muted),
        new("Subido", 10, true, 2, s => DownloadItem.FormatBytes(s.Uploaded)),
    ], "No hay archivos compartidos.");

    private List<Line> Detail(int w)
    {
        Line Field(string label, string value, string style = "") => new Line().Add("   " + label.PadRight(11), Theme.Muted).Add(TextWidth.Fit(value, Math.Max(4, w - 15)).TrimEnd(), style);
        int index = Selected(view);
        if (index < 0) return [];
        switch (view)
        {
            case View.Descargas:
            {
                var d = downloads[index];
                string link = d.Detail.Link is { Length: > 0 } l ? l : $"ed2k://|file|{d.Name}|{d.Size}|{d.Hash}|/";
                return
                [
                    Field("Archivo", d.Name, Theme.Strong),
                    Field("Estado", $"{d.StateText} · {d.ProgressText} de {d.SizeText} · prioridad {d.Detail.PriorityText} · restante {d.EtaText}"),
                    Field("Fuentes", d.Detail.SourcesText),
                    Field("Enlace", link, Theme.Muted),
                ];
            }
            case View.Buscar:
            {
                var r = results[index];
                bool queued = downloads.Any(d => d.Hash == r.Hash);
                return
                [
                    Field("Archivo", r.Name, Theme.Strong),
                    Field("Datos", $"{r.SizeText} · {r.Sources} fuentes · {r.CompleteSources} completas"),
                    Field("Hash", r.Hash, Theme.Muted),
                    queued ? Field("Cola", "✓ Ya está en Descargas", Theme.Ok) : Field("", "Enter la añade a la cola · Espacio marca varias", Theme.Muted),
                ];
            }
            case View.Servidores:
            {
                var s = servers[index];
                bool current = network?.Server?.Endpoint == s.Endpoint;
                string state = current ? network!.Ed2kText : "—";
                return
                [
                    Field("Servidor", s.Name, Theme.Strong),
                    Field("Dirección", s.Endpoint),
                    Field("Datos", $"{s.UsersText} usuarios · {s.FilesText} archivos · ping {s.PingText}"),
                    Field("Sesión", state, current && network!.Connected ? Theme.Ok : Theme.Muted),
                ];
            }
            case View.Compartidos:
            {
                var s = shared[index];
                return
                [
                    Field("Archivo", s.Name, Theme.Strong),
                    Field("Carpeta", s.FolderText),
                    Field("Datos", $"{s.SizeText} · {s.Requests} peticiones · {DownloadItem.FormatBytes(s.Uploaded)} subidos · {s.Queue} en cola"),
                    Field("Enlace", s.Ed2kLink, Theme.Muted),
                ];
            }
        }
        return [];
    }

    private List<Line> LogBody(int w, int height)
    {
        var lines = new List<Line>();
        if (log.Length == 0) { lines.Add(new Line().Add("   Registro vacío.", Theme.Muted)); return lines; }
        logScroll = Math.Clamp(logScroll, 0, Math.Max(0, log.Length - height));
        int end = log.Length - logScroll, start = Math.Max(0, end - height);
        for (int i = start; i < end; i++)
        {
            string text = log[i];
            string style = text.Contains("error", StringComparison.OrdinalIgnoreCase) || text.Contains("fail", StringComparison.OrdinalIgnoreCase) ? Theme.Error
                : text.Contains("warn", StringComparison.OrdinalIgnoreCase) || text.Contains("aviso", StringComparison.OrdinalIgnoreCase) ? Theme.Warn : Theme.Plain;
            lines.Add(new Line().Add(" ").Cell(text, w - 2, style));
        }
        return lines;
    }

    private List<Line> HelpBody(int w)
    {
        var lines = new List<Line>
        {
            new Line().Add("  Teclas", Theme.AccentBold),
            new Line().Add("  Tab / ← →        ", Theme.Accent).Add("cambiar de vista (← → con la caja vacía)"),
            new Line().Add("  ↑ ↓ RePág AvPág  ", Theme.Accent).Add("mover la selección · Alt+↑↓ historial de órdenes"),
            new Line().Add("  Espacio          ", Theme.Accent).Add("marcar varias filas · Esc desmarca"),
            new Line().Add("  Enter            ", Theme.Accent).Add("con texto: ejecuta; vacío: pausa/reanuda, descarga o conecta según la vista"),
            new Line().Add("  Supr             ", Theme.Accent).Add("cancelar descarga o quitar servidor (pide confirmación)"),
            new Line().Add("  Ctrl+P           ", Theme.Accent).Add("pausar / reanudar   ·   Ctrl+L redibujar   ·   Ctrl+C salir"),
            new Line().Add("  Ctrl+← →, Alt+1…9", Theme.Accent).Add(" pestañas de búsqueda · Ctrl+W cierra la pestaña"),
            new Line().Add("                   ", Theme.Accent).Add("Solo la última búsqueda está en vivo; las demás quedan congeladas y se pueden descargar igual.", Theme.Muted),
            new Line(),
            new Line().Add("  Órdenes", Theme.AccentBold).Add("   (escribe / y Tab completa)", Theme.Muted),
        };
        foreach (var c in Commands)
            lines.Add(new Line().Add("  ").Cell("/" + c.Name + (c.Args.Length > 0 ? " " + c.Args : ""), 34, Theme.Accent).Add(c.Help));
        lines.Add(new Line());
        lines.Add(new Line().Add("  Sin barra: un ed2k:// se añade a la cola; cualquier otro texto busca en el servidor conectado.", Theme.Muted));
        lines.Add(new Line().Add($"  Perfil: {engine.ProfilePath}", Theme.Muted));
        lines.Add(new Line().Add($"  Descargas: {engine.IncomingPath}   ·   Temporales: {engine.TempPath}", Theme.Muted));
        return lines;
    }
}
