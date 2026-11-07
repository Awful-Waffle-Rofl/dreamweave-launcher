using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Install;
using DreamWeave.Launcher.Core.Manifests;

namespace DreamWeave.Launcher.Tests.Install
{
    internal static class InstallData
    {
        public static byte[] Bytes(string text)
        {
            return Encoding.ASCII.GetBytes(text);
        }

        public static string Hash(byte[] data)
        {
            return Hasher.ComputeSha256Hex(data);
        }

        public static ManifestFile File(string path, byte[] content)
        {
            return new ManifestFile { Path = path, Size = content.Length, Sha256 = Hash(content) };
        }

        public static ManifestComponent Overlay(string id, params ManifestFile[] files)
        {
            return new ManifestComponent { Id = id, Kind = ComponentKinds.ZipOverlay, Size = 1, Sha256 = new string('A', 64), Url = "https://example.test/x.zip", Files = files.ToList() };
        }

        public static LauncherManifest Manifest(params ManifestComponent[] components)
        {
            return new LauncherManifest { Schema = 1, Serial = 1, Components = components.ToList() };
        }

        /// <summary>Writes a zip whose entries are (name, content) in order.</summary>
        public static string Zip(string path, params KeyValuePair<string, byte[]>[] entries)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (KeyValuePair<string, byte[]> e in entries)
                {
                    ZipArchiveEntry entry = zip.CreateEntry(e.Key, CompressionLevel.Fastest);
                    if (e.Value != null)
                    {
                        using (Stream s = entry.Open())
                        {
                            s.Write(e.Value, 0, e.Value.Length);
                        }
                    }
                }
            }

            return path;
        }

        public static KeyValuePair<string, byte[]> Entry(string name, byte[] content)
        {
            return new KeyValuePair<string, byte[]>(name, content);
        }

        /// <summary>Creates a directory junction with <c>cmd /c mklink /J</c>; false (with a reason) when that is not possible here.</summary>
        public static bool TryMakeJunction(string link, string target, out string reason)
        {
            Directory.CreateDirectory(target);
            var psi = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            try
            {
                using (Process p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10000);
                    if (!Directory.Exists(link) || (System.IO.File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0)
                    {
                        reason = "mklink /J did not create a junction: " + err;
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                reason = "mklink /J could not run: " + ex.Message;
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>Simulates a running game client: holds the file open the way a reader/writer would.</summary>
        public static FileStream HoldLikeGame(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        }

        public static bool AnyTempLeft(string folder)
        {
            return Directory.Exists(folder)
                && (Directory.GetFiles(folder, "*" + ".dwtmp", SearchOption.AllDirectories).Length > 0
                    || Directory.GetFiles(folder, "*~RF*.TMP", SearchOption.AllDirectories).Length > 0);
        }

        /// <summary>True when no FILE remains anywhere under <paramref name="folder"/> (empty component folders are fine).</summary>
        public static bool NoFilesUnder(string folder)
        {
            return !Directory.Exists(folder) || Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length == 0;
        }
    }

    /// <summary>
    /// A COPY of %WINDIR%\System32\ping.exe running <c>-t 127.0.0.1</c> from a temp folder: a real
    /// running executable whose image is mapped. Dispose kills only that process.
    /// </summary>
    internal sealed class RunningExe : IDisposable
    {
        private readonly Process _process;

        public RunningExe(string path)
        {
            Path = path;
            System.IO.File.Copy(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE"), path, true);
            _process = Process.Start(new ProcessStartInfo(path, "-t 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            _process.BeginOutputReadLine();
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                }

                _process.WaitForExit(10000);
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                _process.Dispose();
            }
        }
    }

    /// <summary>A disk-space query reporting a fixed number of free bytes.</summary>
    internal sealed class FixedDiskSpace : DreamWeave.Launcher.Core.Downloads.IDiskSpace
    {
        private readonly long _free;

        public FixedDiskSpace(long free)
        {
            _free = free;
        }

        public long GetFreeBytes(string directory) => _free;
    }

    /// <summary>
    /// A seekable stream that serves <see cref="First"/> on its first pass and <see cref="Later"/>
    /// on every pass after a rewind to 0: a staged file swapped between verification and copy.
    /// </summary>
    internal sealed class SwapAfterFirstPassStream : Stream
    {
        private int _pass;
        private long _position;

        public SwapAfterFirstPassStream(byte[] first, byte[] later)
        {
            First = first;
            Later = later;
        }

        public byte[] First { get; }

        public byte[] Later { get; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => First.Length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value == 0 && _position != 0)
                {
                    _pass++;
                }

                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            byte[] src = _pass == 0 ? First : Later;
            int n = (int)Math.Max(0, Math.Min(count, src.Length - _position));
            Buffer.BlockCopy(src, (int)_position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? _position + offset : Length + offset;
            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Denies the current user write/delete on a folder (inherited) until disposed.</summary>
    internal sealed class DenyWrite : IDisposable
    {
        private readonly string _folder;
        private readonly FileSystemAccessRule _rule;

        private DenyWrite(string folder, FileSystemAccessRule rule)
        {
            _folder = folder;
            _rule = rule;
        }

        public static DenyWrite TryApply(string folder, out string reason)
        {
            try
            {
                SecurityIdentifier me = WindowsIdentity.GetCurrent().User;
                var rule = new FileSystemAccessRule(
                    me,
                    FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Deny);
                DirectorySecurity acl = Directory.GetAccessControl(folder);
                acl.AddAccessRule(rule);
                Directory.SetAccessControl(folder, acl);
                reason = null;
                return new DenyWrite(folder, rule);
            }
            catch (Exception ex)
            {
                reason = "could not set a deny ACL: " + ex.Message;
                return null;
            }
        }

        public void Dispose()
        {
            DirectorySecurity acl = Directory.GetAccessControl(_folder);
            acl.RemoveAccessRule(_rule);
            Directory.SetAccessControl(_folder, acl);
        }
    }

    /// <summary>Process starter that never starts anything; records calls and can run a callback or throw.</summary>
    internal sealed class FakeStarter : IProcessStarter
    {
        public List<ProcessStartInfo> Calls { get; } = new List<ProcessStartInfo>();

        public Action<ProcessStartInfo> OnStart { get; set; }

        public Exception Throw { get; set; }

        public IStartedProcess Start(ProcessStartInfo startInfo)
        {
            Calls.Add(startInfo);
            OnStart?.Invoke(startInfo);
            if (Throw != null)
            {
                throw Throw;
            }

            return new FakeProcess();
        }

        internal sealed class FakeProcess : IStartedProcess
        {
            public bool HasExited => true;

            public int? ExitCode => 0;

            public void Dispose()
            {
            }
        }
    }
}
