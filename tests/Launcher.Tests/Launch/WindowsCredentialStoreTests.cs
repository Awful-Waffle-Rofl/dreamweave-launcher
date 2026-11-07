using System;
using System.Linq;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Credentials;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Launch
{
    /// <summary>
    /// Integration tests against the real Windows Credential Manager, isolated under a unique target
    /// prefix and cleaned up afterwards. If the API cannot be used (for example a locked-down CI
    /// runner) every test reports Inconclusive with the reason; it never passes silently.
    /// </summary>
    [TestClass]
    public class WindowsCredentialStoreTests
    {
        private string prefix;
        private WindowsCredentialStore store;

        [TestInitialize]
        public void Setup()
        {
            prefix = "DreamWeaveTest-" + Guid.NewGuid().ToString("N") + "/";
            store = new WindowsCredentialStore(prefix);
            try
            {
                store.Save("probe", "probe");
                store.Delete("probe");
            }
            catch (LauncherException ex) when (ex.Code == LauncherError.CredentialStoreUnavailable)
            {
                Assert.Inconclusive("Windows Credential Manager is unavailable on this machine: " + ex.Detail);
            }
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (store == null)
            {
                return;
            }

            try
            {
                foreach (string a in store.ListAccounts())
                {
                    store.Delete(a);
                }
            }
            catch (LauncherException)
            {
            }
        }

        [TestMethod]
        public void RoundTrip()
        {
            store.Save("alice", "pa ss!x");
            Assert.AreEqual("pa ss!x", store.Get("alice"));
        }

        [TestMethod]
        public void Get_Missing_ReturnsNull()
        {
            Assert.IsNull(store.Get("nobody"));
        }

        [TestMethod]
        public void Save_Overwrites()
        {
            store.Save("alice", "one");
            store.Save("alice", "two");
            Assert.AreEqual("two", store.Get("alice"));
            Assert.AreEqual(1, store.ListAccounts().Count);
        }

        [TestMethod]
        public void List_ReturnsSortedAccountsWithoutPrefix()
        {
            store.Save("bob", "b");
            store.Save("alice", "a");
            CollectionAssert.AreEqual(new[] { "alice", "bob" }, store.ListAccounts().ToArray());
        }

        [TestMethod]
        public void List_Empty()
        {
            Assert.AreEqual(0, store.ListAccounts().Count);
        }

        [TestMethod]
        public void Delete_RemovesAndReportsPresence()
        {
            store.Save("alice", "a");
            Assert.IsTrue(store.Delete("alice"));
            Assert.IsNull(store.Get("alice"));
            Assert.IsFalse(store.Delete("alice"));
        }

        [TestMethod]
        public void UnicodePassword_RoundTrips()
        {
            string pw = "p" + (char)0xE4 + "ss" + (char)0x4E2D + (char)0x6587 + char.ConvertFromUtf32(0x1F600) + "w" + (char)0xF6 + "rd";
            store.Save("alice", pw);
            Assert.AreEqual(pw, store.Get("alice"));
        }

        [TestMethod]
        public void LongPassword_RoundTrips()
        {
            string pw = new string('x', 1000);
            store.Save("alice", pw);
            Assert.AreEqual(pw, store.Get("alice"));
        }

        [TestMethod]
        public void OverlongPassword_IsRejectedClearly()
        {
            LauncherException ex = Assert.ThrowsExactly<LauncherException>(() => store.Save("alice", new string('x', 2000)));
            Assert.AreEqual(LauncherError.CredentialStoreUnavailable, ex.Code);
            Assert.IsNull(store.Get("alice"));
        }

        [TestMethod]
        public void EmptyPassword_RoundTrips()
        {
            store.Save("alice", string.Empty);
            Assert.AreEqual(string.Empty, store.Get("alice"));
        }

        [TestMethod]
        public void CaseVariants_AreOneEntry_AndDeleteClearsAll()
        {
            store.Save("Alice", "first");
            store.Save("alice", "second");
            Assert.AreEqual("second", store.Get("Alice"));
            Assert.AreEqual(1, store.ListAccounts().Count);
            Assert.IsTrue(store.Delete("alice"));
            Assert.IsNull(store.Get("Alice"));
            Assert.IsNull(store.Get("alice"));
            Assert.AreEqual(0, store.ListAccounts().Count);
        }
    }
}