using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Downloads;
using DreamWeave.Launcher.Core.Manifests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Downloads
{
    [TestClass]
    public class HttpDownloadTests
    {
        private const int Size = 300000;

        private sealed class Rig : IDisposable
        {
            private readonly TempDir _dir = new TempDir();

            public Rig(Func<System.Net.HttpListenerContext, int, Task> handler, byte[] content, long? disk = null, TimeSpan? inactivity = null, string expectedSha = null, long? expectedSize = null)
            {
                Content = content;
                Server = new FakeHttpServer(handler);
                Staging = Path.Combine(_dir.Path, "staging");
                Disk = new FakeDiskSpace(disk ?? long.MaxValue);
                Options = new HttpTransportOptions
                {
                    Retry = DownloadFixtures.InstantRetry(Waits),
                    InactivityTimeout = inactivity ?? TimeSpan.FromSeconds(10),
                };
                Service = new DownloadService(Staging, Disk, new IDownloadTransport[] { new HttpTransport(DownloadFixtures.Client(), Options) });
                Request = new DownloadRequest("ac-client", Transports.Http, Server.FileUri, expectedSize ?? content.Length, expectedSha ?? Hasher.ComputeSha256Hex(content), Path.Combine(Staging, "ac-client", "ac1install.exe"));
            }

            public byte[] Content { get; }

            public FakeHttpServer Server { get; }

            public string Staging { get; }

            public FakeDiskSpace Disk { get; }

            public HttpTransportOptions Options { get; }

            public List<TimeSpan> Waits { get; } = new List<TimeSpan>();

            public DownloadService Service { get; }

            public DownloadRequest Request { get; }

            public DownloadResult Result { get; private set; }

            public ListProgress Progress { get; private set; } = new ListProgress();

            public LauncherError Run(CancellationToken token = default(CancellationToken), ListProgress progress = null)
            {
                Progress = progress ?? new ListProgress();
                return DownloadFixtures.Run(async () => { Result = await Service.DownloadAsync(Request, Progress, token).ConfigureAwait(false); });
            }

            public void SeedPart(byte[] bytes, string etag)
            {
                SeedPart(bytes, etag, Request.Url.AbsoluteUri, Request.ExpectedSha256, Request.ExpectedSize);
            }

            public void SeedPart(byte[] bytes, string etag, string url, string sha, long size)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Request.DestinationPath));
                File.WriteAllBytes(Request.PartPath, bytes);
                File.WriteAllText(
                    Request.SidecarPath,
                    "{ \"url\": \"" + url + "\", \"expectedSha256\": \"" + sha + "\", \"expectedSize\": " + size + ", \"etag\": " + (etag == null ? "null" : "\"" + etag.Replace("\"", "\\\"") + "\"") + ", \"lastModified\": null }");
            }

            public void AssertFinalFileCorrect()
            {
                CollectionAssert.AreEqual(Content, File.ReadAllBytes(Request.DestinationPath));
                Assert.IsFalse(File.Exists(Request.PartPath), "part should be gone");
                Assert.IsFalse(File.Exists(Request.SidecarPath), "sidecar should be gone");
                Assert.AreEqual(Hasher.ComputeSha256Hex(Content), Result.Sha256);
            }

            public void Dispose()
            {
                Server.Dispose();
                _dir.Dispose();
            }
        }

        [TestMethod]
        public void FullDownload_VerifiesAndPromotes_ProbingWithRangeGetNotHead()
        {
            byte[] content = DownloadFixtures.Content(Size);
            using (var rig = new Rig((ctx, i) => Serve.FileAsync(ctx, content), content))
            {
                Assert.AreEqual(LauncherError.None, rig.Run());
                rig.AssertFinalFileCorrect();
                Assert.IsFalse(rig.Result.AlreadyPresent);
                SeenRequest[] seen = rig.Server.Requests.ToArray();
                Assert.IsTrue(seen.All(r => r.Method == "GET"), "never HEAD");
                Assert.AreEqual("bytes=0-0", seen[0].Range);
                Assert.AreEqual(2, seen.Length);
                Assert.IsNull(seen[1].Range, "a fresh download fetches the whole body");
            }
        }

        [TestMethod]
        public void AlreadyPresentFinalFile_IsInstantSuccess_WithNoRequests()
        {
            byte[] content = DownloadFixtures.Content(Size);
            using (var rig = new Rig((ctx, i) => Serve.FileAsync(ctx, content), content))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(rig.Request.DestinationPath));
                File.WriteAllBytes(rig.Request.DestinationPath, content);
                Assert.AreEqual(LauncherError.None, rig.Run());
                Assert.IsTrue(rig.Result.AlreadyPresent);
                Assert.AreEqual(0, rig.Server.Requests.Count);
                Assert.IsTrue(rig.Progress.Reports.Any(r => r.Message == "Verifying"), "hashing the existing file reports a Verifying phase");
            }
        }

        [TestMethod]
        public void AbortMidStream_ThenResume_SendsRangeAndIfRange_AndHashesCorrectly()
        {
            byte[] content = DownloadFixtures.Content(Size);
            using (var rig = new Rig((ctx, i) => i == 1 ? Serve.TruncatedAsync(ctx, content, Size / 3) : Serve.FileAsync(ctx, content), content))
            {
                Assert.AreEqual(LauncherError.None, rig.Run());
                rig.AssertFinalFileCorrect();
                SeenRequest[] seen = rig.Server.Requests.ToArray();
                Assert.AreEqual(3, seen.Length, "probe, truncated GET, resumed GET");
                Assert.IsNotNull(seen[2].Range, "resume must send Range");
                long resumedFrom = long.Parse(seen[2].Range.Substring("bytes=".Length).TrimEnd('-'), System.Globalization.CultureInfo.InvariantCulture);
                Assert.IsTrue(resumedFrom > 0 && resumedFrom <= Size / 3, "resumed from " + resumedFrom);
                Assert.AreEqual("\"v1\"", seen[2].IfRange);
                Assert.AreEqual(1, rig.Waits.Count, "one retry");
            }
        }

        [TestMethod]
        public void ServerIgnoresResumeRange_RestartsFromZero_AndSucceeds()
        {
            byte[] content = DownloadFixtures.Content(Size);

            // Honours only the 1-byte probe; answers every other range with the full body (200).
            Func<System.Net.HttpListenerContext, int, Task> handler = (ctx, i) =>
                Serve.FileAsync(ctx, content, honourRanges: ctx.Request.Headers["Range"] == "bytes=0-0");
            using (var rig = new Rig(handler, content))
            {
                rig.SeedPart(DownloadFixtures.Content(Size / 2, seed: 99), "\"v1\"");
                Assert.AreEqual(LauncherError.None, rig.Run());
                rig.AssertFinalFileCorrect();
                SeenRequest[] seen = rig.Server.Requests.ToArray();
                Assert.AreEqual("bytes=" + (Size / 2) + "-", seen[1].Range, "the seeded part was offered for resume");
                Assert.AreEqual(0, rig.Waits.Count, "a 200 restart needs no retry");
            }
        }

        [TestMethod]
        public void ProbeWithoutRangeSupport_IgnoresExistingPart()
        {
            byte[] content = DownloadFixtures.Content(Size);
            using (var rig = new Rig((ctx, i) => Serve.FileAsync(ctx, content, honourRanges: false), content))
            {
                rig.SeedPart(DownloadFixtures.Content(Size / 2, seed: 99), "\"v1\"");
                Assert.AreEqual(LauncherError.None, rig.Run());
                rig.AssertFinalFileCorrect();
                Assert.IsNull(rig.Server.Requests[1].Range);
            }
        }

        [TestMethod]
        public void ETagChanged_RestartsFromZero()
        {
            byte[] content = DownloadFixtures.Content(Size);
            using (var rig = new Rig((ctx, i) => Serve.FileAsync(ctx, content, etag: "\"v2\""), content))
            {
                rig.SeedPart(DownloadFixtures.Content(Size / 2, seed: 99), "\"v1\"");
                Assert.AreEqual(LauncherError.None, rig.Run());
                rig.AssertFinalFileCorrect();
                SeenRequest[] seen = rig.Server.Requests.ToArray();
                Assert.AreEqual(2, seen.Length);
                Assert.IsNull(seen[1].Range, "a changed ETag must not be resumed");
            }
        }

        [TestMethod]
        public void NoValidator_ResumesWithBareRange()
        {
            // archive.org sends neither ETag nor Last-Modified.
            byte[] content = DownloadFixtures.Content(Size);
            using (var rig = new Rig((ctx, i) => i == 1 ? Serve.TruncatedAsync(ctx, content, Size / 3, etag: null) : Serve.FileAsync(ctx, content, etag: null), content))
            {
                Assert.AreEqual(LauncherError.None, rig.Run());
                rig.AssertFinalFileCorrect();
                SeenRequest[] seen = rig.Server.Requests.ToArray();
                Assert.AreEqual(3, seen.Length);
                Assert.IsNotNull(seen[2].Range);
                Assert.IsNull(seen[2].IfRange);
            }
        }

        [TestMethod]
        public void PartWithMismatchedSidecar_IsNotResumed()
        {
            byte[] content = DownloadFixtures.Content(Size);
            string[] cases = { "sha", "url", "size" };
            foreach (string field in cases)
            {
                using (var rig = new Rig((ctx, i) => Serve.FileAsync(ctx, content), content))
                {
                    string url = field == "url" ? new Uri(rig.Server.BaseUri, "other.bin").AbsoluteUri : rig.Request.Url.AbsoluteUri;
                    string sha = field == "sha" ? new string('B', 64) : rig.Request.ExpectedSha256;
                    long size = field == "size" ? Size + 1 : Size;
                    rig.SeedPart(content.Take(Size / 2).ToArray(), "\"v1\"", url, sha, size);
                    Assert.AreEqual(LauncherError.None, rig.Run(), field);
                    rig.AssertFinalFileCorrect();
                    Assert.IsNull(rig.Server.Requests[1].Range, "a sidecar with a different " + field + " must not be resumed");
                }
            }
        }

        [TestMethod]
        public void NoValidator_ResumeOverChangedUpstream_FailsHashMismatch_AndDeletesPartAndSidecar()
        {
            // The accepted bare-Range trade-off: the server changed the file (same size) and sent no
            // validator, so the stitched file is wrong. The pinned hash must catch it and clean up,
            // and the next run must succeed from zero.
            byte[] oldContent = DownloadFixtures.Content(Size, seed: 1);
            byte[] newContent = DownloadFixtures.Content(Size, seed: 2);
            using (var rig = new Rig((ctx, i) => Serve.FileAsync(ctx, newContent, etag: null), newContent))
            {
                rig.SeedPart(oldContent.Take(Size / 2).ToArray(), null);
                Assert.AreEqual(LauncherError.HashMismatch, rig.Run());
                Assert.AreEqual("bytes=" + (Size / 2) + "-", rig.Server.Requests[1].Range, "it really did resume");
                Assert.IsFalse(File.Exists(rig.Request.PartPath), "part deleted");
                Assert.IsFalse(File.Exists(rig.Request.SidecarPath), "sidecar deleted");
                Assert.IsFalse(File.Exists(rig.Request.DestinationPath), "no final file");

                Assert.AreEqual(LauncherError.None, rig.Run());
                rig.AssertFinalFileCorrect();
                Assert.IsNull(rig.Server.Requests.Last().Range, "the follow-up run starts from zero");
            }
        }

        [TestMethod]
        public void ProbeTotalDiffersFromManifest_FailsSizeMismatch_BeforeDownloading()
        {
            byte[] content = DownloadFixtures.Content(Size);
            using (var rig = new Rig((ctx, i) => Serve.FileAsync(ctx, content), content, expectedSize: Size - 1))
            {
                Assert.AreEqual(LauncherError.SizeMismatch, rig.Run());
                Assert.AreEqual(1, rig.Server.Requests.Count);
                Assert.IsFalse(File.Exists(rig.Request.PartPath));
            }
        }

        [TestMethod]
        public void BodyLongerThanExpected_AbortsSizeMismatch_AndDeletesPart()
        {
            byte[] content = DownloadFixtures.Content(Size);
            byte[] longer = DownloadFixtures.Content(Size + 5000);
            Func<System.Net.HttpListenerContext, int, Task> handler = async (ctx, i) =>
            {
                if (i == 0)
                {
                    // Lie about the total in the probe.
                    ctx.Response.StatusCode = 206;
                    ctx.Response.Headers["Content-Range"] = "bytes 0-0/" + Size;
                    ctx.Response.ContentLength64 = 1;
                    await ctx.Response.OutputStream.WriteAsync(longer, 0, 1).ConfigureAwait(false);
                    ctx.Response.Close();
                    return;
                }

                ctx.Response.StatusCode = 200;
                ctx.Response.SendChunked = true;
                await ctx.Response.OutputStream.WriteAsync(longer, 0, longer.Length).ConfigureAwait(false);
                ctx.Response.Close();
            };
            using (var rig = new Rig(handler, content))
            {
                Assert.AreEqual(LauncherError.SizeMismatch, rig.Run());
                Assert.IsFalse(File.Exists(rig.Request.PartPath));
                Assert.IsFalse(File.Exists(rig.Request.SidecarPath));
                Assert.IsFalse(File.Exists(rig.Request.DestinationPath));
                Assert.AreEqual(0, rig.Waits.Count, "a size violation is not retried");
                Assert.IsTrue(rig.Progress.Downloading.All(r => r.BytesDone <= Size), "never counted past the expected size");
            }
        }

        [TestMethod]
        public void HashMismatch_DeletesFile_AndThrows()
        {
            byte[] content = DownloadFixtures.Content(Size);
            using (var rig = new Rig((ctx, i) => Serve.FileAsync(ctx, content), content, expectedSha: new string('A', 64)))
            {
                Assert.AreEqual(LauncherError.HashMismatch, rig.Run());
                Assert.IsFalse(File.Exists(rig.Request.PartPath));
                Assert.IsFalse(File.Exists(rig.Request.SidecarPath));
                Assert.IsFalse(File.Exists(rig.Request.DestinationPath));
            }
        }

        [TestMethod]
        public void ServiceUnavailable_ThenSuccess_Retries()
        {
            byte[] content = DownloadFixtures.Content(Size);
            using (var rig = new Rig((ctx, i) => i == 0 ? Serve.StatusAsync(ctx, 503) : Serve.FileAsync(ctx, content), content))
            {
                Assert.AreEqual(LauncherError.None, rig.Run());
                rig.AssertFinalFileCorrect();
                Assert.AreEqual(1, rig.Waits.Count);
                Assert.AreEqual(rig.Options.Retry.BaseDelay, rig.Waits[0]);
            }
        }

        [TestMethod]
        public void NotFound_FailsWithoutRetry()
        {
            byte[] content = DownloadFixtures.Content(Size);
            using (var rig = new Rig((ctx, i) => Serve.StatusAsync(ctx, 404), content))
            {
                Assert.AreEqual(LauncherError.SourceHttpError, rig.Run());
                Assert.AreEqual(1, rig.Server.Requests.Count);
                Assert.AreEqual(0, rig.Waits.Count);
            }
        }

        [TestMethod]
        public void TooManyRequests_HonoursRetryAfter_ThenQuotaExceeded()
        {
            byte[] content = DownloadFixtures.Content(Size);
            using (var rig = new Rig((ctx, i) => Serve.StatusAsync(ctx, 429, retryAfter: "7"), content))
            {
                Assert.AreEqual(LauncherError.SourceQuotaExceeded, rig.Run());
                Assert.AreEqual(rig.Options.Retry.MaxRetries + 1, rig.Server.Requests.Count);
                Assert.AreEqual(rig.Options.Retry.MaxRetries, rig.Waits.Count);
                Assert.IsTrue(rig.Waits.All(w => w == TimeSpan.FromSeconds(7)), string.Join(",", rig.Waits));
            }
        }

        [TestMethod]
        public void ExponentialBackoff_DoublesEachRetry_AndCapsRetryAfter()
        {
            var policy = new RetryPolicy();
            Assert.AreEqual(TimeSpan.FromSeconds(2), policy.DelayFor(0, null));
            Assert.AreEqual(TimeSpan.FromSeconds(4), policy.DelayFor(1, null));
            Assert.AreEqual(TimeSpan.FromSeconds(8), policy.DelayFor(2, null));
            Assert.AreEqual(policy.MaxDelay, policy.DelayFor(0, TimeSpan.FromHours(5)));
        }

        [TestMethod]
        public void InactivityTimeout_AbandonsStalledRead_AndResumes()
        {
            byte[] content = DownloadFixtures.Content(Size);
            FakeHttpServer server = null;
            Func<System.Net.HttpListenerContext, int, Task> handler = (ctx, i) =>
                i == 1 ? Serve.StallAsync(ctx, content, Size / 4, server.Stopping) : Serve.FileAsync(ctx, content);
            using (var rig = new Rig(handler, content, inactivity: TimeSpan.FromMilliseconds(400)))
            {
                server = rig.Server;
                Assert.AreEqual(LauncherError.None, rig.Run());
                rig.AssertFinalFileCorrect();
                SeenRequest[] seen = rig.Server.Requests.ToArray();
                Assert.AreEqual(3, seen.Length);
                Assert.IsNotNull(seen[2].Range, "the stalled transfer is resumed, not restarted");
                Assert.AreEqual(1, rig.Waits.Count);
            }
        }

        [TestMethod]
        public void Cancellation_KeepsPartAndSidecar_AndALaterRunResumes()
        {
            byte[] content = DownloadFixtures.Content(Size);
            FakeHttpServer server = null;
            bool stall = true;
            var cts = new CancellationTokenSource();
            Func<System.Net.HttpListenerContext, int, Task> handler = async (ctx, i) =>
            {
                if (i == 1 && stall)
                {
                    // Send half, give the client time to write it, then the "player" cancels mid-transfer.
                    Task serving = Serve.StallAsync(ctx, content, Size / 2, server.Stopping);
                    await Task.Delay(500).ConfigureAwait(false);
                    cts.Cancel();
                    await serving.ConfigureAwait(false);
                    return;
                }

                await Serve.FileAsync(ctx, content).ConfigureAwait(false);
            };
            using (var rig = new Rig(handler, content))
            using (cts)
            {
                server = rig.Server;
                Assert.AreEqual(LauncherError.Cancelled, rig.Run(cts.Token));
                Assert.IsTrue(File.Exists(rig.Request.PartPath), "part kept");
                Assert.IsTrue(File.Exists(rig.Request.SidecarPath), "sidecar kept");
                long kept = new FileInfo(rig.Request.PartPath).Length;
                Assert.IsTrue(kept > 0 && kept <= Size / 2, "kept " + kept);

                stall = false;
                Assert.AreEqual(LauncherError.None, rig.Run());
                rig.AssertFinalFileCorrect();
                SeenRequest last = rig.Server.Requests.Last();
                Assert.AreEqual("bytes=" + kept + "-", last.Range);
                Assert.AreEqual("\"v1\"", last.IfRange);
            }
        }

        [TestMethod]
        public void InsufficientDisk_FailsDiskFull_BeforeAnyRequest()
        {
            byte[] content = DownloadFixtures.Content(Size);
            using (var rig = new Rig((ctx, i) => Serve.FileAsync(ctx, content), content, disk: DownloadService.FreeSpaceMargin + Size - 1))
            {
                Assert.AreEqual(LauncherError.DiskFull, rig.Run());
                Assert.AreEqual(0, rig.Server.Requests.Count);
                Assert.AreEqual(1, rig.Disk.Calls);
            }
        }

        [TestMethod]
        public void DiskCheck_CountsOnlyRemainingBytes_WhenResuming()
        {
            byte[] content = DownloadFixtures.Content(Size);
            using (var rig = new Rig((ctx, i) => Serve.FileAsync(ctx, content), content, disk: DownloadService.FreeSpaceMargin + (Size / 2)))
            {
                byte[] half = content.Take(Size / 2).ToArray();
                rig.SeedPart(half, "\"v1\"");
                Assert.AreEqual(LauncherError.None, rig.Run());
                rig.AssertFinalFileCorrect();
            }
        }

        [TestMethod]
        public void Progress_IsMonotonic_EndsAtSize_ThenVerifies()
        {
            byte[] content = DownloadFixtures.Content(2 * 1024 * 1024);
            using (var rig = new Rig((ctx, i) => Serve.FileAsync(ctx, content), content))
            {
                Assert.AreEqual(LauncherError.None, rig.Run());
                List<ProgressReport> reports = rig.Progress.Reports;
                List<ProgressReport> downloading = rig.Progress.Downloading;
                Assert.IsTrue(downloading.Count >= 1);
                for (int i = 1; i < downloading.Count; i++)
                {
                    Assert.IsTrue(downloading[i].BytesDone >= downloading[i - 1].BytesDone, "report " + i + " went backwards");
                }

                Assert.AreEqual(content.Length, downloading.Last().BytesDone);
                Assert.IsTrue(downloading.All(r => r.BytesTotal == content.Length && r.StepId == "ac-client"));
                int lastDownload = reports.FindLastIndex(r => r.Message == TransferProgress.DownloadingMessage);
                int firstVerify = reports.FindIndex(r => r.Message == "Verifying");
                Assert.IsTrue(firstVerify > lastDownload, "verifying follows the final download report");
                Assert.AreEqual(content.Length, reports.Last().BytesDone);
                Assert.AreEqual("ac-client", reports.Last().StepId);
            }
        }

        [TestMethod]
        public void Redirect_IsFollowed_AndRangeIsResent()
        {
            byte[] content = DownloadFixtures.Content(Size);
            Func<System.Net.HttpListenerContext, int, Task> handler = (ctx, i) =>
            {
                if (ctx.Request.Url.AbsolutePath == "/file.bin")
                {
                    ctx.Response.StatusCode = 302;
                    ctx.Response.RedirectLocation = "/real.bin";
                    ctx.Response.ContentLength64 = 0;
                    ctx.Response.Close();
                    return Task.CompletedTask;
                }

                return Serve.FileAsync(ctx, content);
            };
            using (var rig = new Rig(handler, content))
            {
                Assert.AreEqual(LauncherError.None, rig.Run());
                rig.AssertFinalFileCorrect();
                SeenRequest[] seen = rig.Server.Requests.ToArray();
                Assert.AreEqual("/real.bin", seen[1].Path);
                Assert.AreEqual("bytes=0-0", seen[1].Range, "Range survives the redirect");
            }
        }

        [TestMethod]
        public void FromComponent_TargetsStaging_AndRejectsPathsInFileName()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var component = new ManifestComponent { Id = "ac-client", Transport = Transports.Http, Url = "https://example.test/a.exe", Size = 3, Sha256 = new string('A', 64) };
                DownloadRequest req = DownloadRequest.FromComponent(component, paths, "a.exe");
                Assert.AreEqual(Path.Combine(paths.StagingDir, "ac-client", "a.exe"), req.DestinationPath, "each component gets its own folder");
                Assert.AreEqual("ac-client", req.StepId);
                Assert.AreEqual(req.DestinationPath + ".part", req.PartPath);
                Assert.AreEqual(req.DestinationPath + ".part.json", req.SidecarPath);
                Assert.ThrowsExactly<ArgumentException>(() => DownloadRequest.FromComponent(component, paths, "..\\a.exe"));
                component.Url = "not a url";
                Assert.AreEqual(LauncherError.ManifestInvalid, Assert.ThrowsExactly<LauncherException>(() => DownloadRequest.FromComponent(component, paths, "a.exe")).Code);
            }
        }

        [TestMethod]
        public void UnknownTransport_IsManifestInvalid()
        {
            byte[] content = DownloadFixtures.Content(16);
            using (var rig = new Rig((ctx, i) => Serve.FileAsync(ctx, content), content))
            {
                var bad = new DownloadRequest("x", "ftp", rig.Server.FileUri, 16, Hasher.ComputeSha256Hex(content), Path.Combine(rig.Staging, "x", "x.bin"));
                Assert.AreEqual(LauncherError.ManifestInvalid, DownloadFixtures.Run(() => rig.Service.DownloadAsync(bad, null, CancellationToken.None)));
                Assert.AreEqual(0, rig.Server.Requests.Count);
            }
        }

        [TestMethod]
        public void RestartTruncatesPartBeforeSidecarWrite()
        {
            // A changed ETag forces a restart from zero. With the sidecar locked the sidecar write
            // fails; by then the stale part must already be truncated, never left beside new metadata.
            byte[] content = DownloadFixtures.Content(Size);
            using (var rig = new Rig((ctx, i) => Serve.FileAsync(ctx, content, etag: "\"v2\""), content))
            {
                rig.SeedPart(DownloadFixtures.Content(Size / 2, seed: 99), "\"v1\"");
                LauncherError error;
                using (new FileStream(rig.Request.SidecarPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    error = rig.Run();
                }

                Assert.AreNotEqual(LauncherError.None, error, "the sidecar write must have failed");
                Assert.IsTrue(File.Exists(rig.Request.PartPath));
                Assert.AreEqual(0, new FileInfo(rig.Request.PartPath).Length, "part truncated before the sidecar write");
                Assert.IsFalse(File.Exists(rig.Request.DestinationPath));
            }
        }

        [TestMethod]
        public void DestinationMustBeInItsComponentFolder()
        {
            byte[] content = DownloadFixtures.Content(16);
            using (var rig = new Rig((ctx, i) => Serve.FileAsync(ctx, content), content))
            {
                string sha = Hasher.ComputeSha256Hex(content);
                var flat = new DownloadRequest("ac-client", Transports.Http, rig.Server.FileUri, 16, sha, Path.Combine(rig.Staging, "a.exe"));
                Assert.ThrowsExactly<ArgumentException>(() => rig.Service.DownloadAsync(flat, null, CancellationToken.None).GetAwaiter().GetResult());
                var other = new DownloadRequest("ac-client", Transports.Http, rig.Server.FileUri, 16, sha, Path.Combine(rig.Staging, "eor-dats", "a.exe"));
                Assert.ThrowsExactly<ArgumentException>(() => rig.Service.DownloadAsync(other, null, CancellationToken.None).GetAwaiter().GetResult());
                Assert.AreEqual(0, rig.Server.Requests.Count);
            }
        }

        [TestMethod]
        public void UnsafeComponentId_IsManifestInvalid()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                foreach (string id in new[] { "..", ".", "a\\b", "a/b", "", " x", "x." })
                {
                    var component = new ManifestComponent { Id = id, Transport = Transports.Http, Url = "https://example.test/a.exe", Size = 3, Sha256 = new string('A', 64) };
                    LauncherException ex = Assert.ThrowsExactly<LauncherException>(() => DownloadRequest.FromComponent(component, paths, "a.exe"), "id '" + id + "'");
                    Assert.AreEqual(LauncherError.ManifestInvalid, ex.Code);
                }
            }
        }

        [TestMethod]
        public void DestinationOutsideStaging_IsRejected()
        {
            byte[] content = DownloadFixtures.Content(16);
            using (var rig = new Rig((ctx, i) => Serve.FileAsync(ctx, content), content))
            {
                var bad = new DownloadRequest("x", Transports.Http, rig.Server.FileUri, 16, Hasher.ComputeSha256Hex(content), Path.Combine(Path.GetTempPath(), "elsewhere.bin"));
                Assert.ThrowsExactly<ArgumentException>(() => rig.Service.DownloadAsync(bad, null, CancellationToken.None).GetAwaiter().GetResult());
            }
        }
    }
}
