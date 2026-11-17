using Bevdox.Auth;
using Bevdox.Models;
using Bevdox.Services;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var auth = new AuthService();
var configStore = new ConfigStore();
var discovery = new DevBoxDiscovery(auth);
var launcher = new DevBoxLauncher(auth);

// Load cached config
var config = await configStore.LoadAsync(cts.Token);

// Background refresh with progress reporting
string refreshStatus = "Starting...";
// When the current refresh step started, to show how long it has been waiting
DateTime refreshStepStarted = DateTime.UtcNow;
// Outcome of the last completed refresh when it needs the user's attention
string? refreshError = null;
// Signal when refresh status changes so the UI can update immediately
var statusChanged = new SemaphoreSlim(0);
var progress = new InlineProgress<string>(s =>
{
    refreshStatus = s;
    refreshStepStarted = DateTime.UtcNow;
    statusChanged.Release();
});

Task<DiscoveryResult>? refreshTask = StartRefresh();

if (config.Instances.Count == 0)
{
    // No cache — must wait for discovery
    Console.WriteLine("Bevdox - DevBox RDP Launcher");
    Console.Write($"  {RefreshText()}");
    while (!refreshTask.IsCompleted)
    {
        await Task.Delay(200, cts.Token);
        Console.SetCursorPosition(0, Console.CursorTop);
        Console.Write($"  {RefreshText(),-60}");
    }
    Console.WriteLine();

    try
    {
        var result = await refreshTask;
        config = ConfigStore.Merge(config, result.Instances, result.Failures);
        await configStore.SaveAsync(config, cts.Token);
        refreshTask = null;
        refreshError = FailureSummary(result);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Failed: {ex.Message}");
        return 1;
    }
}

if (config.Instances.Count == 0)
{
    Console.WriteLine("  No Dev Box instances found.");
    if (refreshError is not null) Console.WriteLine($"  {refreshError}");
    return 0;
}

// Main loop
int selectedIndex = 0;
bool redraw = true;
string lastRenderedStatus = "";
Task<ConsoleKeyInfo>? keyTask = null;
Task? statusWait = null;
// Ticks once a second while work is pending so the elapsed time stays current
Task? tickTask = null;

// Launch runs in the background so the menu stays responsive while authenticating
Task? launchTask = null;
CancellationTokenSource? launchCts = null;
DevBoxInstance? launchTarget = null;
string launchStatus = "";
DateTime launchStepStarted = DateTime.UtcNow;
string? launchError = null;
var launchProgress = new InlineProgress<string>(s =>
{
    launchStatus = s;
    launchStepStarted = DateTime.UtcNow;
    statusChanged.Release();
});

