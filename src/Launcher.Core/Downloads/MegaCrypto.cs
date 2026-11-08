using System;
using System.Security.Cryptography;

namespace DreamWeave.Launcher.Core.Downloads
{
    /// <summary>
    /// A mega.nz public file link, <c>https://mega.nz/file/&lt;handle&gt;#&lt;key&gt;</c>. The key is 32 bytes of
    /// base64url: the AES-128 file key is its first half XOR its second half, and the CTR nonce is
    /// bytes 16 to 23 (the last 8 bytes are mega's meta-MAC, which this launcher does not need because
    /// every download is held to the manifest's SHA-256 instead).
    /// </summary>
    internal sealed class MegaLink
    {
        private MegaLink(string handle, byte[] key, byte[] nonce)
        {
            Handle = handle;
            Key = key;
            Nonce = nonce;
        }

        /// <summary>The public file handle (the path segment after <c>/file/</c>).</summary>
        public string Handle { get; }

        /// <summary>AES-128 file key (16 bytes).</summary>
        public byte[] Key { get; }

        /// <summary>CTR nonce (8 bytes): the high half of every counter block.</summary>
        public byte[] Nonce { get; }

        /// <summary>Parses a <c>/file/&lt;handle&gt;#&lt;key&gt;</c> link; false for anything else.</summary>
        public static bool TryParse(Uri link, out MegaLink parsed)
        {
            parsed = null;
            if (link == null || !link.IsAbsoluteUri)
            {
                return false;
            }

            string[] segments = link.AbsolutePath.Trim('/').Split('/');
            if (segments.Length != 2 || segments[0] != "file" || segments[1].Length == 0 || link.Fragment.Length < 2)
            {
                return false;
            }

            byte[] raw = FromBase64Url(link.Fragment.Substring(1));
            if (raw == null || raw.Length != 32)
            {
                return false;
            }

            var key = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                key[i] = (byte)(raw[i] ^ raw[i + 16]);
            }

            var nonce = new byte[8];
            Buffer.BlockCopy(raw, 16, nonce, 0, 8);
            parsed = new MegaLink(segments[1], key, nonce);
            return true;
        }

        /// <summary>Builds the link text for a handle and a raw 32-byte key (tests build fixtures with it).</summary>
        internal static string Format(string handle, byte[] rawKey)
        {
            string b64 = Convert.ToBase64String(rawKey).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return "https://mega.nz/file/" + handle + "#" + b64;
        }

        private static byte[] FromBase64Url(string s)
        {
            string b64 = s.Replace('-', '+').Replace('_', '/').Replace(",", string.Empty);
            switch (b64.Length % 4)
            {
                case 2:
                    b64 += "==";
                    break;
                case 3:
                    b64 += "=";
                    break;
                case 1:
                    return null;
            }

            try
            {
                return Convert.FromBase64String(b64);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// AES-128-CTR as mega.nz applies it to file bodies: the counter block for byte offset
    /// <c>p</c> is the 8-byte nonce followed by <c>p / 16</c> as a big-endian 64-bit integer, so any
    /// range can be decrypted on its own. Encryption and decryption are the same operation. One
    /// instance per thread: the underlying transform is not thread-safe.
    /// </summary>
    internal sealed class MegaCtr : IDisposable
    {
        private const int BlocksPerBatch = 4096;
        private readonly byte[] _nonce;
        private readonly Aes _aes;
        private readonly ICryptoTransform _ecb;
        private readonly byte[] _counters = new byte[BlocksPerBatch * 16];
        private readonly byte[] _stream = new byte[BlocksPerBatch * 16];

        public MegaCtr(byte[] key, byte[] nonce)
        {
            if (key == null || key.Length != 16)
            {
                throw new ArgumentException("A 16-byte key is required.", nameof(key));
            }

            if (nonce == null || nonce.Length != 8)
            {
                throw new ArgumentException("An 8-byte nonce is required.", nameof(nonce));
            }

            _nonce = (byte[])nonce.Clone();
            _aes = Aes.Create();
            _aes.Mode = CipherMode.ECB;
            _aes.Padding = PaddingMode.None;
            _aes.Key = key;
            _ecb = _aes.CreateEncryptor();
        }

        /// <summary>XORs the keystream for file offsets <paramref name="fileOffset"/>.. into <paramref name="buffer"/> in place.</summary>
        public void Transform(byte[] buffer, int offset, int count, long fileOffset)
        {
            if (fileOffset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(fileOffset));
            }

            int done = 0;
            while (done < count)
            {
                long position = fileOffset + done;
                long firstBlock = position / 16;
                int skip = (int)(position % 16);
                int bytesWanted = Math.Min(count - done, (BlocksPerBatch * 16) - skip);
                int blocks = (skip + bytesWanted + 15) / 16;
                for (int b = 0; b < blocks; b++)
                {
                    int o = b * 16;
                    Buffer.BlockCopy(_nonce, 0, _counters, o, 8);
                    ulong counter = (ulong)(firstBlock + b);
                    for (int i = 7; i >= 0; i--)
                    {
                        _counters[o + 8 + i] = (byte)counter;
                        counter >>= 8;
                    }
                }

                _ecb.TransformBlock(_counters, 0, blocks * 16, _stream, 0);
                for (int i = 0; i < bytesWanted; i++)
                {
                    buffer[offset + done + i] ^= _stream[skip + i];
                }

                done += bytesWanted;
            }
        }

        public void Dispose()
        {
            _ecb.Dispose();
            _aes.Dispose();
        }
    }
}
