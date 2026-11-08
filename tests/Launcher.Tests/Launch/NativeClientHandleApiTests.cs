using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DreamWeave.Launcher.Core.Launch;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Launch
{
    /// <summary>
    /// The real P/Invoke surface. Every test here reads ONLY the test process's own handle table
    /// (through the current-process pseudo-handle); no other process is opened.
    /// </summary>
    [TestClass]
    public class NativeClientHandleApiTests
    {
        private const long Junk = 0x1111;
        private const int Access = 0x2222;
        private const int Attributes = 0x3333;
        private const int Reserved = 0x4444;

        /// <summary>A PROCESS_HANDLE_SNAPSHOT_INFORMATION for this process's pointer size, every field filled.</summary>
        private static IntPtr Snapshot(long count, IList<KeyValuePair<long, int>> entries, out int size)
        {
            int ptr = IntPtr.Size;
            int entrySize = (3 * ptr) + 16;
            size = (2 * ptr) + (entries.Count * entrySize);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            WritePtr(buffer, 0, count);
            WritePtr(buffer, ptr, 0x5555);
            for (int i = 0; i < entries.Count; i++)
            {
                int at = (2 * ptr) + (i * entrySize);
                WritePtr(buffer, at, entries[i].Key);
                WritePtr(buffer, at + ptr, Junk);
                WritePtr(buffer, at + (2 * ptr), Junk + 1);
                Marshal.WriteInt32(buffer, at + (3 * ptr), Access);
                Marshal.WriteInt32(buffer, at + (3 * ptr) + 4, entries[i].Value);
                Marshal.WriteInt32(buffer, at + (3 * ptr) + 8, Attributes);
                Marshal.WriteInt32(buffer, at + (3 * ptr) + 12, Reserved);
            }

            return buffer;
        }

        private static void WritePtr(IntPtr buffer, int offset, long value)
        {
            if (IntPtr.Size == 8)
            {
                Marshal.WriteInt64(buffer, offset, value);
            }
            else
            {
                Marshal.WriteInt32(buffer, offset, (int)value);
            }
        }

        private static readonly KeyValuePair<long, int>[] ThreeEntries =
        {
            new KeyValuePair<long, int>(0x04, 7),
            new KeyValuePair<long, int>(0x2ac, 40),
            new KeyValuePair<long, int>(0x7fc, 19),
        };

        // Mutations checked: entry size 3*ptr+12, type index read at +0 / +8 instead of +4, and the
        // header read as one pointer instead of two each fail this test.
        [TestMethod]
        public void Parse_ReadsEveryValueAndTypeIndex_AtThisPointerSize()
        {
            IntPtr buffer = Snapshot(3, ThreeEntries, out int size);
            try
            {
                IReadOnlyList<ClientHandleEntry> entries = NativeClientHandleApi.Parse(buffer, size);
                CollectionAssert.AreEqual(new long[] { 0x04, 0x2ac, 0x7fc }, entries.Select(e => e.Value.ToInt64()).ToArray());
                CollectionAssert.AreEqual(new[] { 7, 40, 19 }, entries.Select(e => e.TypeIndex).ToArray());
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        // Mutation checked: removing the count clamp reads past the buffer (an access violation or
        // garbage entries) and fails this test.
        [TestMethod]
        public void Parse_ClampsAHeaderCountLargerThanTheBuffer()
        {
            IntPtr buffer = Snapshot(100000, ThreeEntries, out int size);
            try
            {
                IReadOnlyList<ClientHandleEntry> entries = NativeClientHandleApi.Parse(buffer, size);
                Assert.AreEqual(3, entries.Count);
                Assert.AreEqual(0x7fc, entries[2].Value.ToInt64());
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [TestMethod]
        public void Parse_ZeroCount_IsEmpty()
        {
            IntPtr buffer = Snapshot(0, ThreeEntries, out int size);
            try
            {
                Assert.AreEqual(0, NativeClientHandleApi.Parse(buffer, size).Count);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static void AssertListsOwnSemaphores(NativeClientHandleApi api)
        {
            Assert.IsTrue(api.Supported, "Windows 10 or later is needed to run this test");
            using (var first = new Semaphore(0, 1))
            using (var second = new Semaphore(0, 1))
            using (var other = new ManualResetEvent(false))
            {
                IntPtr a = first.SafeWaitHandle.DangerousGetHandle();
                IntPtr b = second.SafeWaitHandle.DangerousGetHandle();
                IntPtr e = other.SafeWaitHandle.DangerousGetHandle();

                IReadOnlyList<ClientHandleEntry> table = api.HandleTable(api.CurrentProcess);

                Assert.IsNotNull(table, "error 0x" + api.LastError.ToString("x8"));
                ClientHandleEntry ea = table.Single(x => x.Value == a);
                ClientHandleEntry eb = table.Single(x => x.Value == b);
                ClientHandleEntry ee = table.Single(x => x.Value == e);
                Assert.AreEqual(ea.TypeIndex, eb.TypeIndex, "two semaphores share one type index");
                Assert.AreNotEqual(ea.TypeIndex, ee.TypeIndex, "an event has another");
            }
        }

        // Mutation checked: a wrong type index offset fails this test (the two semaphores and the
        // event read the same field, so they no longer differ), as does a wrong entry size.
        [TestMethod]
        public void HandleTable_OfThisProcess_ListsItsSemaphoresWithOneTypeIndex()
        {
            AssertListsOwnSemaphores(new NativeClientHandleApi());
        }

        // Mutation checked: returning null on STATUS_INFO_LENGTH_MISMATCH instead of growing fails this test.
        [TestMethod]
        public void HandleTable_GrowsFromATinyFirstBuffer()
        {
            AssertListsOwnSemaphores(new NativeClientHandleApi(16));
        }

        [TestMethod]
        public void SameObject_MatchesADuplicateOfTheSameSemaphore_AndNotAnother()
        {
            var api = new NativeClientHandleApi();
            using (var first = new Semaphore(0, 1))
            using (var second = new Semaphore(0, 1))
            {
                IntPtr a = first.SafeWaitHandle.DangerousGetHandle();
                IntPtr copy = api.DuplicateIntoSelf(api.CurrentProcess, a);
                Assert.AreNotEqual(IntPtr.Zero, copy);
                try
                {
                    Assert.IsTrue(api.SameObject(a, copy));
                    Assert.IsFalse(api.SameObject(second.SafeWaitHandle.DangerousGetHandle(), copy));
                }
                finally
                {
                    api.CloseOwn(copy);
                }
            }
        }

        [TestMethod]
        public void OpenSemaphore_FindsANamedSemaphore_AndNotAMissingOne()
        {
            var api = new NativeClientHandleApi();
            string name = "DreamWeaveLauncherTest-" + Guid.NewGuid().ToString("N");
            Assert.AreEqual(IntPtr.Zero, api.OpenSemaphore(name));
            using (new Semaphore(0, 1, name))
            {
                IntPtr h = api.OpenSemaphore(name);
                Assert.AreNotEqual(IntPtr.Zero, h);
                api.CloseOwn(h);
            }
        }
    }
}
