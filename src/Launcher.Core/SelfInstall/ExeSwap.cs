using System;
using DreamWeave.Launcher.Core.Cleanup;
using System.IO;
using System.Threading;

namespace DreamWeave.Launcher.Core.SelfInstall
{
    /// <summary>
    /// The exe replacement used by self-update. Windows lets a
    /// RUNNING exe be renamed but not deleted or overwritten, so the swap is two renames:
    /// <c>target -&gt; target.old</c>, then <c>target.new -&gt; target</c>. Any failure puts the original
    /// back. The incoming bytes are written through a handle that is held from the first byte
    /// written until after the rename into place, so nothing can alter them in between.
    /// </summary>
    internal static class ExeSwap
    {
        /// <summary>
        /// Copies <paramref name="source"/> (from its current position) into a new file at
        /// <c>target.new</c> held by the returned stream, hashing the bytes as they are written.
        /// The caller swaps with <see cref="Swap"/> while still holding the stream, then disposes it.
        /// </summary>
        public static FileStream WriteIncoming(ISelfFileSystem fs, Stream source, string targetPath, long expectedLength, IProgress<ProgressReport> progress, CancellationToken cancellationToken, string stepId, out string sha256)
        {
            string newPath = targetPath + CleanupLayout.NewSuffix;
            fs.DeleteFile(newPath);
            FileStream held = fs.CreateHeldForMove(newPath);
            try
            {
                using (var tee = new TeeReadStream(source, held))
                {
                    sha256 = Hasher.ComputeSha256Hex(tee, expectedLength, progress, cancellationToken, stepId);
                }

                held.Flush(true);
                if (held.Length != expectedLength)
                {
                    throw new LauncherException(LauncherError.SizeMismatch, "The launcher copy is " + held.Length + " bytes, but " + expectedLength + " bytes were expected.");
                }

                return held;
            }
            catch
            {
                Discard(fs, held, newPath);
                throw;
            }
        }

        /// <summary>Closes and deletes an incoming file that will not be swapped in. Never throws.</summary>
        public static void Discard(ISelfFileSystem fs, FileStream held, string newPath)
        {
            try
            {
                held?.Dispose();
                fs.DeleteFile(newPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

        /// <summary>
        /// Renames <paramref name="targetPath"/> (when it exists) to <c>target.old</c> and
        /// <c>target.new</c> to <paramref name="targetPath"/>. On failure the original is restored.
        /// Returns the <c>.old</c> path when there was an original, otherwise null.
        /// </summary>
        /// <exception cref="LauncherException">
        /// <see cref="LauncherError.UpdateSwapFailed"/> (nothing changed), or
        /// <see cref="LauncherError.UpdateRollbackFailed"/> (the original could not be put back).
        /// </exception>
        public static string Swap(ISelfFileSystem fs, string targetPath)
        {
            string newPath = targetPath + CleanupLayout.NewSuffix;
            string oldPath = targetPath + CleanupLayout.OldSuffix;
            bool hadOriginal = fs.FileExists(targetPath);
            if (!hadOriginal)
            {
                // No target to move aside, so a stale .old is just litter: remove it if we can.
                try
                {
                    fs.DeleteFile(oldPath);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }
            }
            else
            {
                try
                {
                    fs.DeleteFile(oldPath);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new LauncherException(LauncherError.UpdateSwapFailed, "An earlier copy of the launcher (" + oldPath + ") is still in use. Close every launcher window and try again.", ex);
                }

                try
                {
                    fs.Move(targetPath, oldPath);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new LauncherException(LauncherError.UpdateSwapFailed, "Could not move the current launcher aside: " + ex.Message, ex);
                }
            }

            try
            {
                fs.Move(newPath, targetPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (hadOriginal)
                {
                    try
                    {
                        fs.Move(oldPath, targetPath);
                    }
                    catch (Exception rollback) when (rollback is IOException || rollback is UnauthorizedAccessException)
                    {
                        throw new LauncherException(LauncherError.UpdateRollbackFailed, "Could not put the new launcher in place (" + ex.Message + ") and could not restore the previous one (" + rollback.Message + "). Download the launcher again to repair it.", rollback);
                    }
                }

                throw new LauncherException(LauncherError.UpdateSwapFailed, "Could not put the new launcher in place; the previous one was kept: " + ex.Message, ex);
            }

            return hadOriginal ? oldPath : null;
        }

        /// <summary>
        /// Undoes a completed <see cref="Swap"/>: the swapped-in exe goes back to <c>target.new</c> (then
        /// is deleted) and <c>target.old</c> returns to <paramref name="targetPath"/>.
        /// </summary>
        /// <exception cref="LauncherException"><see cref="LauncherError.UpdateRollbackFailed"/>.</exception>
        public static void Revert(ISelfFileSystem fs, string targetPath)
        {
            string newPath = targetPath + CleanupLayout.NewSuffix;
            string oldPath = targetPath + CleanupLayout.OldSuffix;
            try
            {
                fs.DeleteFile(newPath);
                fs.Move(targetPath, newPath);
                fs.Move(oldPath, targetPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new LauncherException(LauncherError.UpdateRollbackFailed, "Could not restore the previous launcher: " + ex.Message + ". Download the launcher again to repair it.", ex);
            }

            try
            {
                fs.DeleteFile(newPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

        /// <summary>A read-only stream that copies everything read from it into a sink.</summary>
        private sealed class TeeReadStream : Stream
        {
            private readonly Stream _source;
            private readonly Stream _sink;

            public TeeReadStream(Stream source, Stream sink)
            {
                _source = source;
                _sink = sink;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int read = _source.Read(buffer, offset, count);
                if (read > 0)
                {
                    _sink.Write(buffer, offset, read);
                }

                return read;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            // Neither stream is owned: the caller disposes both.
            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
            }
        }
    }
}
