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
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
namespace PokerStudy.Wpf;

public sealed record PositionStat(string Position, int Count, double Pct);
public sealed record StackBucketStat(string Bucket, int Count, double Pct);

public partial class MainWindow : Window, INotifyPropertyChanged
{
    readonly ObservableCollection<HandEntity> _hands = new(); CancellationTokenSource? _cts; readonly string _db;
    readonly GtoStore? _gto;
    readonly ObservableCollection<PositionStat> _posStats = new();
    readonly ObservableCollection<StackBucketStat> _stackStats = new();
    readonly ObservableCollection<HandFreqStat> _handStats = new();
    string _status = "Ready"; public string StatusText { get => _status; private set { _status = value; OnPropertyChanged(); } }
    public MainWindow()
    {
        InitializeComponent(); DataContext = this; _db = Path.Combine(AppContext.BaseDirectory, "PokerStudy.db"); HandsGrid.ItemsSource = _hands;
        PositionStatsGrid.ItemsSource = _posStats; StackStatsGrid.ItemsSource = _stackStats; HandStatsGrid.ItemsSource = _handStats;
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
        var query = d.Hands.AsNoTracking().AsQueryable().Where(x => x.StackBb >= 5);

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

    void DateRangeToggle_Changed(object s, RoutedEventArgs e)
    {
        if (FromDate == null || ToDate == null) return; // fires during InitializeComponent before these are wired up yet
        var enabled = AllTimeCheck.IsChecked != true;
        FromDate.IsEnabled = enabled; ToDate.IsEnabled = enabled;
        if (IsLoaded) _ = ComputeSummaryAsync();
    }

    void SummaryFilterChanged(object s, RoutedEventArgs e)
    {
        if (IsLoaded) _ = ComputeSummaryAsync();
    }

    void HandStatsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var dep = (DependencyObject)e.OriginalSource;
        while (dep != null && dep is not DataGridRow)
            dep = VisualTreeHelper.GetParent(dep);

        if (dep is DataGridRow row)
            row.DetailsVisibility = row.DetailsVisibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
    }

