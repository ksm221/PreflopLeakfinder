using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using PokerStudy.Core;
using PokerStudy.Core.Gto;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
namespace PokerStudy.Wpf;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    readonly ObservableCollection<HandEntity> _hands = new(); CancellationTokenSource? _cts; readonly string _db;
    readonly GtoStore? _gto;
    string _status = "Ready"; public string StatusText { get => _status; private set { _status = value; OnPropertyChanged(); } }
    public MainWindow()
    {
        InitializeComponent(); DataContext = this; _db = Path.Combine(AppContext.BaseDirectory, "PokerStudy.db"); HandsGrid.ItemsSource = _hands;
        var gtoPath = Path.Combine(AppContext.BaseDirectory, "Data", "gto_ranges.json");
        if (File.Exists(gtoPath)) { try { _gto = new GtoStore(gtoPath); } catch (Exception ex) { StatusText = $"GTO data failed to load: {ex.Message}"; } }
        Loaded += async (_, _) => await LoadPageAsync();
    }
    async void Import_Click(object s, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Select Winamax hand-history folder" }; if (d.ShowDialog() != true) return; _cts?.Cancel(); _cts = new(); StatusText = "Importing...";
        try { var p = new Progress<(long files, long hands, long errors)>(x => StatusText = $"Files: {x.files:n0} | Hands: {x.hands:n0} | Errors: {x.errors:n0}"); var r = await new FolderImporter(_db).ImportAsync(d.FolderName, p, _cts.Token); StatusText = $"Done: {r.Files:n0} files | {r.Hands:n0} new hands | {r.Errors:n0} errors | {r.Duration.TotalSeconds:0.0}s"; await LoadPageAsync(); }
        catch (OperationCanceledException) { StatusText = "Import cancelled"; }
        catch (Exception ex) { StatusText = $"Import failed: {ex.Message}"; MessageBox.Show(ex.ToString(), "Import error"); }
    }
    async void Refresh_Click(object s, RoutedEventArgs e) => await LoadPageAsync(); void Cancel_Click(object s, RoutedEventArgs e) => _cts?.Cancel();
    async void ClearData_Click(object s, RoutedEventArgs e)
    {
        if (MessageBox.Show("This permanently deletes all imported hands and history. Continue?", "Clear data", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _cts?.Cancel();
        try
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { _db, _db + "-wal", _db + "-shm", _db + "-journal" }) if (File.Exists(f)) File.Delete(f);
            _hands.Clear();
            StatusText = "Data cleared";
            await LoadPageAsync();
        }
        catch (Exception ex) { StatusText = $"Clear failed: {ex.Message}"; MessageBox.Show(ex.ToString(), "Clear error"); }
    }
    async Task LoadPageAsync()
    {
        if (!File.Exists(_db)) { CountText.Text = "No database yet"; return; }

        var f = Selected(FormatFilter);
        var p = Selected(PositionFilter);
        var q = SearchBox.Text.Trim();
        var gtoFilter = Selected(GtoFilter);

        await using var d = new PokerStudyDbContext(_db);
        var query = d.Hands.AsNoTracking().AsQueryable();

        if (f == "HU") query = query.Where(x => x.Format == GameFormat.HU);
        if (f == "3W") query = query.Where(x => x.Format == GameFormat.ThreeW);
        if (p == "BTN") query = query.Where(x => x.HeroPosition == Position.BTN);
        if (p == "SB") query = query.Where(x => x.HeroPosition == Position.SB);
        if (p == "BB") query = query.Where(x => x.HeroPosition == Position.BB);
        if (q != "") query = query.Where(x => x.HandId.Contains(q) || x.StartingHand.Contains(q) || x.Spot.Contains(q));

        var count = await query.CountAsync();

        // GTO verdict is calculated in memory, so when "Deviation Only" is selected
        // we inspect a larger batch than the normal 500-row display page. This prevents
        // the filter from only finding deviations among the newest 500 hands.
        var fetchLimit = gtoFilter == "Deviation Only" ? 5000 : 500;
        var page = await query.OrderByDescending(x => x.PlayedAtUtc).Take(fetchLimit).ToListAsync();

        var handIds = page.Select(x => x.HandId).ToList();
        var actionsByHand = (await d.Actions.AsNoTracking()
            .Where(x => handIds.Contains(x.HandId))
            .ToListAsync())
            .GroupBy(x => x.HandId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ActionEntity>)g.ToList());

        foreach (var h in page)
        {
            if (_gto == null)
            {
                h.GtoVerdict = "";
                h.GtoDetail = "No GTO data loaded";
                continue;
            }

            var acts = actionsByHand.TryGetValue(h.HandId, out var a)
                ? a
                : Array.Empty<ActionEntity>();

            var r = GtoMatcher.Evaluate(_gto, h, acts);

            if (r == null)
            {
                h.GtoVerdict = "";
                h.GtoDetail = "Not covered yet (deeper line)";
            }
            else
            {
                h.GtoVerdict = r.Verdict;
                h.GtoDetail = string.IsNullOrEmpty(r.ChartLabel)
                    ? r.Detail
                    : $"{r.Detail} [{r.ChartLabel}]";
            }
        }

        if (gtoFilter == "Deviation Only")
            page = page.Where(x => x.GtoVerdict == "Deviation").ToList();

        // Keep the normal display limit after applying the filter.
        page = page.Take(500).ToList();

        _hands.Clear();
        foreach (var h in page) _hands.Add(h);

        if (gtoFilter == "Deviation Only")
            CountText.Text = $"{page.Count:n0} deviations loaded / {count:n0} matching base filters";
        else
            CountText.Text = $"{page.Count:n0} loaded / {count:n0} matching";
    }

    void FilterChanged(object s, RoutedEventArgs e) { if (IsLoaded) _ = LoadPageAsync(); }
    static string Selected(System.Windows.Controls.ComboBox b) => (b.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "All";
    void HandsGrid_MouseDoubleClick(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        var dep = (DependencyObject)e.OriginalSource;
        while (dep != null && dep is not System.Windows.Controls.DataGridRow) dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
        if (dep is System.Windows.Controls.DataGridRow row) row.DetailsVisibility = row.DetailsVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }
    public event PropertyChangedEventHandler? PropertyChanged; void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}