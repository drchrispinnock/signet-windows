using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Signet.Services;
using Velopack;
using Velopack.Sources;

namespace Signet.App.Services;

/// <summary>
/// Self-updating through Velopack (upstream: Sparkle). Signet checks the GitHub releases of this
/// repository once a day, offers any newer release it finds, and installs it in place when the
/// user restarts. Only the installed copy updates (the Setup or Portable build from a release); a
/// copy run from a build folder reports that updates are unavailable.
///
/// Windows differences: Sparkle verifies every update against an EdDSA key built into the app;
/// Velopack has no such check and relies on HTTPS from GitHub (and, once releases are code-signed,
/// on the signature). So automatic download stays off by default and an update is only applied
/// when the user says so; the Settings text does not claim a key check. Upstream's
/// <c>SUEnableAutomaticChecks</c> and <c>SUAutomaticallyUpdate</c> become the two preferences below.
/// </summary>
public sealed partial class UpdaterService : ObservableObject
{
    public const string RepositoryURL = "https://github.com/drchrispinnock/signet-windows";
    public const string AutomaticChecksKey = "updatesAutomaticChecks";
    public const string AutomaticDownloadsKey = "updatesAutomaticDownloads";
    public const string LastCheckKey = "updatesLastCheck";
    /// <summary>Testing only: a local folder of <c>vpk pack</c> output to update from instead of GitHub.</summary>
    public const string SourceOverrideVariable = "SIGNET_UPDATE_SOURCE";
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    public abstract record UpdateState
    {
        public sealed record Idle : UpdateState;
        public sealed record Checking : UpdateState;
        public sealed record UpToDate : UpdateState;
        public sealed record Available(string Version) : UpdateState;
        public sealed record Downloading(string Version, int Percent) : UpdateState;
        /// <summary>Downloaded and staged: applied by "Restart to update", or at the next launch.</summary>
        public sealed record Ready(string Version) : UpdateState;
        public sealed record Failed(string Message) : UpdateState;
    }

    public static UpdaterService Shared { get; } = new();

    private readonly UpdateManager? manager;
    private UpdateInfo? pending;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? timer;

    [ObservableProperty] public partial UpdateState State { get; set; }

    private UpdaterService()
    {
        State = new UpdateState.Idle();
        try
        {
            var source = Environment.GetEnvironmentVariable(SourceOverrideVariable) is { Length: > 0 } folder
                ? (IUpdateSource)new SimpleFileSource(new DirectoryInfo(folder))
                : new GithubSource(RepositoryURL, null, false);
            manager = new UpdateManager(source);
        }
        catch (Exception)
        {
            manager = null;
        }
    }

    /// <summary>Whether this copy can update itself: it was installed by Velopack (Setup or Portable).</summary>
    public bool IsAvailable => manager?.IsInstalled == true;

    /// <summary>The running version, e.g. 0.9.0.</summary>
    public string CurrentVersion =>
        manager is { IsInstalled: true } m ? m.CurrentVersion?.ToString() ?? "?"
        : typeof(UpdaterService).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

    /// <summary>The daily background check. On by default.</summary>
    public bool AutomaticallyChecksForUpdates
    {
        get => UserDefaults.Standard.Object(AutomaticChecksKey) is null || UserDefaults.Standard.Bool(AutomaticChecksKey);
        set
        {
            UserDefaults.Standard.Set(AutomaticChecksKey, value);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Download and stage updates silently, installing on the next launch. Off by default so an
    /// update never arrives unannounced (and, until releases are signed, never without a look).
    /// </summary>
    public bool AutomaticallyDownloadsUpdates
    {
        get => UserDefaults.Standard.Bool(AutomaticDownloadsKey);
        set
        {
            UserDefaults.Standard.Set(AutomaticDownloadsKey, value);
            OnPropertyChanged();
        }
    }

    /// <summary>When the last check ran, for Settings.</summary>
    public DateTimeOffset? LastUpdateCheckDate =>
        DateTimeOffset.TryParse(UserDefaults.Standard.String(LastCheckKey), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : null;

    public bool CanCheckForUpdates => IsAvailable && State is not (UpdateState.Checking or UpdateState.Downloading);

    partial void OnStateChanged(UpdateState value) => OnPropertyChanged(nameof(CanCheckForUpdates));

    /// <summary>Starts the daily schedule: a check soon after launch when one is due, then hourly looks at whether one is due.</summary>
    public void Start(Microsoft.UI.Dispatching.DispatcherQueue queue)
    {
        if (!IsAvailable || timer is not null) return;
        timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromHours(1);
        timer.Tick += async (_, _) => await CheckIfDue();
        timer.Start();
        var first = queue.CreateTimer();
        first.Interval = TimeSpan.FromSeconds(10);
        first.IsRepeating = false;
        first.Tick += async (_, _) => await CheckIfDue();
        first.Start();
    }

    private async Task CheckIfDue()
    {
        if (!AutomaticallyChecksForUpdates) return;
        if (LastUpdateCheckDate is { } last && DateTimeOffset.Now - last < CheckInterval) return;
        await CheckForUpdates();
    }

    /// <summary>
    /// Asks GitHub for a newer release. With automatic downloads on, a found update is downloaded
    /// and staged straight away; otherwise it is offered (<c>UpdateState.Available</c>).
    /// </summary>
    public async Task CheckForUpdates()
    {
        if (manager is null || !CanCheckForUpdates) return;
        if (State is UpdateState.Ready) return;   // one is already waiting for a restart
        State = new UpdateState.Checking();
        try
        {
            pending = await manager.CheckForUpdatesAsync();
            UserDefaults.Standard.Set(LastCheckKey, DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
            OnPropertyChanged(nameof(LastUpdateCheckDate));
            if (pending is null)
            {
                State = new UpdateState.UpToDate();
                return;
            }
            State = new UpdateState.Available(pending.TargetFullRelease.Version.ToString());
            if (AutomaticallyDownloadsUpdates) await Download();
        }
        catch (Exception e)
        {
            State = new UpdateState.Failed($"Could not check for updates: {e.Message}");
        }
    }

    /// <summary>Downloads the update found by the last check and stages it.</summary>
    public async Task Download()
    {
        if (manager is null || pending is null) return;
        var version = pending.TargetFullRelease.Version.ToString();
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        State = new UpdateState.Downloading(version, 0);
        try
        {
            await manager.DownloadUpdatesAsync(pending, percent =>
                queue?.TryEnqueue(() => { if (State is UpdateState.Downloading) State = new UpdateState.Downloading(version, percent); }));
            State = new UpdateState.Ready(version);
        }
        catch (Exception e)
        {
            State = new UpdateState.Failed($"Could not download Signet {version}: {e.Message}");
        }
    }

    /// <summary>Closes Signet, installs the staged update and starts the new version with the same arguments.</summary>
    public void RestartToUpdate()
    {
        if (manager is null || pending is null || State is not UpdateState.Ready) return;
        manager.ApplyUpdatesAndRestart(pending.TargetFullRelease, Environment.GetCommandLineArgs().Skip(1).ToArray());
    }
}
