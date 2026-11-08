using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace DreamWeave.Launcher.Core.Launch
{
    /// <summary>What <see cref="ClientLockReleaser.Release"/> did.</summary>
    public enum ClientLockOutcome
    {
        /// <summary>The started client's lock handle was closed: another client can start.</summary>
        Released,

        /// <summary>The client did not create its lock within the timeout.</summary>
        NotFound,

        /// <summary>The client exited before its lock was found.</summary>
        ClientExited,

        /// <summary>This Windows lacks an API the release needs (Windows 10 or later is required).</summary>
        Unsupported,

        /// <summary>A Windows call failed; the client keeps its lock.</summary>
        Failed,
    }

    /// <summary>One handle in a process's own handle table (no object names, no other processes).</summary>
    public struct ClientHandleEntry
    {
        /// <summary>Creates an entry.</summary>
        public ClientHandleEntry(IntPtr value, int typeIndex)
        {
            Value = value;
            TypeIndex = typeIndex;
        }

        /// <summary>The handle value inside its process.</summary>
        public IntPtr Value { get; }

        /// <summary>The kernel object type index (same for every handle of one object type).</summary>
        public int TypeIndex { get; }
    }

    /// <summary>
    /// The Windows calls <see cref="ClientLockReleaser"/> makes; the seam the tests fake. Every process
    /// handle it returns was opened for one process id passed to <see cref="OpenProcess"/>.
    /// </summary>
    public interface IClientHandleApi
    {
        /// <summary>True when every call below is available (Windows 10 or later).</summary>
        bool Supported { get; }

        /// <summary>A pseudo-handle for the launcher's own process.</summary>
        IntPtr CurrentProcess { get; }

        /// <summary>Opens <paramref name="pid"/> for handle duplication and query only (no write, no VM, no terminate). Zero on failure.</summary>
        IntPtr OpenProcess(int pid);

        /// <summary>True when the process behind <paramref name="process"/> has exited.</summary>
        bool HasExited(IntPtr process);

        /// <summary>That one process's handle table (ProcessHandleInformation), or null on failure.</summary>
        IReadOnlyList<ClientHandleEntry> HandleTable(IntPtr process);

        /// <summary>Opens the launcher's own handle to the named semaphore; zero when none exists.</summary>
        IntPtr OpenSemaphore(string name);

        /// <summary>Duplicates <paramref name="value"/> from <paramref name="process"/> into the launcher. Zero on failure.</summary>
        IntPtr DuplicateIntoSelf(IntPtr process, IntPtr value);

        /// <summary>True when both of the launcher's handles refer to the same kernel object.</summary>
        bool SameObject(IntPtr first, IntPtr second);

        /// <summary>Closes the handle <paramref name="value"/> inside <paramref name="process"/> (DUPLICATE_CLOSE_SOURCE).</summary>
        bool CloseInProcess(IntPtr process, IntPtr value);

        /// <summary>Closes one of the launcher's own handles.</summary>
        void CloseOwn(IntPtr handle);

        /// <summary>The last Windows error code of the calling thread.</summary>
        int LastError { get; }
    }

    /// <summary>
    /// Lets a second acclient.exe start. The client refuses to run when the named semaphore
    /// <see cref="LockName"/> already exists (it calls CreateSemaphoreA with that name and checks for
    /// ERROR_ALREADY_EXISTS), so after the launcher starts a client it closes that one handle inside
    /// that one process. Owner ruling, a narrow exemption: no client file is modified, no code is
    /// injected, only a process the launcher itself just started is touched, and nothing but the lock
    /// handle is closed. It reads only the target's own handle table, matches by object identity
    /// against the launcher's own handle to the named semaphore, and never queries object names.
    /// </summary>
    public sealed class ClientLockReleaser
    {
        /// <summary>The client's single-instance semaphore (acclient.exe, CreateSemaphoreA near VA 0x4122ae).</summary>
        public const string LockName = "Empyrean Client";

        /// <summary>How long to wait for a started client to create its lock.</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

        /// <summary>Pause between two looks for the lock.</summary>
        public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

        private readonly IClientHandleApi _api;
        private readonly Action<string> _log;
        private readonly Action<TimeSpan> _sleep;

        /// <summary>Creates a releaser over the real Windows calls.</summary>
        public ClientLockReleaser(Action<string> log)
            : this(new NativeClientHandleApi(), log, null, null)
        {
        }

        /// <summary>Creates a releaser over the given seams (tests). Null clock / sleep use the real ones.</summary>
        public ClientLockReleaser(IClientHandleApi api, Action<string> log, Func<Func<TimeSpan>> clock, Action<TimeSpan> sleep)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _log = log ?? (line => { });
            Clock = clock ?? (() =>
            {
                Stopwatch watch = Stopwatch.StartNew();
                return () => watch.Elapsed;
            });
            _sleep = sleep ?? (t => Thread.Sleep(t));
        }

        private Func<Func<TimeSpan>> Clock { get; }

        /// <summary>
        /// Waits (up to <paramref name="timeout"/>, default <see cref="DefaultTimeout"/>) for the client
        /// <paramref name="pid"/> to create its lock, then closes that one handle in that process. Never
        /// throws; every outcome is written to the log.
        /// </summary>
        public ClientLockOutcome Release(int pid, TimeSpan? timeout = null)
        {
            ClientLockOutcome outcome;
            try
            {
                outcome = ReleaseCore(pid, timeout ?? DefaultTimeout);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                Note(pid, "unsupported (" + ex.GetType().Name + ")");
                outcome = ClientLockOutcome.Unsupported;
            }
            catch (Exception ex)
            {
                // The client is already running; a failed release must never turn into a failed launch.
                Note(pid, "failed step=exception type=" + ex.GetType().FullName);
                outcome = ClientLockOutcome.Failed;
            }

            return outcome;
        }

        private ClientLockOutcome ReleaseCore(int pid, TimeSpan timeout)
        {
            if (!_api.Supported)
            {
                Note(pid, "unsupported (needs Windows 10 or later)");
                return ClientLockOutcome.Unsupported;
            }

            Func<TimeSpan> elapsed = Clock();
            IntPtr process = _api.OpenProcess(pid);
            if (process == IntPtr.Zero)
            {
                Note(pid, "failed step=open-process error=" + Code(_api.LastError));
                return ClientLockOutcome.Failed;
            }

            try
            {
                while (true)
                {
                    IntPtr own = _api.OpenSemaphore(LockName);
                    if (own != IntPtr.Zero)
                    {
                        IntPtr found;
                        string failure;
                        try
                        {
                            found = FindInTarget(process, own, out failure);
                        }
                        finally
                        {
                            // Before the close below: while the launcher holds its own handle the object
                            // stays alive, and the next client would still find it.
                            _api.CloseOwn(own);
                        }

                        if (failure != null)
                        {
                            Note(pid, "failed step=" + failure + " error=" + Code(_api.LastError));
                            return ClientLockOutcome.Failed;
                        }

                        if (found != IntPtr.Zero)
                        {
                            if (!_api.CloseInProcess(process, found))
                            {
                                Note(pid, "failed step=close error=" + Code(_api.LastError));
                                return ClientLockOutcome.Failed;
                            }

                            Note(pid, "released handle=0x" + found.ToInt64().ToString("x", CultureInfo.InvariantCulture) + " after=" + Seconds(elapsed()));
                            return ClientLockOutcome.Released;
                        }
                    }

                    if (_api.HasExited(process))
                    {
                        Note(pid, "client exited before its lock was found after=" + Seconds(elapsed()));
                        return ClientLockOutcome.ClientExited;
                    }

                    if (elapsed() >= timeout)
                    {
                        Note(pid, "not found after=" + Seconds(elapsed()));
                        return ClientLockOutcome.NotFound;
                    }

                    _sleep(PollInterval);
                }
            }
            finally
            {
                _api.CloseOwn(process);
            }
        }

        /// <summary>
        /// The target's handle to the same object as <paramref name="own"/>, or zero when it holds none
        /// (yet). Only handles of the semaphore's own object type are duplicated, and each duplicate is
        /// closed before returning. <paramref name="failure"/> names the failed step, or is null.
        /// </summary>
        private IntPtr FindInTarget(IntPtr process, IntPtr own, out string failure)
        {
            failure = null;
            int? semaphoreType = TypeOf(own);
            if (semaphoreType == null)
            {
                failure = "own-type";
                return IntPtr.Zero;
            }

            IReadOnlyList<ClientHandleEntry> table = _api.HandleTable(process);
            if (table == null)
            {
                failure = "handle-table";
                return IntPtr.Zero;
            }

            foreach (ClientHandleEntry entry in table)
            {
                if (entry.TypeIndex != semaphoreType.Value)
                {
                    continue;
                }

                IntPtr copy = _api.DuplicateIntoSelf(process, entry.Value);
                if (copy == IntPtr.Zero)
                {
                    continue;
                }

                bool same;
                try
                {
                    same = _api.SameObject(own, copy);
                }
                finally
                {
                    _api.CloseOwn(copy);
                }

                if (same)
                {
                    return entry.Value;
                }
            }

            return IntPtr.Zero;
        }

        private int? TypeOf(IntPtr own)
        {
            IReadOnlyList<ClientHandleEntry> mine = _api.HandleTable(_api.CurrentProcess);
            if (mine == null)
            {
                return null;
            }

            foreach (ClientHandleEntry entry in mine)
            {
                if (entry.Value == own)
                {
                    return entry.TypeIndex;
                }
            }

            return null;
        }

        private void Note(int pid, string text)
        {
            try
            {
                _log("client-lock pid=" + pid.ToString(CultureInfo.InvariantCulture) + " " + text);
            }
            catch (Exception)
            {
                // Logging never fails a launch.
            }
        }

        private static string Code(int error)
        {
            // Win32 codes and NTSTATUS values both read best in hex (0xC0000022 = access denied).
            return "0x" + error.ToString("x8", CultureInfo.InvariantCulture);
        }

        private static string Seconds(TimeSpan t)
        {
            return t.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s";
        }
    }
}
