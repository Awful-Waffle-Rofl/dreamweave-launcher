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
            foreach (Exception ex in new Exception[] { new IOException("reset"), new WebException("dns"), new AggregateException(new HttpRequestException("An error occurred while sending the request.", new WebException("dns"))) })
            {
                bool transient;
                Assert.AreEqual(LauncherError.NetworkUnavailable, Map(ex, out transient), ex.GetType().Name);
                Assert.IsTrue(transient);
            }
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
