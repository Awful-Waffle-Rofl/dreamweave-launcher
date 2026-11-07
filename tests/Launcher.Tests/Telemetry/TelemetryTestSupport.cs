using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.Net;
using DreamWeave.Launcher.Core.State;
using DreamWeave.Launcher.Core.Telemetry;
using Newtonsoft.Json.Linq;

namespace DreamWeave.Launcher.Tests.Telemetry
{
    /// <summary>A clock whose delays complete instantly (advancing the clock) and are recorded.</summary>
    internal sealed class FakeTelemetryClock : ITelemetryClock
    {
        private readonly object _lock = new object();

        public DateTime Now { get; set; } = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

        public List<TimeSpan> Delays { get; } = new List<TimeSpan>();

        public DateTime UtcNow
        {
            get
            {
                lock (_lock)
                {
                    return Now;
                }
            }
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                Delays.Add(delay);
                Now = Now + delay;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        }
    }

    /// <summary>Scripted poster: responses are consumed in order, the last one repeats; a null entry throws a network error.</summary>
    internal sealed class FakePoster : IJsonPoster
    {
        private readonly Queue<HttpPostResult> _script = new Queue<HttpPostResult>();
        private HttpPostResult _last = new HttpPostResult(202, null);

        public List<JObject> Bodies { get; } = new List<JObject>();

        public List<int> BodyLengths { get; } = new List<int>();

        public List<Uri> Uris { get; } = new List<Uri>();

        public FakePoster Then(int status, TimeSpan? retryAfter = null)
        {
            _script.Enqueue(new HttpPostResult(status, retryAfter));
            return this;
        }

        public FakePoster ThenNetworkError()
        {
            _script.Enqueue(null);
            return this;
        }

        public Task<HttpPostResult> PostJsonAsync(Uri uri, byte[] body, CancellationToken cancellationToken)
        {
            Uris.Add(uri);
            BodyLengths.Add(body.Length);
            Bodies.Add(JObject.Parse(Encoding.UTF8.GetString(body)));
            if (_script.Count > 0)
            {
                _last = _script.Dequeue();
            }

            if (_last == null)
            {
                throw new LauncherException(LauncherError.NetworkUnavailable, "offline");
            }

            return Task.FromResult(_last);
        }

        public IEnumerable<JToken> AllEvents => Bodies.SelectMany(b => (JArray)b["events"]);
    }

    /// <summary>Builds clients on a temp folder with a real state file.</summary>
    internal sealed class TelemetryHarness : IDisposable
    {
        public const string Endpoint = "https://www.example.test/tools/api/launcher/v1/events";

        private readonly TempDir _dir = new TempDir();

        public TelemetryHarness()
        {
            Paths = new AppPaths(_dir.Path);
            Store = StateStore.Open(Paths);
            Clock = new FakeTelemetryClock();
            Poster = new FakePoster();
        }

        public AppPaths Paths { get; }

        public StateStore Store { get; }

        public FakeTelemetryClock Clock { get; }

        public FakePoster Poster { get; set; }

        public string QueueFile => Path.Combine(Paths.DataDir, TelemetryClient.QueueFileName);

        public static LauncherManifest Manifest(string endpoint = Endpoint, string serverHost = "play.example.test")
        {
            return new LauncherManifest
            {
                Server = new ServerInfo { Name = "t", Host = serverHost, Port = 9000 },
                Telemetry = endpoint == null ? null : new TelemetryInfo { Endpoint = endpoint },
            };
        }

        /// <summary>A client with the flusher OFF (tests drive <see cref="TelemetryClient.FlushNowAsync"/>).</summary>
        public TelemetryClient NewClient(IJsonPoster poster = null, bool startFlusher = false, bool loopback = false)
        {
            return new TelemetryClient(Paths, Store, null, new TelemetryOptions
            {
                Poster = poster ?? Poster,
                Clock = Clock,
                LauncherVersion = "1.4.0",
                OsBuild = 26200,
                StartFlusher = startFlusher,
                AllowLoopbackHttp = loopback,
                Debounce = TimeSpan.Zero,
                MinRequestInterval = TimeSpan.Zero,
            });
        }

        /// <summary>A configured, enabled client (opt-in persisted and applied, valid endpoint).</summary>
        public TelemetryClient EnabledClient(IJsonPoster poster = null)
        {
            TelemetryClient c = NewClient(poster);
            c.SetOptIn(true);
            c.Configure(true, Manifest());
            return c;
        }

        public void Dispose()
        {
            _dir.Dispose();
        }
    }
}
