using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Mula;

static class Ansi
{
    public const string Reset = "\x1b[0m";
    public const string Bold = "\x1b[1m";
    public const string Underline = "\x1b[4m";
    public static string Fg(int r, int g, int b) => $"\x1b[38;2;{r};{g};{b}m";
}

static class Theme
{
    public static readonly string Accent = Ansi.Fg(217, 119, 87);
    public static readonly string AccentBold = Ansi.Bold + Accent;
    public static readonly string Muted = Ansi.Fg(128, 128, 128);
    public static readonly string Ok = Ansi.Fg(110, 190, 120);
    public static readonly string Warn = Ansi.Fg(230, 180, 80);
    public static readonly string Error = Ansi.Fg(235, 100, 100);
    public static readonly string Info = Ansi.Fg(110, 160, 230);
    public const string Strong = Ansi.Bold;
    public const string Plain = "";
}

static class TextWidth
{
    // Network data (file names, logs) must never reach the terminal as control sequences.
    public static string Clean(string text)
    {
        if (!text.Any(c => char.IsControl(c))) return text;
        var sb = new StringBuilder(text.Length);
        foreach (char c in text) sb.Append(char.IsControl(c) ? ' ' : c);
        return sb.ToString();
    }
    public static int Of(string text)
    {
        int width = 0;
        foreach (var rune in text.EnumerateRunes()) width += Of(rune);
        return width;
    }
    public static int Of(Rune rune)
    {
        int c = rune.Value;
        if (c < 32 || c == 0x7f) return 0;
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        bool wide = c >= 0x1100 && (c <= 0x115f || c == 0x2329 || c == 0x232a
            || (c >= 0x2e80 && c <= 0xa4cf && c != 0x303f) || (c >= 0xac00 && c <= 0xd7a3)
            || (c >= 0xf900 && c <= 0xfaff) || (c >= 0xfe30 && c <= 0xfe6f)
            || (c >= 0xff00 && c <= 0xff60) || (c >= 0xffe0 && c <= 0xffe6)
            || (c >= 0x1f300 && c <= 0x1f64f) || (c >= 0x1f900 && c <= 0x1f9ff)
            || (c >= 0x20000 && c <= 0x3fffd));
        return wide ? 2 : 1;
    }
    public static string Fit(string text, int width, bool right = false)
    {
        if (width <= 0) return "";
        text = Clean(text);
        int w = Of(text);
        if (w <= width) return right ? new string(' ', width - w) + text : text + new string(' ', width - w);
        var sb = new StringBuilder();
        int used = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            int rw = Of(rune);
            if (used + rw > width - 1) break;
            sb.Append(rune.ToString());
            used += rw;
        }
        sb.Append('…');
        used++;
        if (used < width) sb.Append(' ', width - used);
        return sb.ToString();
    }
    // Cuts from the left so the end of the text stays visible.
    public static string Tail(string text, int width)
    {
        text = Clean(text);
        if (Of(text) <= width) return text;
        var runes = text.EnumerateRunes().ToArray();
        int used = 1, start = runes.Length;
        while (start > 0 && used + Of(runes[start - 1]) <= width) used += Of(runes[--start]);
        return "…" + string.Concat(runes[start..].Select(r => r.ToString()));
    }
}

sealed class Line
{
    private readonly List<(string Text, string Style)> parts = [];
    public int Width { get; private set; }
    public Line Add(string text, string style = "")
    {
        text = TextWidth.Clean(text);
        if (text.Length == 0) return this;
        parts.Add((text, style));
        Width += TextWidth.Of(text);
        return this;
    }
    public Line Cell(string text, int width, string style = "", bool right = false) => Add(TextWidth.Fit(text, width, right), style);
    public Line Pad(int width) => width > Width ? Add(new string(' ', width - Width)) : this;
    public Line Right(Line other, int total)
    {
        int gap = total - Width - other.Width;
        if (gap < 1) return this;
        Add(new string(' ', gap));
        foreach (var (text, style) in other.parts) Add(text, style);
        return this;
    }
    public string Render(int width)
    {
        var sb = new StringBuilder();
        int used = 0;
        foreach (var (text, style) in parts)
        {
            if (used >= width) break;
            string piece = text;
            int w = TextWidth.Of(piece);
            if (used + w > width) { piece = TextWidth.Fit(piece, width - used); w = width - used; }
            if (style.Length > 0) sb.Append(style).Append(piece).Append(Ansi.Reset);
            else sb.Append(piece);
            used += w;
        }
        if (used < width) sb.Append(' ', width - used);
        return sb.ToString();
    }
}

abstract record TermEvent;
sealed record KeyEvent(ConsoleKeyInfo Info) : TermEvent;
sealed record TickEvent : TermEvent;

sealed class Terminal : IDisposable
{
    private string[] previous = [];
    private int lastWidth, lastHeight;
    private readonly Stream output = Console.OpenStandardOutput();
    private bool entered;

    public static (int Width, int Height) Size
    {
        get
        {
            try { return (Math.Max(Console.WindowWidth, 40), Math.Max(Console.WindowHeight, 12)); }
            catch (IOException) { return (100, 30); }
        }
    }

    public void Enter()
    {
        if (OperatingSystem.IsWindows()) EnableVirtualTerminal();
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false);
        Console.TreatControlCAsInput = true;
        Write("\x1b[?1049h\x1b[?25l\x1b[2J\x1b[H");
        entered = true;
    }

    public void Invalidate() => lastWidth = -1;

    public void Draw(IReadOnlyList<Line> lines, int width, int height, (int Row, int Col)? cursor)
    {
        var sb = new StringBuilder("\x1b[?25l");
        bool full = width != lastWidth || height != lastHeight || previous.Length != height;
        if (full)
        {
            sb.Append("\x1b[2J");
            previous = new string[height];
            lastWidth = width; lastHeight = height;
        }
        // One column short of the edge: writing the last cell can scroll some terminals.
        int usable = width - 1;
        for (int row = 0; row < height; row++)
        {
            string text = row < lines.Count ? lines[row].Render(usable) : new string(' ', usable);
            if (!full && previous[row] == text) continue;
            sb.Append("\x1b[").Append(row + 1).Append(";1H").Append(text);
            previous[row] = text;
        }
        if (cursor is { } at) sb.Append("\x1b[").Append(at.Row + 1).Append(';').Append(at.Col + 1).Append("H\x1b[?25h");
        Write(sb.ToString());
    }

    private void Write(string text)
    {
        output.Write(Encoding.UTF8.GetBytes(text));
        output.Flush();
    }

    public void Dispose()
    {
        if (!entered) return;
        entered = false;
        Write(Ansi.Reset + "\x1b[?25h\x1b[?1049l");
        Console.TreatControlCAsInput = false;
    }

    private static void EnableVirtualTerminal()
    {
        const int StdOutput = -11;
        const uint VirtualTerminalProcessing = 0x0004, DisableNewlineAutoReturn = 0x0008;
        var handle = GetStdHandle(StdOutput);
        if (GetConsoleMode(handle, out uint mode))
            SetConsoleMode(handle, mode | VirtualTerminalProcessing | DisableNewlineAutoReturn);
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr handle, uint mode);
}
