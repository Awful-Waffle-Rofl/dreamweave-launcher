using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using DreamWeave.Launcher.Core.Launch;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Launch
{
    [TestClass]
    public class ClientLockReleaserTests
    {
        private const int Self = 1;
        private const int Target = 4242;
        private const int Bot = 77;
        private const int SemaphoreType = 40;
        private const int EventType = 17;
        private const int MutantType = 18;
        private const int LockObject = 900;
        private const int OtherSemaphore = 901;
        private const int AccessDenied = unchecked((int)0xC0000022);

        /// <summary>A fake kernel: per-process handle tables of (value, object, type); the lock exists while any process holds it.</summary>
        private sealed class FakeKernel : IClientHandleApi
        {
            private readonly Dictionary<IntPtr, int> _processHandles = new Dictionary<IntPtr, int>();
            private int _nextOwn = 0x5000;
            private int _nextProcess = 0x9000;
            private int _semaphoreLookups;

            public FakeKernel()
            {
                Tables[Self] = new List<Row>();
                _processHandles[CurrentProcess] = Self;
            }

            public sealed class Row
            {
                public IntPtr Value;
                public int Object;
                public int Type;
            }

            public Dictionary<int, List<Row>> Tables { get; } = new Dictionary<int, List<Row>>();

            public bool Supported { get; set; } = true;

            public IntPtr CurrentProcess { get; } = new IntPtr(-1);

            public int LastError { get; private set; }

            /// <summary>The lock can be opened only once this many OpenSemaphore calls have missed (int.MaxValue: never).</summary>
            public int LockAppearsAfter { get; set; }

            public HashSet<int> Exited { get; } = new HashSet<int>();

            /// <summary>Processes whose handle table query fails (access denied).</summary>
            public HashSet<int> TableFails { get; } = new HashSet<int>();

            /// <summary>The launcher's own table leaves out its own semaphore handles.</summary>
            public bool HideOwnSemaphores { get; set; }

            /// <summary>Every process handle passed to any call.</summary>
            public List<IntPtr> ProcessHandlesUsed { get; } = new List<IntPtr>();

            /// <summary>Every (pid, call) made through a process handle.</summary>
            public List<KeyValuePair<int, string>> ProcessCalls { get; } = new List<KeyValuePair<int, string>>();

            /// <summary>Every handle duplicated into the launcher: pid and value.</summary>
            public List<KeyValuePair<int, IntPtr>> Duplicated { get; } = new List<KeyValuePair<int, IntPtr>>();

            /// <summary>Every close-in-process: pid, value, and how many launcher handles to that object were still open.</summary>
            public List<Tuple<int, IntPtr, int>> Closes { get; } = new List<Tuple<int, IntPtr, int>>();

            public List<IntPtr> ClosedProcessHandles { get; } = new List<IntPtr>();

            /// <summary>A CreateProcess-style handle for <paramref name="pid"/>, as the starter would return.</summary>
            public StartedClient Started(int pid)
            {
                var h = new IntPtr(_nextProcess++);
                _processHandles[h] = pid;
                return new StartedClient(pid, h);
            }

            public void Add(int pid, int value, int obj, int type)
            {
                if (!Tables.ContainsKey(pid))
                {
                    Tables[pid] = new List<Row>();
                }

                Tables[pid].Add(new Row { Value = new IntPtr(value), Object = obj, Type = type });
            }

            public int OwnHandlesTo(int obj) => Tables[Self].Count(r => r.Object == obj);

            public bool HasExited(IntPtr process)
            {
                return Exited.Contains(Pid(process, "exited"));
            }

            public IReadOnlyList<ClientHandleEntry> HandleTable(IntPtr process)
            {
                int pid = Pid(process, "table");
                if (TableFails.Contains(pid))
                {
                    LastError = AccessDenied;
                    return null;
                }

                IEnumerable<Row> rows = Tables[pid];
                if (pid == Self && HideOwnSemaphores)
                {
                    rows = rows.Where(r => r.Type != SemaphoreType);
                }

                return rows.Select(r => new ClientHandleEntry(r.Value, r.Type)).ToList();
            }

            public IntPtr OpenSemaphore(string name)
            {
                if (name != ClientLockReleaser.LockName || _semaphoreLookups++ < LockAppearsAfter)
                {
                    return IntPtr.Zero;
                }

                bool exists = Tables.Any(t => t.Value.Any(r => r.Object == LockObject));
                return exists ? AddOwn(LockObject, SemaphoreType) : IntPtr.Zero;
            }

            public IntPtr DuplicateIntoSelf(IntPtr process, IntPtr value)
            {
                int pid = Pid(process, "duplicate");
                Duplicated.Add(new KeyValuePair<int, IntPtr>(pid, value));
                Row row = Tables[pid].FirstOrDefault(r => r.Value == value);
                return row == null ? IntPtr.Zero : AddOwn(row.Object, row.Type);
            }

            public bool SameObject(IntPtr first, IntPtr second)
            {
                return OwnRow(first).Object == OwnRow(second).Object;
            }

            public bool CloseInProcess(IntPtr process, IntPtr value)
            {
                int pid = Pid(process, "close");
                Row row = Tables[pid].FirstOrDefault(r => r.Value == value);
                if (row == null)
                {
                    return false;
                }

                Closes.Add(Tuple.Create(pid, value, OwnHandlesTo(row.Object)));
                Tables[pid].Remove(row);
                return true;
            }

            public void CloseOwn(IntPtr handle)
            {
                if (_processHandles.ContainsKey(handle))
                {
                    ClosedProcessHandles.Add(handle);
                    return;
                }

                Tables[Self].Remove(OwnRow(handle));
            }

            private IntPtr AddOwn(int obj, int type)
            {
                var h = new IntPtr(_nextOwn += 4);
                Tables[Self].Add(new Row { Value = h, Object = obj, Type = type });
                return h;
            }

            private Row OwnRow(IntPtr h)
            {
                return Tables[Self].Single(r => r.Value == h);
            }

            private int Pid(IntPtr process, string call)
            {
                ProcessHandlesUsed.Add(process);
                int pid = _processHandles[process];
                ProcessCalls.Add(new KeyValuePair<int, string>(pid, call));
                return pid;
            }
        }

        /// <summary>A clock that only moves when the releaser sleeps; throws if it sleeps forever.</summary>
        private sealed class FakeTime
        {
            public TimeSpan Now { get; private set; }

            public int Sleeps { get; private set; }

            /// <summary>Runs on each sleep (after counting it).</summary>
            public Action<int> OnSleep { get; set; }

            public Func<Func<TimeSpan>> Clock => () =>
            {
                TimeSpan start = Now;
                return () => Now - start;
            };

            public void Sleep(TimeSpan t, CancellationToken cancel)
            {
                if (++Sleeps > 1000)
                {
                    throw new InvalidOperationException("the releaser never gave up");
                }

                OnSleep?.Invoke(Sleeps);
                Now += t;
            }
        }

        private static FakeKernel Scene()
        {
            var k = new FakeKernel();

            // The started client: an event, an unrelated semaphore listed BEFORE the lock, the lock, a mutant.
            k.Add(Target, 0x10, 500, EventType);
            k.Add(Target, 0x20, OtherSemaphore, SemaphoreType);
            k.Add(Target, 0x30, LockObject, SemaphoreType);
            k.Add(Target, 0x40, 501, MutantType);

            // A bot client the launcher did not start, with handles of its own (not the lock).
            k.Add(Bot, 0x30, OtherSemaphore, SemaphoreType);
            k.Add(Bot, 0x44, 502, EventType);
            return k;
        }

        private static ClientLockReleaser Releaser(FakeKernel k, FakeTime time, List<string> log)
        {
            return new ClientLockReleaser(k, log.Add, time.Clock, time.Sleep);
        }

        // Mutation checked: removing the SameObject match (same = true) closes 0x20 too and fails this test.
        [TestMethod]
        public void Release_ClosesOnlyTheLockHandle_InTheStartedProcess()
        {
            FakeKernel k = Scene();
            var log = new List<string>();

            ClientLockOutcome outcome = Releaser(k, new FakeTime(), log).Release(k.Started(Target));

            Assert.AreEqual(ClientLockOutcome.Released, outcome);
            Assert.AreEqual(1, k.Closes.Count, "exactly one handle is closed");
            Assert.AreEqual(Target, k.Closes[0].Item1);
            Assert.AreEqual(new IntPtr(0x30), k.Closes[0].Item2);
            CollectionAssert.AreEqual(new[] { 0x10, 0x20, 0x40 }, k.Tables[Target].Select(r => r.Value.ToInt32()).ToArray(), "every other handle of the client is left alone");
            Assert.AreEqual(0, k.Tables[Self].Count, "the launcher keeps no handle of its own");
            Assert.AreEqual(1, log.Count);
            StringAssert.StartsWith(log[0], "client-lock pid=4242 released handle=0x30");
        }

        // Mutation checked: CloseInProcess(_api.CurrentProcess, value) instead of the client's handle
        // closes in pid 1 and fails this test.
        [TestMethod]
        public void Release_UsesOnlyTheStartedClientsOwnHandle_AndNeverClosesIt()
        {
            FakeKernel k = Scene();
            StartedClient client = k.Started(Target);

            Releaser(k, new FakeTime(), new List<string>()).Release(client);

            Assert.IsTrue(k.ProcessHandlesUsed.All(h => h == client.Handle || h == k.CurrentProcess), "no other process handle is ever used");
            Assert.IsTrue(k.ProcessCalls.All(c => c.Key == Target || (c.Key == Self && c.Value == "table")), "the launcher's own table is only read");
            Assert.IsTrue(k.Closes.All(c => c.Item1 == Target));
            Assert.AreEqual(2, k.Tables[Bot].Count, "the bot is untouched");
            Assert.AreEqual(0, k.ClosedProcessHandles.Count, "the CreateProcess handle belongs to the StartedClient, not to the releaser");
        }

        // Mutation checked: adding back a [DllImport] of OpenProcess to NativeClientHandleApi (or an
        // OpenProcess member to the seam) fails this test.
        [TestMethod]
        public void ReleasePath_HasNoWayToOpenAProcessById()
        {
            Assert.IsFalse(typeof(IClientHandleApi).GetMethods().Any(m => m.Name.IndexOf("OpenProcess", StringComparison.OrdinalIgnoreCase) >= 0));
            foreach (MethodInfo m in typeof(NativeClientHandleApi).GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            {
                DllImportAttribute import = m.GetCustomAttribute<DllImportAttribute>();
                if (import != null)
                {
                    string entry = import.EntryPoint ?? m.Name;
                    Assert.IsFalse(entry.StartsWith("OpenProcess", StringComparison.OrdinalIgnoreCase) || entry.StartsWith("NtOpenProcess", StringComparison.OrdinalIgnoreCase), "imports " + entry);
                }
            }
        }

        // Mutation checked: closing the launcher's own semaphore handle after CloseInProcess (instead
        // of before it) leaves 1 launcher handle open at the close and fails this test.
        [TestMethod]
        public void Release_ClosesItsOwnHandlesBeforeClosingTheClientsHandle()
        {
            FakeKernel k = Scene();

            Releaser(k, new FakeTime(), new List<string>()).Release(k.Started(Target));

            Assert.AreEqual(1, k.Closes.Count);
            Assert.AreEqual(0, k.Closes[0].Item3, "no launcher handle (own or duplicate) to the lock may be open when the client's is closed, or the object outlives it");
        }

        // Mutation checked: disabling the type filter (if (false) continue) duplicates the event and
        // the mutant too and fails this test.
        [TestMethod]
        public void Release_DuplicatesOnlyHandlesOfTheSemaphoreType()
        {
            FakeKernel k = Scene();

            Releaser(k, new FakeTime(), new List<string>()).Release(k.Started(Target));

            CollectionAssert.AreEqual(new[] { 0x20, 0x30 }, k.Duplicated.Select(d => d.Value.ToInt32()).ToArray(), "only the two semaphores are duplicated");
            Assert.IsTrue(k.Duplicated.All(d => d.Key == Target));
        }

        // Mutation checked: deleting the timeout return makes the releaser poll forever (the fake
        // clock throws after 1000 sleeps) and fails this test.
        [TestMethod]
        public void Release_GivesUpAfterTheTimeout_WhenTheLockNeverAppears()
        {
            FakeKernel k = Scene();
            k.LockAppearsAfter = int.MaxValue;
            var time = new FakeTime();
            var log = new List<string>();

            ClientLockOutcome outcome = Releaser(k, time, log).Release(k.Started(Target), TimeSpan.FromSeconds(20));

            Assert.AreEqual(ClientLockOutcome.NotFound, outcome);
            Assert.IsTrue(time.Now >= TimeSpan.FromSeconds(20) && time.Now < TimeSpan.FromSeconds(21), "waited the timeout, not longer: " + time.Now);
            Assert.AreEqual(0, k.Closes.Count);
            StringAssert.StartsWith(log.Single(), "client-lock pid=4242 not found after=20.");
        }

        [TestMethod]
        public void Release_WaitsForTheClientToCreateItsLock()
        {
            FakeKernel k = Scene();
            k.LockAppearsAfter = 3;
            var time = new FakeTime();

            Assert.AreEqual(ClientLockOutcome.Released, Releaser(k, time, new List<string>()).Release(k.Started(Target)));
            Assert.AreEqual(3, time.Sleeps);
            Assert.AreEqual(new IntPtr(0x30), k.Closes.Single().Item2);
        }

        // Mutation checked: removing the Supported check makes the releaser read the client's table and fail this test.
        [TestMethod]
        public void Release_OnOlderWindows_TouchesNothing()
        {
            FakeKernel k = Scene();
            k.Supported = false;
            var log = new List<string>();

            Assert.AreEqual(ClientLockOutcome.Unsupported, Releaser(k, new FakeTime(), log).Release(k.Started(Target)));
            Assert.AreEqual(0, k.ProcessCalls.Count);
            StringAssert.Contains(log.Single(), "unsupported");
        }

        [TestMethod]
        public void Release_StopsWhenTheClientExits()
        {
            FakeKernel k = Scene();
            k.LockAppearsAfter = int.MaxValue;
            k.Exited.Add(Target);
            var time = new FakeTime();

            Assert.AreEqual(ClientLockOutcome.ClientExited, Releaser(k, time, new List<string>()).Release(k.Started(Target)));
            Assert.AreEqual(0, time.Sleeps);
            Assert.AreEqual(0, k.Closes.Count);
        }

        [TestMethod]
        public void Release_WhenTheLockIsHeldOnlyByAnotherProcess_ClosesNothing()
        {
            var k = new FakeKernel();
            k.Add(Target, 0x10, 500, EventType);
            k.Add(Bot, 0x30, LockObject, SemaphoreType);

            Assert.AreEqual(ClientLockOutcome.NotFound, Releaser(k, new FakeTime(), new List<string>()).Release(k.Started(Target), TimeSpan.FromSeconds(1)));
            Assert.AreEqual(0, k.Closes.Count);
            Assert.AreEqual(1, k.Tables[Bot].Count);
            Assert.AreEqual(0, k.Tables[Self].Count, "each look releases the launcher's own handles");
        }

        // Mutation checked: deleting the second OpenSemaphore check after the close reports Released
        // and fails this test.
        [TestMethod]
        public void Release_WhenAnotherProcessStillHoldsTheLock_ReportsHeldElsewhere()
        {
            FakeKernel k = Scene();
            k.Add(Bot, 0x50, LockObject, SemaphoreType);
            var log = new List<string>();

            ClientLockOutcome outcome = Releaser(k, new FakeTime(), log).Release(k.Started(Target));

            Assert.AreEqual(ClientLockOutcome.HeldElsewhere, outcome);
            Assert.AreEqual(new IntPtr(0x30), k.Closes.Single().Item2, "the client's own handle is still closed");
            Assert.AreEqual(3, k.Tables[Bot].Count, "the bot keeps its lock handle");
            Assert.AreEqual(0, k.Tables[Self].Count, "the re-check handle is closed too");
            StringAssert.Contains(log.Single(), "held elsewhere");
        }

        // Mutation checked: dropping the HasExited check in the handle-table failure branch reports
        // Failed and fails this test.
        [TestMethod]
        public void Release_WhenTheTableReadFailsBecauseTheClientExited_ReportsClientExited()
        {
            FakeKernel k = Scene();
            k.TableFails.Add(Target);
            k.Exited.Add(Target);

            Assert.AreEqual(ClientLockOutcome.ClientExited, Releaser(k, new FakeTime(), new List<string>()).Release(k.Started(Target)));
            Assert.AreEqual(0, k.Closes.Count);
        }

        [TestMethod]
        public void Release_WhenTheTableReadFails_ReportsFailedWithTheStatus()
        {
            FakeKernel k = Scene();
            k.TableFails.Add(Target);
            var log = new List<string>();

            Assert.AreEqual(ClientLockOutcome.Failed, Releaser(k, new FakeTime(), log).Release(k.Started(Target)));
            Assert.AreEqual("client-lock pid=4242 failed step=handle-table error=0xc0000022", log.Single());
        }

        // Mutation checked: logging _api.LastError instead of the sentinel logs error=0x00000000 and fails this test.
        [TestMethod]
        public void Release_WhenItsOwnHandleIsNotListed_LogsTheSentinel()
        {
            FakeKernel k = Scene();
            k.HideOwnSemaphores = true;
            var log = new List<string>();

            Assert.AreEqual(ClientLockOutcome.Failed, Releaser(k, new FakeTime(), log).Release(k.Started(Target)));
            Assert.AreEqual("client-lock pid=4242 failed step=own-type error=0xe0000001", log.Single());
            Assert.AreEqual(0, k.Closes.Count);
        }

        // Mutation checked: ignoring the token polls on to the timeout (NotFound) and fails this test.
        [TestMethod]
        public void Release_CancelledWhileWaiting_StopsWaiting()
        {
            FakeKernel k = Scene();
            k.LockAppearsAfter = int.MaxValue;
            var cancel = new CancellationTokenSource();
            var time = new FakeTime { OnSleep = n => { if (n == 2) { cancel.Cancel(); } } };

            Assert.AreEqual(ClientLockOutcome.Cancelled, Releaser(k, time, new List<string>()).Release(k.Started(Target), TimeSpan.FromSeconds(20), cancel.Token));
            Assert.AreEqual(2, time.Sleeps, "stops at the next look after the cancel");
        }

        // Mutation checked: checking the token before the first look returns Cancelled and fails this test.
        [TestMethod]
        public void Release_AlreadyCancelled_StillClosesALockItFinds()
        {
            FakeKernel k = Scene();
            var cancel = new CancellationTokenSource();
            cancel.Cancel();

            Assert.AreEqual(ClientLockOutcome.Released, Releaser(k, new FakeTime(), new List<string>()).Release(k.Started(Target), null, cancel.Token));
            Assert.AreEqual(new IntPtr(0x30), k.Closes.Single().Item2);
        }
    }
}
