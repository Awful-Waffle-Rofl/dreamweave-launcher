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
        /// <summary>A minimal valid schema-1 manifest as exact bytes (LF line endings, no BOM).</summary>
        public static byte[] Build(long serial, int schema = 1, string extraTopLevel = null)
        {
            return BuildRaw(serial.ToString(System.Globalization.CultureInfo.InvariantCulture), schema.ToString(System.Globalization.CultureInfo.InvariantCulture), extraTopLevel);
        }

        /// <summary>As <see cref="Build"/>, with the serial and schema spliced in as raw JSON text.</summary>
        public static byte[] BuildRaw(string serialJson, string schemaJson, string extraTopLevel = null)
        {
            string json =
                "{\n" +
                "  \"schema\": " + schemaJson + ",\n" +
                "  \"serial\": " + serialJson + ",\n" +
                "  \"issued\": \"2026-11-07T00:00:00Z\",\n" +
                "  \"server\": { \"name\": \"DreamWeave\", \"host\": \"play.example.test\", \"port\": 9000 },\n" +
                "  \"components\": [\n" +
                "    { \"id\": \"ac-client\", \"kind\": \"installer\", \"required\": true, \"url\": \"https://example.test/a.exe\", \"size\": 3, \"sha256\": \"" + new string('A', 64) + "\" }\n" +
                "  ]" + (extraTopLevel == null ? string.Empty : ",\n  " + extraTopLevel) + "\n" +
                "}\n";
            return new UTF8Encoding(false).GetBytes(json);
        }
    }

    /// <summary>
    /// Scripted <see cref="IHttpFetcher"/>. Routes match on the URI WITHOUT its query string, so a
    /// cache-busting refetch hits the same route; a route can hold a sequence of responses (the
    /// last one repeats). Unknown URIs fail as network errors.
    /// </summary>
    internal sealed class FakeHttpFetcher : IHttpFetcher
    {
        private readonly Dictionary<string, Queue<Func<byte[]>>> _routes = new Dictionary<string, Queue<Func<byte[]>>>(StringComparer.Ordinal);

        public List<Tuple<Uri, long>> Requests { get; } = new List<Tuple<Uri, long>>();

        public void Serve(string uri, params byte[][] bodies)
        {
            var q = new Queue<Func<byte[]>>();
            foreach (byte[] b in bodies)
            {
                byte[] copy = b;
                q.Enqueue(() => copy);
            }

            _routes[uri] = q;
        }

        public void Fail(string uri, LauncherError code)
        {
            var q = new Queue<Func<byte[]>>();
            q.Enqueue(() => { throw new LauncherException(code, "scripted failure " + code); });
            _routes[uri] = q;
        }

        public void Throw(string uri, Exception ex)
        {
            var q = new Queue<Func<byte[]>>();
            q.Enqueue(() => { throw ex; });
            _routes[uri] = q;
        }

        public Task<byte[]> GetBytesAsync(Uri uri, long maxBytes, CancellationToken cancellationToken)
        {
            Requests.Add(Tuple.Create(uri, maxBytes));
            Queue<Func<byte[]>> route;
            if (!_routes.TryGetValue(uri.GetLeftPart(UriPartial.Path), out route))
            {
                throw new LauncherException(LauncherError.NetworkUnavailable, "no route for " + uri);
            }

            Func<byte[]> next = route.Count > 1 ? route.Dequeue() : route.Peek();
            byte[] body = next();
            if (body.Length > maxBytes)
            {
                throw new LauncherException(LauncherError.ResponseTooLarge, "too large");
            }

            return Task.FromResult(body);
        }
    }
}
