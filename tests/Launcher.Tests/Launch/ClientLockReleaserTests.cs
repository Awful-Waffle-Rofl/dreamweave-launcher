using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>A fake kernel: per-process handle tables of (value, object, type), plus named objects.</summary>
        private sealed class FakeKernel : IClientHandleApi
        {
            private readonly Dictionary<IntPtr, int> _processHandles = new Dictionary<IntPtr, int>();
            private int _nextOwn = 0x5000;
            private int _nextProcess = 0x9000;

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

            public int LastError => 0;

            /// <summary>The lock exists once this many OpenSemaphore calls have missed (int.MaxValue: never).</summary>
            public int LockAppearsAfter { get; set; }

            public HashSet<int> Exited { get; } = new HashSet<int>();

            public List<int> OpenedPids { get; } = new List<int>();

            /// <summary>Every (pid, call) made through a process handle.</summary>
            public List<KeyValuePair<int, string>> ProcessCalls { get; } = new List<KeyValuePair<int, string>>();

            /// <summary>Every close-in-process: pid, value, and how many launcher handles to that object were still open.</summary>
            public List<Tuple<int, IntPtr, int>> Closes { get; } = new List<Tuple<int, IntPtr, int>>();

            public List<IntPtr> OpenProcessHandles => _processHandles.Keys.Where(k => k != CurrentProcess).ToList();

            private int _semaphoreLookups;

            public void Add(int pid, int value, int obj, int type)
            {
                if (!Tables.ContainsKey(pid))
                {
                    Tables[pid] = new List<Row>();
                }

                Tables[pid].Add(new Row { Value = new IntPtr(value), Object = obj, Type = type });
            }

            public int OwnHandlesTo(int obj) => Tables[Self].Count(r => r.Object == obj);

            public IntPtr OpenProcess(int pid)
            {
                OpenedPids.Add(pid);
                var h = new IntPtr(_nextProcess++);
                _processHandles[h] = pid;
                return h;
            }

            public bool HasExited(IntPtr process)
            {
                int pid = Pid(process, "exited");
                return Exited.Contains(pid);
            }

            public IReadOnlyList<ClientHandleEntry> HandleTable(IntPtr process)
            {
                int pid = Pid(process, "table");
                return Tables[pid].Select(r => new ClientHandleEntry(r.Value, r.Type)).ToList();
            }

            public IntPtr OpenSemaphore(string name)
            {
                if (name != ClientLockReleaser.LockName || _semaphoreLookups++ < LockAppearsAfter)
                {
                    return IntPtr.Zero;
                }

                return AddOwn(LockObject, SemaphoreType);
            }

            public IntPtr DuplicateIntoSelf(IntPtr process, IntPtr value)
            {
                int pid = Pid(process, "duplicate");
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
                if (_processHandles.Remove(handle))
                {
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

            public Func<Func<TimeSpan>> Clock => () =>
            {
                TimeSpan start = Now;
                return () => Now - start;
            };

            public void Sleep(TimeSpan t)
            {
                if (++Sleeps > 1000)
                {
                    throw new InvalidOperationException("the releaser never gave up");
                }

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

            // A bot client the launcher did not start, holding the same lock object.
            k.Add(Bot, 0x30, LockObject, SemaphoreType);
            return k;
        }

        private static ClientLockReleaser Releaser(FakeKernel k, FakeTime time, List<string> log)
        {
            return new ClientLockReleaser(k, log.Add, time.Clock, time.Sleep);
        }

        // Mutation checked: removing the SameObject match (closing the first semaphore-typed handle)
        // closes 0x20 instead of 0x30 and fails this test.
        [TestMethod]
        public void Release_ClosesOnlyTheLockHandle_InTheStartedProcess()
        {
            FakeKernel k = Scene();
            var log = new List<string>();

            ClientLockOutcome outcome = Releaser(k, new FakeTime(), log).Release(Target);

            Assert.AreEqual(ClientLockOutcome.Released, outcome);
            Assert.AreEqual(1, k.Closes.Count, "exactly one handle is closed");
            Assert.AreEqual(Target, k.Closes[0].Item1);
            Assert.AreEqual(new IntPtr(0x30), k.Closes[0].Item2);
            CollectionAssert.AreEqual(new[] { 0x10, 0x20, 0x40 }, k.Tables[Target].Select(r => r.Value.ToInt32()).ToArray(), "every other handle of the client is left alone");
            Assert.AreEqual(0, k.Tables[Self].Count, "the launcher keeps no handle of its own");
            Assert.AreEqual(0, k.OpenProcessHandles.Count, "the process handle is closed");
            Assert.AreEqual(1, log.Count);
            StringAssert.StartsWith(log[0], "client-lock pid=4242 released handle=0x30");
        }

        // Mutation checked: CloseInProcess(_api.CurrentProcess, found) instead of the target's handle
        // closes in pid 1 and fails this test (and the one above).
        [TestMethod]
        public void Release_NeverTouchesAProcessItDidNotStart()
        {
            FakeKernel k = Scene();

            Releaser(k, new FakeTime(), new List<string>()).Release(Target);

            CollectionAssert.AreEqual(new[] { Target }, k.OpenedPids, "only the started client is opened");
            Assert.IsTrue(k.Closes.All(c => c.Item1 == Target), "nothing is closed anywhere else");
            Assert.IsTrue(k.ProcessCalls.All(c => c.Key == Target || (c.Key == Self && c.Value == "table")), "the launcher's own table is only read");
            Assert.AreEqual(1, k.Tables[Bot].Count, "the bot's lock handle is untouched");
        }

        // Mutation checked: closing the launcher's own semaphore handle after CloseInProcess (instead
        // of before it) leaves 1 launcher handle open at the close and fails this test.
        [TestMethod]
        public void Release_ClosesItsOwnHandlesBeforeClosingTheClientsHandle()
        {
            FakeKernel k = Scene();

            Releaser(k, new FakeTime(), new List<string>()).Release(Target);

            Assert.AreEqual(1, k.Closes.Count);
            Assert.AreEqual(0, k.Closes[0].Item3, "no launcher handle (own or duplicate) to the lock may be open when the client's is closed, or the object outlives it");
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

            ClientLockOutcome outcome = Releaser(k, time, log).Release(Target, TimeSpan.FromSeconds(20));

            Assert.AreEqual(ClientLockOutcome.NotFound, outcome);
            Assert.IsTrue(time.Now >= TimeSpan.FromSeconds(20) && time.Now < TimeSpan.FromSeconds(21), "waited the timeout, not longer: " + time.Now);
            Assert.AreEqual(0, k.Closes.Count);
            Assert.AreEqual(0, k.OpenProcessHandles.Count);
            StringAssert.StartsWith(log.Single(), "client-lock pid=4242 not found after=20.");
        }

        [TestMethod]
        public void Release_WaitsForTheClientToCreateItsLock()
        {
            FakeKernel k = Scene();
            k.LockAppearsAfter = 3;
            var time = new FakeTime();

            Assert.AreEqual(ClientLockOutcome.Released, Releaser(k, time, new List<string>()).Release(Target));
            Assert.AreEqual(3, time.Sleeps);
            Assert.AreEqual(new IntPtr(0x30), k.Closes.Single().Item2);
        }

        // Mutation checked: removing the Supported check makes the releaser open the process and fail this test.
        [TestMethod]
        public void Release_OnOlderWindows_TouchesNothing()
        {
            FakeKernel k = Scene();
            k.Supported = false;
            var log = new List<string>();

            Assert.AreEqual(ClientLockOutcome.Unsupported, Releaser(k, new FakeTime(), log).Release(Target));
            Assert.AreEqual(0, k.OpenedPids.Count);
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

            Assert.AreEqual(ClientLockOutcome.ClientExited, Releaser(k, time, new List<string>()).Release(Target));
            Assert.AreEqual(0, time.Sleeps);
            Assert.AreEqual(0, k.Closes.Count);
        }

        [TestMethod]
        public void Release_WhenTheLockIsHeldOnlyByAnotherProcess_ClosesNothing()
        {
            var k = new FakeKernel();
            k.Add(Target, 0x10, 500, EventType);
            k.Add(Bot, 0x30, LockObject, SemaphoreType);
            var time = new FakeTime();

            Assert.AreEqual(ClientLockOutcome.NotFound, Releaser(k, time, new List<string>()).Release(Target, TimeSpan.FromSeconds(1)));
            Assert.AreEqual(0, k.Closes.Count);
            Assert.AreEqual(1, k.Tables[Bot].Count);
            Assert.AreEqual(0, k.Tables[Self].Count, "each look releases the launcher's own handles");
        }
    }
}
