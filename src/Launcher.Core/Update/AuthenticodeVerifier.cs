using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DreamWeave.Launcher.Core.Update
{
    /// <summary>Checks a file's Authenticode signature. Production implementation: <see cref="WinTrustVerifier"/>.</summary>
    public interface IAuthenticodeVerifier
    {
        /// <summary>
        /// Succeeds only when the file open as <paramref name="heldFile"/> (at <paramref name="path"/>)
        /// carries a valid embedded Authenticode signature that chains to a trusted root AND whose
        /// signer certificate subject equals <paramref name="expectedSubject"/> exactly.
        /// </summary>
        /// <exception cref="LauncherException"><see cref="LauncherError.UpdateSignatureInvalid"/>.</exception>
        void Verify(string path, FileStream heldFile, string expectedSubject);
    }

    /// <summary>
    /// <see cref="IAuthenticodeVerifier"/> over WinVerifyTrust (WINTRUST_ACTION_GENERIC_VERIFY_V2,
    /// no UI, revocation checked for the whole chain except the root). The file is verified through
    /// the caller's held handle, so the bytes checked are the bytes that will be installed. The
    /// subject comparison is ordinal against <see cref="X509Certificate.Subject"/> (for example
    /// <c>CN=Example Signer, O=Example Signer, C=US</c>), so the manifest must carry it exactly as
    /// Windows formats it.
    /// </summary>
    public sealed class WinTrustVerifier : IAuthenticodeVerifier
    {
        private const uint WTD_UI_NONE = 2;
        private const uint WTD_REVOKE_WHOLECHAIN = 1;
        private const uint WTD_CHOICE_FILE = 1;
        private const uint WTD_STATEACTION_VERIFY = 1;
        private const uint WTD_STATEACTION_CLOSE = 2;
        private const uint WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT = 0x80;

        private static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        /// <inheritdoc />
        public void Verify(string path, FileStream heldFile, string expectedSubject)
        {
            if (string.IsNullOrEmpty(expectedSubject))
            {
                throw new ArgumentException("An expected signer subject is required.", nameof(expectedSubject));
            }

            int result = VerifyTrust(path, heldFile);
            if (result != 0)
            {
                throw new LauncherException(LauncherError.UpdateSignatureInvalid, "The launcher update's digital signature is not valid (WinVerifyTrust 0x" + result.ToString("X8") + "). The update was not installed.");
            }

            string subject;
            try
            {
                using (var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)))
                {
                    subject = cert.Subject;
                }
            }
            catch (CryptographicException ex)
            {
                throw new LauncherException(LauncherError.UpdateSignatureInvalid, "The launcher update's signer could not be read. The update was not installed.", ex);
            }

            if (!string.Equals(subject, expectedSubject, StringComparison.Ordinal))
            {
                throw new LauncherException(LauncherError.UpdateSignatureInvalid, "The launcher update is signed by '" + subject + "', not by '" + expectedSubject + "'. The update was not installed.");
            }
        }

        /// <summary>The raw WinVerifyTrust result (0 = trusted) for the file.</summary>
        internal static int VerifyTrust(string path, FileStream heldFile)
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)),
                pcwszFilePath = path,
                hFile = heldFile != null ? heldFile.SafeFileHandle.DangerousGetHandle() : IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };

            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
            try
            {
                Marshal.StructureToPtr(fileInfo, pFile, false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_WHOLECHAIN,
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pFile = pFile,
                    dwStateAction = WTD_STATEACTION_VERIFY,
                    dwProvFlags = WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT,
                };

                int result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
                data.dwStateAction = WTD_STATEACTION_CLOSE;
                WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
                GC.KeepAlive(heldFile);
                return result;
            }
            finally
            {
                Marshal.DestroyStructure(pFile, typeof(WINTRUST_FILE_INFO));
                Marshal.FreeHGlobal(pFile);
            }
        }

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string pcwszFilePath;

            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }
    }
}
