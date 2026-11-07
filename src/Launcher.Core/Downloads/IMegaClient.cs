extern alias MegaApi;

using System;
using System.Threading;
using System.Threading.Tasks;
using MegaApi::CG.Web.MegaApiClient;

namespace DreamWeave.Launcher.Core.Downloads
{
    /// <summary>
    /// The slice of MegaApiClient the launcher uses, so tests can fake mega.nz. Implementations may
    /// throw MegaApiClient's own exception types; <see cref="MegaErrorMapper"/> classifies them.
    /// One instance serves one download attempt.
    /// </summary>
    public interface IMegaClient : IDisposable
    {
        /// <summary>Opens an anonymous session (no account).</summary>
        Task LoginAnonymousAsync();

        /// <summary>Resolves a public file link (<c>https://mega.nz/file/&lt;id&gt;#&lt;key&gt;</c>).</summary>
        Task<MegaFile> GetFileFromLinkAsync(Uri link);

        /// <summary>
        /// Downloads and decrypts <paramref name="file"/> into a NEW file at <paramref name="path"/>
        /// (which must not exist), reporting percent complete in [0, 100].
        /// </summary>
        Task DownloadFileAsync(MegaFile file, string path, IProgress<double> percent, CancellationToken cancellationToken);
    }

    /// <summary>A resolved mega.nz file.</summary>
    public sealed class MegaFile
    {
        /// <summary>Creates a file handle.</summary>
        public MegaFile(string name, long size, object handle)
        {
            Name = name;
            Size = size;
            Handle = handle;
        }

        /// <summary>File name as stored on mega.nz.</summary>
        public string Name { get; }

        /// <summary>Decrypted length in bytes.</summary>
        public long Size { get; }

        /// <summary>Implementation-specific node object.</summary>
        public object Handle { get; }
    }

    /// <summary>Production <see cref="IMegaClient"/> over MegaApiClient 1.10.5.</summary>
    internal sealed class MegaApiClientAdapter : IMegaClient
    {
        private readonly MegaApiClient _client;

        static MegaApiClientAdapter()
        {
            Net.TlsConfig.EnsureModernTls();
        }

        public MegaApiClientAdapter()
        {
            _client = new MegaApiClient();
        }

        public Task LoginAnonymousAsync()
        {
            return _client.LoginAnonymousAsync();
        }

        public async Task<MegaFile> GetFileFromLinkAsync(Uri link)
        {
            INode node = await _client.GetNodeFromLinkAsync(link).ConfigureAwait(false);
            return new MegaFile(node.Name, node.Size, node);
        }

        public Task DownloadFileAsync(MegaFile file, string path, IProgress<double> percent, CancellationToken cancellationToken)
        {
            return _client.DownloadFileAsync((INode)file.Handle, path, percent, cancellationToken);
        }

        public void Dispose()
        {
            // Anonymous sessions hold no server-side state worth logging out of, and MegaApiClient
            // shares one static HttpClient across instances, so there is nothing to release.
        }
    }
}
