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
    /// <summary>Fallback sources: the primary first, then each mirror, all held to the same size and SHA-256.</summary>
    [TestClass]
    public class MirrorDownloadTests
    {
        private static readonly byte[] Good = MakeBytes(4096, 7);
        private static readonly byte[] SameSizeWrong = MakeBytes(4096, 9);

        private static byte[] MakeBytes(int n, byte seed)
        {
            var b = new byte[n];
            for (int i = 0; i < n; i++)
            {
                b[i] = (byte)(seed + i);
            }

            return b;
        }

        /// <summary>Scripted transport: each call runs the next behaviour for this transport name.</summary>
        private sealed class ScriptedTransport : IDownloadTransport
        {
            public ScriptedTransport(string name, params Func<DownloadRequest, TransferProgress, CancellationToken, Task>[] script)
            {
                Name = name;
                Script = new Queue<Func<DownloadRequest, TransferProgress, CancellationToken, Task>>(script);
            }

            public string Name { get; }

            public Queue<Func<DownloadRequest, TransferProgress, CancellationToken, Task>> Script { get; }

            public List<Uri> Urls { get; } = new List<Uri>();

            public List<bool> PartExistedAtStart { get; } = new List<bool>();

            public long GetResumableBytes(DownloadRequest request) => 0;

            public Task DownloadToPartAsync(DownloadRequest request, TransferProgress progress, CancellationToken cancellationToken)
            {
                Urls.Add(request.Url);
                PartExistedAtStart.Add(File.Exists(request.PartPath));
                return Script.Dequeue()(request, progress, cancellationToken);
            }
        }

        private static Func<DownloadRequest, TransferProgress, CancellationToken, Task> Writes(byte[] bytes)
        {
            return (r, p, ct) =>
            {
                File.WriteAllBytes(r.PartPath, bytes);
                return Task.FromResult(0);
            };
        }

        private static Func<DownloadRequest, TransferProgress, CancellationToken, Task> Fails(LauncherError code, bool leavePartial = false)
        {
            return (r, p, ct) =>
            {
                if (leavePartial)
                {
                    File.WriteAllBytes(r.PartPath, new byte[10]);
                }

                throw new LauncherException(code, "scripted " + code);
            };
        }

        private sealed class Rig : IDisposable
        {
            private readonly TempDir _dir = new TempDir();

            public Rig(ScriptedTransport mega, ScriptedTransport http)
            {
                Mega = mega;
                Http = http;
                Staging = Path.Combine(_dir.Path, "staging");
                Service = new DownloadService(Staging, new FakeDiskSpace(long.MaxValue), new IDownloadTransport[] { mega, http });
                Dest = Path.Combine(DownloadRequest.ComponentFolder(Staging, "ac-client"), "ac-client.exe");
                Request = new DownloadRequest("ac-client", Transports.Mega, new Uri("https://mega.nz/file/PRIMARY#key"), Good.Length, Hasher.ComputeSha256Hex(Good), Dest)
                {
                    Alternates = new[] { new DownloadSource(Transports.Http, new Uri("https://mirror.example.test/ac1install.exe"), "Mirror One") },
                    OnFallback = label => Fallbacks.Add(label),
                };
            }

            public ScriptedTransport Mega { get; }

            public ScriptedTransport Http { get; }

            public string Staging { get; }

            public string Dest { get; }

            public DownloadService Service { get; }

            public DownloadRequest Request { get; set; }

            public List<string> Fallbacks { get; } = new List<string>();

            public LauncherException Fail(CancellationToken ct = default(CancellationToken))
            {
                try
                {
                    Service.DownloadAsync(Request, null, ct).GetAwaiter().GetResult();
                }
                catch (LauncherException ex)
                {
                    return ex;
                }

                Assert.Fail("expected a LauncherException");
                return null;
            }

            public void Dispose() => _dir.Dispose();
        }

        [TestMethod]
        public void PrimaryFails_MirrorSucceeds_AndTheFallbackIsAnnounced()
        {
            using (var r = new Rig(new ScriptedTransport("mega", Fails(LauncherError.SourceQuotaExceeded)), new ScriptedTransport("http", Writes(Good))))
            {
                DownloadResult result = r.Service.DownloadAsync(r.Request, null, CancellationToken.None).GetAwaiter().GetResult();
                CollectionAssert.AreEqual(Good, File.ReadAllBytes(r.Dest));
                Assert.AreEqual(r.Dest, result.Path);
                CollectionAssert.AreEqual(new[] { "Mirror One" }, r.Fallbacks);
                Assert.AreEqual("https://mirror.example.test/ac1install.exe", r.Http.Urls[0].AbsoluteUri);
            }
        }

        [TestMethod]
        [DataRow(LauncherError.NetworkUnavailable)]
        [DataRow(LauncherError.SourceHttpError)]
        [DataRow(LauncherError.SourceQuotaExceeded)]
        [DataRow(LauncherError.SizeMismatch)]
        [DataRow(LauncherError.HashMismatch)]
        public void EachFallbackableFailure_MovesToTheMirror(LauncherError code)
        {
            using (var r = new Rig(new ScriptedTransport("mega", Fails(code)), new ScriptedTransport("http", Writes(Good))))
            {
                r.Service.DownloadAsync(r.Request, null, CancellationToken.None).GetAwaiter().GetResult();
                Assert.AreEqual(1, r.Http.Urls.Count);
            }
        }

        [TestMethod]
        public void PrimaryWritesWrongBytes_IsRejectedAndTheMirrorIsUsed()
        {
            using (var r = new Rig(new ScriptedTransport("mega", Writes(SameSizeWrong)), new ScriptedTransport("http", Writes(Good))))
            {
                r.Service.DownloadAsync(r.Request, null, CancellationToken.None).GetAwaiter().GetResult();
                CollectionAssert.AreEqual(Good, File.ReadAllBytes(r.Dest));
                Assert.AreEqual(1, r.Http.Urls.Count);
            }
        }

        [TestMethod]
        public void MirrorWithTheWrongHash_IsNeverAccepted()
        {
            using (var r = new Rig(new ScriptedTransport("mega", Fails(LauncherError.NetworkUnavailable)), new ScriptedTransport("http", Writes(SameSizeWrong))))
            {
                LauncherException ex = r.Fail();
                Assert.AreEqual(LauncherError.HashMismatch, ex.Code);
                Assert.IsFalse(File.Exists(r.Dest), "a different hash must never reach the final name");
                Assert.IsFalse(File.Exists(r.Request.PartPath), "the bad part must be deleted");
            }
        }

        [TestMethod]
        public void AllSourcesFail_ReportsTheLastError()
        {
            using (var r = new Rig(new ScriptedTransport("mega", Fails(LauncherError.SourceQuotaExceeded)), new ScriptedTransport("http", Fails(LauncherError.SourceHttpError))))
            {
                LauncherException ex = r.Fail();
                Assert.AreEqual(LauncherError.SourceHttpError, ex.Code);
                Assert.AreEqual(1, r.Mega.Urls.Count);
                Assert.AreEqual(1, r.Http.Urls.Count);
            }
        }

        [TestMethod]
        public void Cancel_DuringThePrimary_NeverFallsBack()
        {
            using (var cts = new CancellationTokenSource())
            using (var r = new Rig(new ScriptedTransport("mega", Fails(LauncherError.Cancelled)), new ScriptedTransport("http", Writes(Good))))
            {
                cts.Cancel();
                LauncherException ex = r.Fail(cts.Token);
                Assert.AreEqual(LauncherError.Cancelled, ex.Code);
                Assert.AreEqual(0, r.Http.Urls.Count, "cancel must stop the whole chain");
                Assert.AreEqual(0, r.Fallbacks.Count);
            }
        }

        [TestMethod]
        public void CancelledErrorFromThePrimary_EvenWithAnUncancelledToken_DoesNotFallBack()
        {
            using (var r = new Rig(new ScriptedTransport("mega", Fails(LauncherError.Cancelled)), new ScriptedTransport("http", Writes(Good))))
            {
                Assert.AreEqual(LauncherError.Cancelled, r.Fail().Code);
                Assert.AreEqual(0, r.Http.Urls.Count);
            }
        }

        [TestMethod]
        public void SwitchingSource_DiscardsTheOtherSourcesPartialFile()
        {
            using (var r = new Rig(new ScriptedTransport("mega", Fails(LauncherError.NetworkUnavailable, leavePartial: true)), new ScriptedTransport("http", Writes(Good))))
            {
                r.Service.DownloadAsync(r.Request, null, CancellationToken.None).GetAwaiter().GetResult();
                Assert.IsFalse(r.Http.PartExistedAtStart[0], "the mirror must not inherit the primary's partial file");
            }
        }

        [TestMethod]
        public void DiskFull_DoesNotTryAnotherSource()
        {
            using (var r = new Rig(new ScriptedTransport("mega", Fails(LauncherError.DiskFull)), new ScriptedTransport("http", Writes(Good))))
            {
                Assert.AreEqual(LauncherError.DiskFull, r.Fail().Code);
                Assert.AreEqual(0, r.Http.Urls.Count);
            }
        }

        [TestMethod]
        public void NoMirrors_BehavesAsBefore()
        {
            using (var r = new Rig(new ScriptedTransport("mega", Fails(LauncherError.SourceQuotaExceeded)), new ScriptedTransport("http", Writes(Good))))
            {
                r.Request = new DownloadRequest("ac-client", Transports.Mega, new Uri("https://mega.nz/file/PRIMARY#key"), Good.Length, Hasher.ComputeSha256Hex(Good), r.Dest);
                Assert.AreEqual(LauncherError.SourceQuotaExceeded, r.Fail().Code);
                Assert.AreEqual(0, r.Http.Urls.Count);
            }
        }

        private static Func<DownloadRequest, TransferProgress, CancellationToken, Task> ReportsHalfThenFails(LauncherError code)
        {
            return (r, p, ct) =>
            {
                p.Report(r.ExpectedSize / 2);
                throw new LauncherException(code, "scripted " + code);
            };
        }

        [TestMethod]
        public void UnknownTransportAtRuntime_FallsBackToTheMirror()
        {
            using (var r = new Rig(new ScriptedTransport("mega", Fails(LauncherError.Unknown)), new ScriptedTransport("http", Writes(Good))))
            {
                r.Request = new DownloadRequest("ac-client", "carrier-pigeon", new Uri("https://pigeon.example.test/x"), Good.Length, Hasher.ComputeSha256Hex(Good), r.Dest)
                {
                    Alternates = new[] { new DownloadSource(Transports.Http, new Uri("https://mirror.example.test/ac1install.exe"), "Mirror One") },
                    OnFallback = label => r.Fallbacks.Add(label),
                };
                r.Service.DownloadAsync(r.Request, null, CancellationToken.None).GetAwaiter().GetResult();
                CollectionAssert.AreEqual(Good, File.ReadAllBytes(r.Dest));
                Assert.AreEqual(0, r.Mega.Urls.Count, "the unknown transport was never started");
                CollectionAssert.AreEqual(new[] { "Mirror One" }, r.Fallbacks);
            }
        }

        [TestMethod]
        public void ProgressAcrossASwitch_ReachesTheEndAndStaysOnTheStep()
        {
            using (var r = new Rig(new ScriptedTransport("mega", ReportsHalfThenFails(LauncherError.NetworkUnavailable)), new ScriptedTransport("http", (req, pr, ct) =>
            {
                File.WriteAllBytes(req.PartPath, Good);
                pr.Report(Good.Length);
                return Task.FromResult(0);
            })))
            {
                var seen = new List<ProgressReport>();
                var progress = new SyncProgress(seen);
                r.Service.DownloadAsync(r.Request, progress, CancellationToken.None).GetAwaiter().GetResult();
                Assert.IsTrue(seen.Count > 0);
                Assert.IsTrue(seen.All(x => x.StepId == "ac-client"));
                Assert.IsTrue(seen.Any(x => x.Message == TransferProgress.DownloadingMessage && x.BytesDone == Good.Length), "the mirror's transfer reaches 100%");
                Assert.AreEqual(Good.Length, seen.Where(x => x.Message == TransferProgress.DownloadingMessage).Last().BytesDone);
            }
        }

        [TestMethod]
        public void EachSourceHasItsOwnPartFile_AndNoSourcePartIsLeftAfterSuccess()
        {
            using (var r = new Rig(new ScriptedTransport("mega", Fails(LauncherError.NetworkUnavailable, leavePartial: true)), new ScriptedTransport("http", (req, p, ct) =>
            {
                File.WriteAllBytes(req.PartPath, Good);
                File.WriteAllText(req.SidecarPath, "{}");
                return Task.FromResult(0);
            })))
            {
                r.Service.DownloadAsync(r.Request, null, CancellationToken.None).GetAwaiter().GetResult();
                string folder = Path.GetDirectoryName(r.Dest);
                CollectionAssert.AreEqual(new[] { "ac-client.exe" }, Directory.GetFiles(folder).Select(Path.GetFileName).ToArray());
                Assert.AreNotEqual(r.Request.PartPath, r.Request.ForSource(r.Request.Alternates[0], 1).PartPath);
                StringAssert.EndsWith(r.Request.ForSource(r.Request.Alternates[0], 1).PartPath, ".s1.part");
            }
        }

        private sealed class SyncProgress : IProgress<ProgressReport>
        {
            private readonly List<ProgressReport> _into;

            public SyncProgress(List<ProgressReport> into)
            {
                _into = into;
            }

            public void Report(ProgressReport value)
            {
                lock (_into)
                {
                    _into.Add(value);
                }
            }
        }

        [TestMethod]
        public void FromComponent_CarriesMirrorsInOrder_AndAnOldManifestHasNone()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(Path.Combine(dir.Path, "root"));
                var c = new ManifestComponent { Id = "ac-client", Kind = ComponentKinds.Installer, Transport = Transports.Mega, Url = "https://mega.nz/file/X#k", Size = 5, Sha256 = new string('A', 64) };
                Assert.AreEqual(0, DownloadRequest.FromComponent(c, paths, "a.exe").Alternates.Count);
                c.Mirrors.Add(new ManifestMirror { Transport = Transports.Http, Url = "https://one.test/a.exe", SourceLabel = "One" });
                c.Mirrors.Add(new ManifestMirror { Transport = Transports.Http, Url = "https://two.test/a.exe", SourceLabel = "Two" });
                DownloadRequest r = DownloadRequest.FromComponent(c, paths, "a.exe");
                Assert.AreEqual("https://mega.nz/file/X#k", r.Url.AbsoluteUri);
                CollectionAssert.AreEqual(new[] { "One", "Two" }, new List<string> { r.Alternates[0].Label, r.Alternates[1].Label });
            }
        }
    }
}