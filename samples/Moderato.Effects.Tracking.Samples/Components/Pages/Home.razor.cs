using Microsoft.AspNetCore.Components;

namespace Moderato.Effects.Tracking.Samples.Components.Pages;

public partial class Home
{
    private static readonly TimeSpan refreshInterval = TimeSpan.FromMilliseconds(500);
    private EffectRecordingSnapshot? snapshot;
    private long? selectedId;
    private long? rootId;
    private string query = "";
    private string outcome = "";
    private string? notice;
    private bool paused;
    private bool interactive;
    private readonly CancellationTokenSource stopping = new();
    private Task? refresh;
    private Dictionary<long, long> roots = [];

    protected override void OnInitialized() => Refresh();

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            interactive = true;
            refresh = RefreshAsync();
            StateHasChanged();
        }
        return Task.CompletedTask;
    }

    private void Refresh()
    {
        snapshot = Session.GetSnapshot();
        roots = [];
        foreach (var entry in snapshot.Entries)
        {
            roots[entry.Id] = entry.ParentId is { } parent && roots.TryGetValue(parent, out var root)
                ? root : entry.Id;
        }
        notice = null;
        if (rootId is { } selected && !roots.ContainsKey(selected))
        {
            notice = $"Execution #{selected} is no longer retained. Showing matching executions.";
            rootId = null;
        }
    }

    private IEnumerable<EffectRecordingEntry> MatchingRoots()
    {
        if (snapshot is null) return [];
        var matches = snapshot.Entries.Where(entry => string.IsNullOrWhiteSpace(query)
            || entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.TraceId?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
            || entry.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == query)
            .Select(entry => roots[entry.Id]).ToHashSet();
        return snapshot.Entries.Where(entry => roots[entry.Id] == entry.Id && matches.Contains(entry.Id)
            && (outcome.Length == 0 || entry.State == outcome)).Reverse();
    }

    private IReadOnlyList<EffectRecordingEntry> VisibleEntries()
    {
        if (snapshot is null) return [];
        var matching = MatchingRoots().Select(entry => entry.Id).ToHashSet();
        return snapshot.Entries.Where(entry => matching.Contains(roots[entry.Id]) && (rootId is null || roots[entry.Id] == rootId)).ToArray();
    }

    private string ExecutionName(EffectRecordingEntry root)
        => snapshot!.Entries.FirstOrDefault(entry => entry.ParentId == root.Id && entry.Kind == "op")?.Name ?? root.Name;

    private void Select(long id) => selectedId = id;
    private void ResetRoot() { rootId = null; selectedId = null; }
    private void SelectRoot() => selectedId = rootId;
    private void ToggleUpdates() { paused = !paused; if (!paused) Refresh(); }

    private async Task RefreshAsync()
    {
        using var timer = new PeriodicTimer(refreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stopping.Token))
            {
                await InvokeAsync(() => { if (paused) return; Refresh(); StateHasChanged(); });
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        if (refresh is not null) await refresh;
        stopping.Dispose();
    }
}
