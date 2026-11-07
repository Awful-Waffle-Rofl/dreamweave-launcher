using System;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Credentials;
using DreamWeave.Launcher.Core.Detection;
using DreamWeave.Launcher.Core.Downloads;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.Net;
using DreamWeave.Launcher.Core.SelfInstall;
using DreamWeave.Launcher.Core.State;
using DreamWeave.Launcher.Core.Telemetry;

namespace DreamWeave.Launcher.App
{
    /// <summary>
    /// The composition root of a normal (or <c>--updated</c>) run: exactly one manifest provider,
    /// state store, telemetry client, download service and credential store per process. Built once in
    /// <c>Program.Main</c> after the single-instance mutex is held, then shared by every screen.
    /// </summary>
    internal sealed class AppServices : IDisposable
    {
        private readonly HttpFetcher _fetcher;
        private int _disposed;
        private int _shutdown;

        private AppServices(AppPaths paths, string runningExePath)
        {
            Paths = paths;
            RunningExePath = runningExePath;
            State = StateStore.Open(paths);
            _fetcher = new HttpFetcher();
            Manifests = new ManifestService(new ManifestProvider(_fetcher, ManifestVerifier.CreateProduction(), paths, State));
            Telemetry = new TelemetryClient(paths, State);
            TelemetrySink = new TelemetrySink(Telemetry);
            Downloads = new DownloadService(paths);
            Credentials = new WindowsCredentialStore();
            InstallEnvironment = SelfInstallEnvironment.CreateDefault(paths);
            InstallEnvironment.Credentials = Credentials;
            AcFileSystem = new RealAcFileSystem();
            Disk = new SystemDiskSpace();
            Locator = new AcInstallLocator(new RegistryReader(), AcFileSystem);
            Verifier = new FileVerifier(AcFileSystem, State);
            Evaluator = new ComponentEvaluator(Verifier);
            Elevated = new ElevatedCopyRunner(InstallEnvironment.Processes, runningExePath);
            FlushForShutdown = FlushTelemetry;
        }

        public AppPaths Paths { get; }

        public string RunningExePath { get; }

        public StateStore State { get; }

        public ManifestService Manifests { get; }

        public TelemetryClient Telemetry { get; }

        public ITelemetrySink TelemetrySink { get; }

        public IDownloader Downloads { get; }

        public ICredentialStore Credentials { get; }

        public SelfInstallEnvironment InstallEnvironment { get; }

        public IAcFileSystem AcFileSystem { get; }

        public IDiskSpace Disk { get; }

        public AcInstallLocator Locator { get; }

        public FileVerifier Verifier { get; }

        public ComponentEvaluator Evaluator { get; }

        public ElevatedCopyRunner Elevated { get; }

        /// <summary>Builds the services. Blocking (opens the state file): call it off any UI thread.</summary>
        /// <exception cref="LauncherException">When the state file cannot be opened (StateLocked, AccessDenied, ...).</exception>
        public static AppServices Create(AppPaths paths, string runningExePath)
        {
            return new AppServices(paths ?? throw new ArgumentNullException(nameof(paths)), runningExePath);
        }

        /// <summary>Reads a value from the state store on a background thread.</summary>
        public Task<T> ReadStateAsync<T>(Func<LauncherState, T> reader)
        {
            return Task.Run(() => State.Read(reader));
        }

        /// <summary>Updates the state store on a background thread.</summary>
        public Task UpdateStateAsync(Action<LauncherState> mutate)
        {
            return Task.Run(() => State.Update(mutate));
        }

        /// <summary>
        /// Sends what is queued (bounded), then stops telemetry. Idempotent: only the first call flushes
        /// and disposes (Settings > Uninstall shuts down before the window closes, and Main's
        /// <c>finally</c> calls it again).
        /// </summary>
        public void Shutdown()
        {
            if (Interlocked.Exchange(ref _shutdown, 1) != 0)
            {
                return;
            }

            try
            {
                FlushForShutdown();
            }
            catch (AggregateException)
            {
                // Telemetry never delays or fails an exit; the queue stays on disk.
            }

            Dispose();
        }

        /// <summary>The bounded final flush; replaceable so tests can count calls.</summary>
        internal Action FlushForShutdown { get; set; }

        private void FlushTelemetry()
        {
            if (Telemetry.IsEnabled)
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                {
                    Task.Run(() => Telemetry.FlushNowAsync(cts.Token)).Wait(TimeSpan.FromSeconds(4));
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Telemetry.Dispose();
            _fetcher.Dispose();
        }
    }
}
