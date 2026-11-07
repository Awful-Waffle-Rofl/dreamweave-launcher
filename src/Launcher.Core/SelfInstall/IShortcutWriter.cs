using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace DreamWeave.Launcher.Core.SelfInstall
{
    /// <summary>Creates and removes Windows shortcut (.lnk) files. Production implementation: <see cref="ShellLinkWriter"/>.</summary>
    public interface IShortcutWriter
    {
        /// <summary>Creates or overwrites the shortcut at <paramref name="shortcutPath"/>.</summary>
        /// <exception cref="LauncherException"><see cref="LauncherError.ShortcutFailed"/>.</exception>
        void Write(string shortcutPath, string targetPath, string workingDirectory, string description);

        /// <summary>The target path stored in an existing shortcut, or null when the file is absent or unreadable.</summary>
        string ReadTarget(string shortcutPath);
    }

    /// <summary>
    /// <see cref="IShortcutWriter"/> over the shell's own IShellLinkW / IPersistFile COM objects
    /// (no WScript.Shell, which policy can disable, and no extra package).
    /// </summary>
    public sealed class ShellLinkWriter : IShortcutWriter
    {
        /// <inheritdoc />
        public void Write(string shortcutPath, string targetPath, string workingDirectory, string description)
        {
            object link = null;
            try
            {
                link = new ShellLinkCoClass();
                var shellLink = (IShellLinkW)link;
                shellLink.SetPath(targetPath);
                shellLink.SetWorkingDirectory(workingDirectory ?? string.Empty);
                shellLink.SetDescription(description ?? string.Empty);
                shellLink.SetIconLocation(targetPath, 0);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(shortcutPath)));
                ((IPersistFile)link).Save(shortcutPath, true);
            }
            catch (Exception ex) when (ex is COMException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidCastException)
            {
                throw new LauncherException(LauncherError.ShortcutFailed, "Could not create the Start-menu shortcut " + shortcutPath + ": " + ex.Message, ex);
            }
            finally
            {
                if (link != null)
                {
                    Marshal.ReleaseComObject(link);
                }
            }
        }

        /// <inheritdoc />
        public string ReadTarget(string shortcutPath)
        {
            if (!File.Exists(shortcutPath))
            {
                return null;
            }

            object link = null;
            try
            {
                link = new ShellLinkCoClass();
                ((IPersistFile)link).Load(shortcutPath, 0);
                var sb = new StringBuilder(1024);
                ((IShellLinkW)link).GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                return sb.ToString();
            }
            catch (Exception ex) when (ex is COMException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidCastException)
            {
                return null;
            }
            finally
            {
                if (link != null)
                {
                    Marshal.ReleaseComObject(link);
                }
            }
        }

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLinkCoClass
        {
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);

            void GetIDList(out IntPtr ppidl);

            void SetIDList(IntPtr pidl);

            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);

            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);

            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);

            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

            void GetHotkey(out short pwHotkey);

            void SetHotkey(short wHotkey);

            void GetShowCmd(out int piShowCmd);

            void SetShowCmd(int iShowCmd);

            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);

            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);

            void Resolve(IntPtr hwnd, int fFlags);

            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }
    }
}
