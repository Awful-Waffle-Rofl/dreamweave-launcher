using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Net;

namespace DreamWeave.Launcher.Tests
{
    /// <summary>A unique temp folder deleted on dispose.</summary>
    internal sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dwl-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    internal static class TestManifests
    {
        /// <summary>A minimal schema-1 manifest as exact bytes (LF line endings, no BOM).</summary>
        public static byte[] Build(long serial, int schema = 1, string extraTopLevel = null)
        {
            string json =
                "{\n" +
                "  \"schema\": " + schema + ",\n" +
                "  \"serial\": " + serial + ",\n" +
                "  \"issued\": \"2026-11-07T00:00:00Z\",\n" +
                "  \"server\": { \"name\": \"DreamWeave\", \"host\": \"play.example.test\", \"port\": 9000 },\n" +
                "  \"components\": [\n" +
                "    { \"id\": \"ac-client\", \"kind\": \"installer\", \"required\": true, \"url\": \"https://example.test/a.exe\", \"size\": 3, \"sha256\": \"" + new string('A', 64) + "\" }\n" +
                "  ]" + (extraTopLevel == null ? string.Empty : ",\n  " + extraTopLevel) + "\n" +
                "}\n";
            return new UTF8Encoding(false).GetBytes(json);
        }
    }

    /// <summary>Scripted <see cref="IHttpFetcher"/>: each URI maps to bytes or an exception; unknown URIs fail as network errors.</summary>
    internal sealed class FakeHttpFetcher : IHttpFetcher
    {
        private readonly Dictionary<string, Func<byte[]>> _routes = new Dictionary<string, Func<byte[]>>(StringComparer.Ordinal);

        public List<Tuple<Uri, long>> Requests { get; } = new List<Tuple<Uri, long>>();

        public void Serve(string uri, byte[] body)
        {
            _routes[uri] = () => body;
        }

        public void Fail(string uri, LauncherError code)
        {
            _routes[uri] = () => { throw new LauncherException(code, "scripted failure " + code); };
        }

        public Task<byte[]> GetBytesAsync(Uri uri, long maxBytes, CancellationToken cancellationToken)
        {
            Requests.Add(Tuple.Create(uri, maxBytes));
            Func<byte[]> route;
            if (!_routes.TryGetValue(uri.AbsoluteUri, out route))
            {
                throw new LauncherException(LauncherError.NetworkUnavailable, "no route for " + uri);
            }

            byte[] body = route();
            if (body.Length > maxBytes)
            {
                throw new LauncherException(LauncherError.ResponseTooLarge, "too large");
            }

            return Task.FromResult(body);
        }
    }
}
