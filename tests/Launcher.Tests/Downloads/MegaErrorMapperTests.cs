extern alias MegaApi;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Downloads;
using MegaApi::CG.Web.MegaApiClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Downloads
{
    [TestClass]
    public class MegaErrorMapperTests
    {
        private static LauncherError Map(Exception ex, out bool transient)
        {
            return MegaErrorMapper.Map(ex, out transient).Code;
        }

        [TestMethod]
        public void QuotaClassCodes_MapToQuotaExceeded_NotTransient()
        {
            foreach (ApiResultCode code in new[] { ApiResultCode.QuotaExceeded, ApiResultCode.TooManyRequests, ApiResultCode.ToManyRequestsForThisResource, ApiResultCode.TooManyConnectionsOnThisResource })
            {
                bool transient;
                Assert.AreEqual(LauncherError.SourceQuotaExceeded, Map(MegaDownloadTests.Api(code), out transient), code.ToString());
                Assert.IsFalse(transient, code.ToString());
            }
        }

        [TestMethod]
        public void BusyCodes_AreTransientSourceErrors()
        {
            foreach (ApiResultCode code in new[] { ApiResultCode.RequestFailedRetry, ApiResultCode.ResourceTemporarilyNotAvailable, ApiResultCode.InternalError })
            {
                bool transient;
                Assert.AreEqual(LauncherError.SourceHttpError, Map(MegaDownloadTests.Api(code), out transient), code.ToString());
                Assert.IsTrue(transient, code.ToString());
            }
        }

        [TestMethod]
        public void NetworkFailures_AreTransientNetworkUnavailable()
        {
            foreach (Exception ex in new Exception[] { new IOException("reset", new System.Net.Sockets.SocketException(10054)), new WebException("dns"), new AggregateException(new HttpRequestException("An error occurred while sending the request.", new WebException("dns"))) })
            {
                bool transient;
                Assert.AreEqual(LauncherError.NetworkUnavailable, Map(ex, out transient), ex.GetType().Name);
                Assert.IsTrue(transient);
            }
        }

        [TestMethod]
        public void PlainIOException_FailsFast()
        {
            bool transient;
            Assert.AreEqual(LauncherError.Unknown, Map(new IOException("The device is not ready."), out transient));
            Assert.IsFalse(transient, "a non-network, non-sharing IOException is not retried");
        }

        [TestMethod]
        public void SharingViolation_IsTransient()
        {
            bool transient;
            Map(new IOException("The process cannot access the file because it is being used by another process.", unchecked((int)0x80070020)), out transient);
            Assert.IsTrue(transient);
        }

        [TestMethod]
        public void DiskFullIOException_IsDiskFull_NotTransient()
        {
            bool transient;
            Assert.AreEqual(LauncherError.DiskFull, Map(new IOException("full", unchecked((int)0x80070070)), out transient));
            Assert.IsFalse(transient);
        }

        [TestMethod]
        public void Localized509Message_WithoutEnglishMarker_IsStillQuota()
        {
            // A French-style framework message: no "success:" marker, other digits present.
            var ex = new AggregateException(new HttpRequestException("Le code d'etat de la reponse (1.1) n'indique pas une reussite : 509 (Bandwidth Limit Exceeded)."));
            bool transient;
            Assert.AreEqual(LauncherError.SourceQuotaExceeded, Map(ex, out transient));
            Assert.IsFalse(transient);
        }

        [TestMethod]
        public void StatusScan_PrefersQuotaThenServerErrors()
        {
            Assert.AreEqual(509, MegaErrorMapper.StatusOf(new HttpRequestException("Fehler 404 ... 509")));
            Assert.AreEqual(429, MegaErrorMapper.StatusOf(new HttpRequestException("Fehler 503 ... 429")));
            Assert.AreEqual(503, MegaErrorMapper.StatusOf(new HttpRequestException("Fehler 404 ... 503")));
            Assert.AreEqual(404, MegaErrorMapper.StatusOf(new HttpRequestException("Fehler 404")));
            Assert.IsNull(MegaErrorMapper.StatusOf(new HttpRequestException("An error occurred while sending the request.")));
        }

        [TestMethod]
        public void InvalidLink_IsManifestInvalid()
        {
            bool transient;
            Assert.AreEqual(LauncherError.ManifestInvalid, Map(new ArgumentException("Invalid uri"), out transient));
            Assert.IsFalse(transient);
        }

        /// <summary>
        /// Pins the real .NET Framework message format the 509 detection parses: MegaApiClient's
        /// data fetch is <c>HttpClient.GetStreamAsync(url).Result</c>, so reproduce exactly that
        /// against a loopback server answering 509.
        /// </summary>
        [TestMethod]
        public void RealNet48Http509_FromGetStreamAsyncResult_MapsToQuotaExceeded()
        {
            using (var server = new FakeHttpServer((ctx, i) => Serve.StatusAsync(ctx, 509, description: "Bandwidth Limit Exceeded")))
            using (var client = new HttpClient())
            {
                Exception caught = null;
                try
                {
                    Task.Run(() => client.GetStreamAsync(server.FileUri).Result).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    caught = ex;
                }

                Assert.IsNotNull(caught);
                Assert.IsInstanceOfType(caught, typeof(AggregateException));
                bool transient;
                LauncherException mapped = MegaErrorMapper.Map(caught, out transient);
                Assert.AreEqual(LauncherError.SourceQuotaExceeded, mapped.Code, "inner message: " + ((AggregateException)caught).InnerException.Message);
                Assert.IsFalse(transient);
            }
        }

        [TestMethod]
        public void RealNet48Http503_FromGetStreamAsyncResult_IsTransientSourceError()
        {
            using (var server = new FakeHttpServer((ctx, i) => Serve.StatusAsync(ctx, 503)))
            using (var client = new HttpClient())
            {
                Exception caught = null;
                try
                {
                    Task.Run(() => client.GetStreamAsync(server.FileUri).Result).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    caught = ex;
                }

                bool transient;
                Assert.AreEqual(LauncherError.SourceHttpError, MegaErrorMapper.Map(caught, out transient).Code);
                Assert.IsTrue(transient);
            }
        }
    }
}
