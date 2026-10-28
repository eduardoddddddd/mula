using System.Text.Json;
using AmuleModern.Amule;

namespace Mula;

// amuled keeps one live search; older tabs are snapshots that can still be queued by ed2k link.
sealed class SearchTab
{
    public string Query { get; set; } = "";
    public string Kind { get; set; } = "servidor";
    public DateTime Started { get; set; }
    public List<SearchResult> Results { get; set; } = [];
    public bool Live { get; set; }
    public string? SelectedKey { get; set; }

    public static string Link(SearchResult r) => $"ed2k://|file|{r.Name}|{r.Size}|{r.Hash}|/";
}

sealed class SearchStore
{
    public const int MaxTabs = 9;
    private readonly string path;
    public List<SearchTab> Tabs { get; } = [];
    public int Active { get; set; } = -1;
    public SearchTab? Current => Active >= 0 && Active < Tabs.Count ? Tabs[Active] : null;
    public SearchTab? LiveTab => Tabs.FirstOrDefault(t => t.Live);

    public SearchStore(string path)
    {
        this.path = path;
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<List<SearchTab>>(File.ReadAllText(path)) is { } saved)
            {
                foreach (var tab in saved.Take(MaxTabs)) { tab.Live = false; Tabs.Add(tab); }
                Active = Tabs.Count - 1;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { /* empezar sin pestañas */ }
    }

    // Under the home folder, not AppData: AppData can be virtualized per MSIX app, and the
    // tabs must be the same from SSH, RDP or any terminal.
    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mula", "searches.json");

    // A repeated query reuses its tab instead of piling up duplicates.
    public SearchTab Start(string query, string kind)
    {
        foreach (var t in Tabs) t.Live = false;
        var tab = Tabs.FirstOrDefault(t => t.Kind == kind && string.Equals(t.Query, query, StringComparison.CurrentCultureIgnoreCase));
        if (tab is null)
        {
            if (Tabs.Count >= MaxTabs) Tabs.RemoveAt(0);
            tab = new SearchTab { Query = query, Kind = kind };
            Tabs.Add(tab);
        }
        tab.Started = DateTime.UtcNow;
        tab.Results = [];
        tab.SelectedKey = null;
        tab.Live = true;
        Active = Tabs.IndexOf(tab);
        Save();
        return tab;
    }

    public void Close(int index)
    {
        if (index < 0 || index >= Tabs.Count) return;
        Tabs.RemoveAt(index);
        Active = Math.Min(Active, Tabs.Count - 1);
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Tabs));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* las pestañas viven en memoria */ }
    }
}
