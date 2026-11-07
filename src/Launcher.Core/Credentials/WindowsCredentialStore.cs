using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace DreamWeave.Launcher.Core.Credentials
{
    /// <summary>
    /// <see cref="ICredentialStore"/> over the Windows Credential Manager (generic credentials,
    /// target <c>DreamWeave/&lt;account&gt;</c>). Windows encrypts the blob with the user's logon
    /// secrets; the launcher never writes a password to disk itself.
    /// <para>
    /// Persistence is <c>CRED_PERSIST_LOCAL_MACHINE</c>, chosen deliberately: it survives log-off and
    /// reboot (so "remember me" works) and stays on this computer for this user. ENTERPRISE would
    /// roam the secret to other machines through the domain, which a game password should never do;
    /// SESSION would vanish at log-off.
    /// </para>
    /// <para>
    /// Managed strings are immutable and cannot be wiped; the unmanaged and byte[] copies made here
    /// are zeroed as soon as they are no longer needed.
    /// </para>
    /// </summary>
    public sealed class WindowsCredentialStore : ICredentialStore
    {
        /// <summary>The target prefix the launcher uses.</summary>
        public const string DefaultTargetPrefix = "DreamWeave/";

        private const uint CredTypeGeneric = 1;
        private const uint CredPersistLocalMachine = 2;
        private const int ErrorNotFound = 1168;
        private const int MaxBlobBytes = 2560; // CRED_MAX_CREDENTIAL_BLOB_SIZE (5 * 512)

        private readonly string prefix;

        /// <summary>Creates a store using <see cref="DefaultTargetPrefix"/>.</summary>
        public WindowsCredentialStore()
            : this(DefaultTargetPrefix)
        {
        }

        /// <summary>Creates a store whose targets start with <paramref name="targetPrefix"/> (tests use a unique one).</summary>
        public WindowsCredentialStore(string targetPrefix)
        {
            if (string.IsNullOrEmpty(targetPrefix))
            {
                throw new ArgumentException("A target prefix is required.", nameof(targetPrefix));
            }

            prefix = targetPrefix;
        }

        /// <inheritdoc />
        public void Save(string account, string password)
        {
            RequireAccount(account);
            if (password == null)
            {
                throw new ArgumentNullException(nameof(password));
            }

            byte[] blob = Encoding.Unicode.GetBytes(password);
            IntPtr blobPtr = IntPtr.Zero;
            try
            {
                if (blob.Length > MaxBlobBytes)
                {
                    throw new LauncherException(LauncherError.CredentialStoreUnavailable, "That password is too long to store in Windows Credential Manager.");
                }

                blobPtr = Marshal.AllocHGlobal(Math.Max(blob.Length, 1));
                Marshal.Copy(blob, 0, blobPtr, blob.Length);

                var cred = new Credential
                {
                    Type = CredTypeGeneric,
                    TargetName = Target(account),
                    CredentialBlobSize = (uint)blob.Length,
                    CredentialBlob = blobPtr,
                    Persist = CredPersistLocalMachine,
                    UserName = account,
                };

                if (!CredWrite(ref cred, 0))
                {
                    throw Unavailable("save", Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                Array.Clear(blob, 0, blob.Length);
                if (blobPtr != IntPtr.Zero)
                {
                    ZeroUnmanaged(blobPtr, blob.Length);
                    Marshal.FreeHGlobal(blobPtr);
                }
            }
        }

        /// <inheritdoc />
        public string Get(string account)
        {
            RequireAccount(account);
            IntPtr credPtr;
            if (!CredRead(Target(account), CredTypeGeneric, 0, out credPtr))
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ErrorNotFound)
                {
                    return null;
                }

                throw Unavailable("read", err);
            }

            try
            {
                return ReadPassword(credPtr);
            }
            finally
            {
                CredFree(credPtr);
            }
        }

        /// <inheritdoc />
        public bool Delete(string account)
        {
            RequireAccount(account);
            bool any = false;

            // Repeat until Windows reports not-found, so no duplicate under the normalized key survives.
            while (DeleteTarget(Target(account)))
            {
                any = true;
            }

            // Legacy entries saved under another spelling of the same account name.
            foreach (string target in EnumerateTargets())
            {
                if (string.Equals(target, prefix + account, StringComparison.OrdinalIgnoreCase) && DeleteTarget(target))
                {
                    any = true;
                }
            }

            return any;
        }

        private bool DeleteTarget(string target)
        {
            if (CredDelete(target, CredTypeGeneric, 0))
            {
                return true;
            }

            int err = Marshal.GetLastWin32Error();
            if (err == ErrorNotFound)
            {
                return false;
            }

            throw Unavailable("delete", err);
        }

        private string Target(string account)
        {
            // Normalized so "Alice" and "alice" are one entry (the server lowercases account names).
            return prefix + account.ToLowerInvariant();
        }

        private List<string> EnumerateTargets()
        {
            var targets = new List<string>();
            int count;
            IntPtr array;
            if (!CredEnumerate(prefix + "*", 0, out count, out array))
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ErrorNotFound)
                {
                    return targets;
                }

                throw Unavailable("list", err);
            }

            try
            {
                for (int i = 0; i < count; i++)
                {
                    IntPtr credPtr = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                    var cred = (Credential)Marshal.PtrToStructure(credPtr, typeof(Credential));
                    if (cred.TargetName != null && cred.Type == CredTypeGeneric &&
                        cred.TargetName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        targets.Add(cred.TargetName);
                    }
                }
            }
            finally
            {
                CredFree(array);
            }

            return targets;
        }
        /// <inheritdoc />
        public IReadOnlyList<string> ListAccounts()
        {
            var names = new List<string>();
            foreach (string target in EnumerateTargets())
            {
                string name = target.Substring(prefix.Length);
                if (!names.Exists(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                {
                    names.Add(name);
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
        private static string ReadPassword(IntPtr credPtr)
        {
            var cred = (Credential)Marshal.PtrToStructure(credPtr, typeof(Credential));
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0)
            {
                return string.Empty;
            }

            byte[] bytes = new byte[cred.CredentialBlobSize];
            try
            {
                Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
                return Encoding.Unicode.GetString(bytes);
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
                ZeroUnmanaged(cred.CredentialBlob, bytes.Length);
            }
        }

        private static void ZeroUnmanaged(IntPtr ptr, int length)
        {
            if (length > 0)
            {
                Marshal.Copy(new byte[length], 0, ptr, length);
            }
        }

        private static void RequireAccount(string account)
        {
            if (string.IsNullOrEmpty(account))
            {
                throw new ArgumentException("An account name is required.", nameof(account));
            }
        }

        private static LauncherException Unavailable(string what, int win32)
        {
            return new LauncherException(
                LauncherError.CredentialStoreUnavailable,
                "Windows Credential Manager could not " + what + " the saved password (Windows error " + win32 + ").");
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public uint Flags;
            public uint Type;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string TargetName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string TargetAlias;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref Credential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredEnumerate(string filter, uint flags, out int count, out IntPtr credentials);

        [DllImport("advapi32.dll", SetLastError = false)]
        private static extern void CredFree(IntPtr buffer);
    }
}