    void DeviationHandListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox lb && lb.SelectedItem is HandEntity h)
            SelectedDeviationText.Text = h.RawText;
    }

    async void RefreshSummary_Click(object s, RoutedEventArgs e) => await ComputeSummaryAsync();

    async Task ComputeSummaryAsync()
    {
        if (!File.Exists(_db)) { SummaryHeadlineText.Text = "No database yet"; return; }
        if (_gto == null) { SummaryHeadlineText.Text = "GTO data not loaded"; return; }

        SummaryStatusText.Text = "Calculating...";
        _posStats.Clear(); _stackStats.Clear(); _handStats.Clear(); SummaryHeadlineText.Text = "";

        DateTime? from = null, to = null;
        if (AllTimeCheck.IsChecked != true)
        {
            from = FromDate.SelectedDate;
            to = ToDate.SelectedDate?.AddDays(1); // inclusive of the whole "to" day
        }

        await using var d = new PokerStudyDbContext(_db);
        // Ignore hands below 5bb - too shallow to be a meaningful preflop decision for this analysis.
        var query = d.Hands.AsNoTracking().Where(x => x.StackBb >= 5);
        var fmt = Selected(SummaryFormatFilter);
        if (fmt == "HU") query = query.Where(x => x.Format == GameFormat.HU);
        if (fmt == "3W") query = query.Where(x => x.Format == GameFormat.ThreeW);
        if (from.HasValue) query = query.Where(x => x.PlayedAtUtc >= from.Value);
        if (to.HasValue) query = query.Where(x => x.PlayedAtUtc < to.Value);
        var hands = await query.ToListAsync();

        if (hands.Count == 0)
        {
            SummaryHeadlineText.Text = "No hands (5bb+) in this date range";
            SummaryStatusText.Text = "";
            return;
        }

        // Fetch actions in chunks to stay well under SQLite's parameter-count limit for large datasets.
        var handIds = hands.Select(x => x.HandId).ToList();
        var actionsByHand = new Dictionary<string, List<ActionEntity>>();
        const int chunkSize = 500;
        for (int i = 0; i < handIds.Count; i += chunkSize)
        {
            var chunk = handIds.Skip(i).Take(chunkSize).ToList();
            var acts = await d.Actions.AsNoTracking().Where(x => chunk.Contains(x.HandId)).ToListAsync();
            foreach (var g in acts.GroupBy(x => x.HandId)) actionsByHand[g.Key] = g.ToList();
        }

        int totalEvaluated = 0, totalDeviations = 0;
        // Keep format and position together. This is important when "All Formats"
        // is selected because HU and 3W both have an SB position.
        var byPos = new Dictionary<(GameFormat Format, Position Position), int>();
        var byBucket = new Dictionary<int, int>();
        var byHand = new Dictionary<string, int>();

        foreach (var h in hands)
        {
            var acts = actionsByHand.TryGetValue(h.HandId, out var a) ? (IReadOnlyList<ActionEntity>)a : Array.Empty<ActionEntity>();
            var r = GtoMatcher.Evaluate(_gto, h, acts);
            if (r == null || r.Verdict == "N/A" || r.Verdict == "No data") continue; // not covered / not a real decision - excluded from the mistake pool entirely

            totalEvaluated++;
            if (r.Verdict != "Deviation") continue;

            totalDeviations++;
            var posKey = (h.Format, h.HeroPosition);
            byPos[posKey] = byPos.GetValueOrDefault(posKey) + 1;
            var bucketLow = (int)Math.Floor(h.StackBb / 5) * 5;
            byBucket[bucketLow] = byBucket.GetValueOrDefault(bucketLow) + 1;
            byHand[h.StartingHand] = byHand.GetValueOrDefault(h.StartingHand) + 1;
        }

        SummaryHeadlineText.Text = totalEvaluated > 0
            ? $"{totalDeviations:n0} deviations out of {totalEvaluated:n0} evaluated hands ({(100.0 * totalDeviations / totalEvaluated):0.#}% mistake rate)"
            : "No evaluated hands in this range (GTO data doesn't cover these spots yet)";

        foreach (var kv in byPos
            .OrderBy(x => x.Key.Format == GameFormat.HU ? 0 : 1)
            .ThenBy(x => x.Key.Position))
        {
            var label = kv.Key.Format == GameFormat.HU
                ? $"HU {kv.Key.Position}"
                : $"3W {kv.Key.Position}";
            _posStats.Add(new PositionStat(label, kv.Value, totalDeviations > 0 ? 100.0 * kv.Value / totalDeviations : 0));
        }

        foreach (var kv in byBucket.OrderBy(x => x.Key))
            _stackStats.Add(new StackBucketStat($"{kv.Key}-{kv.Key + 4}bb", kv.Value, totalDeviations > 0 ? 100.0 * kv.Value / totalDeviations : 0));

        foreach (var kv in byHand.OrderByDescending(x => x.Value).Take(15))
        {
            var stat = new HandFreqStat(kv.Key, kv.Value);

            stat.DeviationHands = hands
                .Where(h => h.StartingHand == kv.Key)
                .Where(h =>
                {
                    var acts = actionsByHand.TryGetValue(h.HandId, out var a)
                        ? (IReadOnlyList<ActionEntity>)a
                        : Array.Empty<ActionEntity>();

                    var r = GtoMatcher.Evaluate(_gto, h, acts);
                    return r != null && r.Verdict == "Deviation";
                })
                .OrderByDescending(h => h.PlayedAtUtc)
                .ToList();

            _handStats.Add(stat);
        }


        var formatLabel = fmt == "HU" ? "HU" : fmt == "3W" ? "3W" : "HU + 3W";
        SummaryStatusText.Text = $"Based on {hands.Count:n0} hands (5bb+) | {formatLabel}";
    }

    public event PropertyChangedEventHandler? PropertyChanged; void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}