while (!cts.Token.IsCancellationRequested)
{
    // Integrate a finished background refresh, including failures, so it never lingers as "refreshing"
    if (refreshTask is { IsCompleted: true })
    {
        try
        {
            var result = await refreshTask;
            config = ConfigStore.Merge(config, result.Instances, result.Failures);
            await configStore.SaveAsync(config, cts.Token);
            refreshError = FailureSummary(result);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            refreshError = $"✗ Refresh failed: {FirstLine(ex.Message)}";
        }
        refreshTask = null;
        redraw = true;
    }

    // Integrate a finished launch: exit on success, otherwise report it and stay in the menu
    if (launchTask is { IsCompleted: true })
    {
        var target = launchTarget!;
        try
        {
            await launchTask;
            Console.WriteLine();
            Console.WriteLine($"  Launched {target.EffectiveName}.");
            goto exit;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (OperationCanceledException) when (launchCts!.IsCancellationRequested)
        {
            launchError = $"✗ Launch of {target.EffectiveName} cancelled";
        }
        catch (Exception ex)
        {
            launchError = $"✗ Launch of {target.EffectiveName} failed: {FirstLine(ex.Message)}";
        }
        finally
        {
            launchCts!.Dispose();
            launchCts = null;
            launchTask = null;
            launchTarget = null;
        }
        redraw = true;
    }

    var sorted = config.Instances.OrderBy(i => i.EffectiveName, StringComparer.OrdinalIgnoreCase).ToList();
    if (selectedIndex >= sorted.Count) selectedIndex = sorted.Count - 1;

    // Check if status text changed — update just the status line
    var activity = ActivityText();
    if (!redraw && activity != lastRenderedStatus)
    {
        UpdateStatusLine(config, activity);
        lastRenderedStatus = activity;
    }

    if (redraw)
    {
        Console.Clear();
        Console.WriteLine("Bevdox - DevBox RDP Launcher");
        Console.WriteLine();
        DisplayStatusLine(config, activity);
        lastRenderedStatus = activity;
        Console.WriteLine();
        DisplayMenu(sorted, selectedIndex);
        Console.WriteLine();
        Console.Write("  ↑↓ Navigate  Enter/1-9 Launch  Esc Cancel launch  N Rename  R Reload  Q Quit");
        redraw = false;
    }

    // Await key press, status change, refresh or launch completion — no polling
    keyTask ??= Task.Run(() => Console.ReadKey(intercept: true));
    // Reuse the pending wait across key presses: an abandoned WaitAsync would
    // still consume a later Release and the UI would miss that status update.
    statusWait ??= statusChanged.WaitAsync(cts.Token);

    var waits = new List<Task> { keyTask, statusWait };
    if (refreshTask is not null) waits.Add(refreshTask);
    if (launchTask is not null) waits.Add(launchTask);
    if (refreshTask is not null || launchTask is not null)
        waits.Add(tickTask ??= Task.Delay(TimeSpan.FromSeconds(1), cts.Token));

    var completed = await Task.WhenAny(waits);
    if (completed != keyTask)
    {
        if (completed == statusWait)
        {
            // Status changed — drain any extra signals and loop to update display
            statusWait = null;
            while (statusChanged.Wait(0)) { }
        }
        else if (completed == tickTask)
        {
            tickTask = null;
        }
        continue;
    }

    // Key was pressed
    var key = await keyTask;
    keyTask = null;

    // Handle digit keys (1-9) for direct selection
    int digitValue = key.Key switch
    {
        >= ConsoleKey.D1 and <= ConsoleKey.D9 => key.Key - ConsoleKey.D0,
        >= ConsoleKey.NumPad1 and <= ConsoleKey.NumPad9 => key.Key - ConsoleKey.NumPad0,
        _ => -1
    };

    if (digitValue >= 1 && digitValue <= sorted.Count)
    {
        selectedIndex = digitValue - 1;
        StartLaunch(sorted[selectedIndex]);
        redraw = true;
        continue;
    }

    switch (key.Key)
    {
        case ConsoleKey.UpArrow:
            if (selectedIndex > 0)
            {
                selectedIndex--;
                redraw = true;
            }
            break;

        case ConsoleKey.DownArrow:
            if (selectedIndex < sorted.Count - 1)
            {
                selectedIndex++;
                redraw = true;
            }
            break;

        case ConsoleKey.Enter:
            StartLaunch(sorted[selectedIndex]);
            redraw = true;
            break;

        case ConsoleKey.Escape:
            launchCts?.Cancel();
            break;

        case ConsoleKey.Q:
            launchCts?.Cancel();
            goto exit;

        case ConsoleKey.R:
        {
            // Ignore while a refresh is in flight: overlapping refreshes would race over the same state
            if (refreshTask is null)
            {
                refreshTask = StartRefresh();
                redraw = true;
            }
            break;
        }

        case ConsoleKey.N:
        {
            // Renaming reads a whole line synchronously; don't start it while a launch is pending
            if (launchTask is not null) break;
            Console.WriteLine();
            await HandleRenameAsync(sorted, selectedIndex, config, configStore, cts.Token);
            redraw = true;
            break;
        }
    }
}

exit:
// Wait for background refresh before exiting
if (refreshTask is not null)
{
    try
    {
        Console.WriteLine();
        Console.Write($"  ⟳ {RefreshText()}");
        while (!refreshTask.IsCompleted)
        {
            await Task.Delay(200);
            Console.SetCursorPosition(0, Console.CursorTop);
            Console.Write($"  ⟳ {RefreshText(),-60}");
        }
        var result = await refreshTask;
        var merged = ConfigStore.Merge(config, result.Instances, result.Failures);
        await configStore.SaveAsync(merged, cts.Token);
        Console.SetCursorPosition(0, Console.CursorTop);
        Console.WriteLine($"  ✓ {refreshStatus,-60}");
        if (FailureSummary(result) is { } failures) Console.WriteLine($"  {failures}");
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        Console.WriteLine($"\n  ✗ Refresh failed: {ex.Message}");
    }
}
return 0;

Task<DiscoveryResult> StartRefresh()
{
    refreshStatus = "Starting...";
    refreshStepStarted = DateTime.UtcNow;
    refreshError = null;
    return Task.Run(() => discovery.DiscoverAsync(progress, cts.Token), cts.Token);
}

string RefreshText() => refreshStatus + ElapsedText(refreshStepStarted);

string ActivityText()
{
    var parts = new List<string>();
    if (launchTask is not null) parts.Add($"▶ {launchTarget!.EffectiveName}: {launchStatus}{ElapsedText(launchStepStarted)} (Esc to cancel)");
    else if (launchError is not null) parts.Add(launchError);
    if (refreshTask is not null) parts.Add($"⟳ {RefreshText()}");
    else if (refreshError is not null) parts.Add(refreshError);
    return string.Join("  │ ", parts);
}

void StartLaunch(DevBoxInstance target)
{
    // One launch at a time; Esc cancels the pending one
    if (launchTask is not null) return;
    launchTarget = target;
    launchStatus = "Starting...";
    launchStepStarted = DateTime.UtcNow;
    launchError = null;
    launchCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
    var token = launchCts.Token;
    launchTask = Task.Run(() => launcher.LaunchAsync(target, launchProgress, token), token);
}

// --- Helper methods ---

static void DisplayStatusLine(BevdoxConfig config, string activity)
{
    var cacheText = "";
    if (config.LastRefreshUtc is not null)
    {
        var ago = DateTimeOffset.UtcNow - config.LastRefreshUtc.Value;
        cacheText = ago.TotalMinutes < 1 ? "Cache: just now" :
            ago.TotalHours < 1 ? $"Cache: {(int)ago.TotalMinutes} min ago" :
            $"Cache: {(int)ago.TotalHours}h {ago.Minutes}m ago";
    }

    var statusText = activity.Length > 0 ? $"  │ {activity}" : "";
    Console.WriteLine(FitToWidth($"  {cacheText}{statusText}"));
}

static string FirstLine(string message)
{
    var newline = message.IndexOfAny(['\r', '\n']);
    return newline < 0 ? message : message[..newline];
}

// Only shown once a step takes noticeably long, e.g. a token request that is waiting on sign-in
static string ElapsedText(DateTime stepStarted)
{
    var elapsed = DateTime.UtcNow - stepStarted;
    return elapsed.TotalSeconds < 2 ? "" : $" ({(int)elapsed.TotalSeconds}s)";
}

static string? FailureSummary(DiscoveryResult result) => result.Failures.Count == 0 ? null
    : $"⚠ {result.Failures.Count}/{result.ProjectCount} project(s) failed — {result.Failures[0].Error}";

// Pad to clear leftovers of a longer previous status, and truncate to avoid wrapping into the next row.
static string FitToWidth(string line)
{
    var width = Math.Max(Console.WindowWidth - 1, 1);
    return line.Length > width ? line[..width] : line.PadRight(width);
}

static void UpdateStatusLine(BevdoxConfig config, string activity)
{
    // Status line is on row 2 (0=title, 1=blank, 2=status)
    var (savedLeft, savedTop) = (Console.CursorLeft, Console.CursorTop);
    Console.SetCursorPosition(0, 2);
    DisplayStatusLine(config, activity);
    Console.SetCursorPosition(savedLeft, savedTop);
}

static void DisplayMenu(List<DevBoxInstance> instances, int selectedIndex)
{
    Console.WriteLine($"  {"#",-4} {"Name",-30} {"Project",-20} {"State",-12} {"Location"}");
    Console.WriteLine($"  {"─",-4} {"─",-30} {"─",-20} {"─",-12} {"─"}");

    for (int i = 0; i < instances.Count; i++)
    {
        var d = instances[i];
        bool isSelected = i == selectedIndex;
        var stateColor = d.State.Equals("Running", StringComparison.OrdinalIgnoreCase)
            ? ConsoleColor.Green : ConsoleColor.DarkGray;

        if (isSelected)
        {
            Console.BackgroundColor = ConsoleColor.DarkCyan;
            Console.ForegroundColor = ConsoleColor.White;
        }

        Console.Write(isSelected ? "> " : "  ");
        Console.Write($"{i + 1,-3} {d.EffectiveName,-30} {d.ProjectName,-20} ");

        if (!isSelected)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = stateColor;
            Console.Write($"{d.State,-12}");
            Console.ForegroundColor = prev;
        }
        else
        {
            Console.Write($"{d.State,-12}");
        }

        Console.Write($" {d.Location}");

        if (isSelected)
            Console.ResetColor();

        Console.WriteLine();
    }
}

static async Task HandleRenameAsync(List<DevBoxInstance> sorted, int selectedIndex, BevdoxConfig config, ConfigStore configStore, CancellationToken ct)
{
    var target = sorted[selectedIndex];
    Console.Write($"  New name for '{target.EffectiveName}' (empty to reset): ");
    var newName = Console.ReadLine()?.Trim();

    target.DisplayName = string.IsNullOrEmpty(newName) ? null : newName;
    await configStore.SaveAsync(config, ct);
    Console.WriteLine("  Renamed.");
}

// Progress<T> posts callbacks to the thread pool when there is no SynchronizationContext,
// so updates can arrive out of order; report synchronously so the latest status wins.
sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

