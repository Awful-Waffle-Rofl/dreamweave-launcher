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
        /// <summary>The started client's lock handle was closed and the lock is gone: another client can start.</summary>
        Released,

        /// <summary>The client did not create its lock within the timeout.</summary>
        NotFound,

        /// <summary>The client exited before its lock was released.</summary>
        ClientExited,

        /// <summary>This Windows lacks an API the release needs (Windows 10 or later is required).</summary>
        Unsupported,

        /// <summary>A Windows call failed; the client keeps its lock.</summary>
        Failed,

        /// <summary>The client's handle was closed, but another process still holds the lock.</summary>
        HeldElsewhere,

        /// <summary>The wait was cancelled (the launcher is closing) before the lock was found.</summary>
        Cancelled,
    }

    /// <summary>
    /// A client the launcher has just started: its id and the process handle from CreateProcess. The
    /// release uses this handle rather than reopening the id, so a reused process id can never be
    /// touched. Disposing it releases the handle.
    /// </summary>
    public sealed class StartedClient : IDisposable
    {
        private readonly IDisposable _owner;

        /// <summary>Creates a started client over <paramref name="handle"/>, kept alive by <paramref name="owner"/> (the <see cref="Process"/>).</summary>
        public StartedClient(int id, IntPtr handle, IDisposable owner = null)
        {
            Id = id;
            Handle = handle;
            _owner = owner;
        }

        /// <summary>The process id (for log lines only).</summary>
        public int Id { get; }

        /// <summary>The process handle returned by CreateProcess.</summary>
        public IntPtr Handle { get; }

        /// <inheritdoc />
        public void Dispose()
        {
            _owner?.Dispose();
        }
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
    /// The Windows calls <see cref="ClientLockReleaser"/> makes; the seam the tests fake. There is
    /// deliberately no way to open a process by id: the only process handles used are the launcher's
    /// own pseudo-handle and the CreateProcess handle of a client it started.
    /// </summary>
    public interface IClientHandleApi
    {
        /// <summary>True when every call below is available (Windows 10 or later).</summary>
        bool Supported { get; }

        /// <summary>A pseudo-handle for the launcher's own process.</summary>
        IntPtr CurrentProcess { get; }

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

        /// <summary>The last Windows error code (or NTSTATUS) of the most recent failed call.</summary>
        int LastError { get; }
    }

    /// <summary>
    /// Lets a second acclient.exe start. The client refuses to run when the named semaphore
    /// <see cref="LockName"/> already exists (it calls CreateSemaphoreA with that name and checks for
    /// ERROR_ALREADY_EXISTS), so after the launcher starts a client it closes that client's handle to
    /// the lock. Owner ruling, a narrow exemption: no client file is modified, no code is injected,
    /// only a process the launcher itself just started is touched (through its CreateProcess handle),
    /// and nothing but the lock handle is closed. It reads only the target's own handle table, matches
    /// by object identity against the launcher's own handle to the named semaphore, and never queries
    /// object names.
    /// </summary>
    public sealed class ClientLockReleaser
    {
        /// <summary>The client's single-instance semaphore (acclient.exe, CreateSemaphoreA near VA 0x4122ae).</summary>
        public const string LockName = "Empyrean Client";

        /// <summary>
        /// Logged as the error when the launcher's own semaphore handle is missing from its own handle
        /// table (no Windows call failed, so there is no real error code). Customer bit set: never a
        /// Windows code.
        /// </summary>
        public const int OwnHandleNotListed = unchecked((int)0xE0000001);

        /// <summary>How long to wait for a started client to create its lock.</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

        /// <summary>Pause between two looks for the lock.</summary>
        public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

        private readonly IClientHandleApi _api;
        private readonly Action<string> _log;
        private readonly Func<Func<TimeSpan>> _clock;
        private readonly Action<TimeSpan, CancellationToken> _sleep;

        /// <summary>Creates a releaser over the real Windows calls.</summary>
        public ClientLockReleaser(Action<string> log)
            : this(new NativeClientHandleApi(), log, null, null)
        {
        }

        /// <summary>Creates a releaser over the given seams (tests). Null clock / sleep use the real ones.</summary>
        public ClientLockReleaser(IClientHandleApi api, Action<string> log, Func<Func<TimeSpan>> clock, Action<TimeSpan, CancellationToken> sleep)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _log = log ?? (line => { });
            _clock = clock ?? (() =>
            {
                Stopwatch watch = Stopwatch.StartNew();
                return () => watch.Elapsed;
            });
            _sleep = sleep ?? ((t, c) => c.WaitHandle.WaitOne(t));
        }

        /// <summary>
        /// Waits (up to <paramref name="timeout"/>, default <see cref="DefaultTimeout"/>) for
        /// <paramref name="client"/> to create its lock, then closes its handle to it. A cancel stops the
        /// wait between looks; once the lock has been found the close always completes. Never throws;
        /// every outcome is written to the log. The client's process handle is used, never closed.
        /// </summary>
        public ClientLockOutcome Release(StartedClient client, TimeSpan? timeout = null, CancellationToken cancel = default(CancellationToken))
        {
            if (client == null)
            {
                throw new ArgumentNullException(nameof(client));
            }

            try
            {
                return ReleaseCore(client, timeout ?? DefaultTimeout, cancel);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                Note(client.Id, "unsupported (" + ex.GetType().Name + ")");
                return ClientLockOutcome.Unsupported;
            }
            catch (Exception ex)
            {
                // The client is already running; a failed release must never turn into a failed launch.
                Note(client.Id, "failed step=exception type=" + ex.GetType().FullName);
                return ClientLockOutcome.Failed;
            }
        }

        private ClientLockOutcome ReleaseCore(StartedClient client, TimeSpan timeout, CancellationToken cancel)
        {
            int pid = client.Id;
            if (!_api.Supported)
            {
                Note(pid, "unsupported (needs Windows 10 or later)");
                return ClientLockOutcome.Unsupported;
            }

            IntPtr process = client.Handle;
            Func<TimeSpan> elapsed = _clock();
            while (true)
            {
                IntPtr own = _api.OpenSemaphore(LockName);
                if (own != IntPtr.Zero)
                {
                    List<IntPtr> found;
                    string failure;
                    int error;
                    try
                    {
                        found = FindInTarget(process, own, out failure, out error);
                    }
                    finally
                    {
                        // Before the close below: while the launcher holds its own handle the object
                        // stays alive, and the next client would still find it.
                        _api.CloseOwn(own);
                    }

                    if (failure != null)
                    {
                        if (failure == "handle-table" && _api.HasExited(process))
                        {
                            Note(pid, "client exited before its lock was released after=" + Seconds(elapsed()));
                            return ClientLockOutcome.ClientExited;
                        }

                        Note(pid, "failed step=" + failure + " error=" + Code(error));
                        return ClientLockOutcome.Failed;
                    }

                    if (found.Count > 0)
                    {
                        // Found: from here on the close is not interruptible.
                        return Close(pid, process, found, elapsed);
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

                if (cancel.IsCancellationRequested)
                {
                    Note(pid, "cancelled after=" + Seconds(elapsed()));
                    return ClientLockOutcome.Cancelled;
                }

                _sleep(PollInterval, cancel);
            }
        }

        private ClientLockOutcome Close(int pid, IntPtr process, List<IntPtr> found, Func<TimeSpan> elapsed)
        {
            var closed = new List<string>();
            foreach (IntPtr value in found)
            {
                if (!_api.CloseInProcess(process, value))
                {
                    Note(pid, "failed step=close error=" + Code(_api.LastError));
                    return ClientLockOutcome.Failed;
                }

                closed.Add("0x" + value.ToInt64().ToString("x", CultureInfo.InvariantCulture));
            }

            // The object outlives the close when any other process still holds it; the next client
            // would then be refused anyway.
            IntPtr again = _api.OpenSemaphore(LockName);
            if (again != IntPtr.Zero)
            {
                _api.CloseOwn(again);
                Note(pid, "closed handle=" + string.Join(",", closed) + " but the lock is held elsewhere after=" + Seconds(elapsed()));
                return ClientLockOutcome.HeldElsewhere;
            }

            Note(pid, "released handle=" + string.Join(",", closed) + " after=" + Seconds(elapsed()));
            return ClientLockOutcome.Released;
        }

        /// <summary>
        /// The target's handles to the same object as <paramref name="own"/> (empty when it holds none
        /// yet). Only handles of the semaphore's own object type are duplicated, and each duplicate is
        /// closed before returning. <paramref name="failure"/> names the failed step, or is null.
        /// </summary>
        private List<IntPtr> FindInTarget(IntPtr process, IntPtr own, out string failure, out int error)
        {
            failure = null;
            error = 0;
            var found = new List<IntPtr>();
            IReadOnlyList<ClientHandleEntry> mine = _api.HandleTable(_api.CurrentProcess);
            if (mine == null)
            {
                failure = "own-type";
                error = _api.LastError;
                return found;
            }

            int? semaphoreType = null;
            foreach (ClientHandleEntry entry in mine)
            {
                if (entry.Value == own)
                {
                    semaphoreType = entry.TypeIndex;
                    break;
                }
            }

            if (semaphoreType == null)
            {
                failure = "own-type";
                error = OwnHandleNotListed;
                return found;
            }

            IReadOnlyList<ClientHandleEntry> table = _api.HandleTable(process);
            if (table == null)
            {
                failure = "handle-table";
                error = _api.LastError;
                return found;
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
                    found.Add(entry.Value);
                }
            }

            return found;
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
