using System;
using DreamWeave.Launcher.Core.Telemetry;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Telemetry
{
    [TestClass]
    public class TelemetryEndpointTests
    {
        private const string Ok = "https://www.example.test/tools/api/launcher/v1/events";

        [TestMethod]
        public void SameRegistrableDomain_IsAccepted()
        {
            Assert.IsNotNull(TelemetryEndpoint.Validate(Ok, "play.example.test", null));
            Assert.IsNotNull(TelemetryEndpoint.Validate("https://EXAMPLE.test/e", "play.example.test", null));
        }

        [TestMethod]
        public void OtherDomain_IsRefused_UnlessAllowListed()
        {
            const string other = "https://telemetry.other.test/e";
            Assert.IsNull(TelemetryEndpoint.Validate(other, "play.example.test", null));
            Assert.IsNull(TelemetryEndpoint.Validate(other, "play.example.test", new[] { "unrelated.test" }));
            Assert.IsNotNull(TelemetryEndpoint.Validate(other, "play.example.test", new[] { "Telemetry.Other.Test" }));
        }

        [TestMethod]
        public void LookalikeSuffix_IsRefused()
        {
            Assert.IsNull(TelemetryEndpoint.Validate("https://www.example.test.evil.test/e", "play.example.test", null));
            Assert.IsNull(TelemetryEndpoint.Validate("https://notexample.test/e", "play.example.test", null));
        }

        [TestMethod]
        public void TwoPartSuffix_IsHandled()
        {
            Assert.IsNotNull(TelemetryEndpoint.Validate("https://www.game.co.uk/e", "play.game.co.uk", null));
            Assert.IsNull(TelemetryEndpoint.Validate("https://www.other.co.uk/e", "play.game.co.uk", null));
        }

        [TestMethod]
        public void NotHttps_Query_Fragment_UserInfo_Port_AreRefused()
        {
            Assert.IsNull(TelemetryEndpoint.Validate("http://www.example.test/e", "play.example.test", null));
            Assert.IsNull(TelemetryEndpoint.Validate(Ok + "?x=1", "play.example.test", null));
            Assert.IsNull(TelemetryEndpoint.Validate(Ok + "#f", "play.example.test", null));
            Assert.IsNull(TelemetryEndpoint.Validate("https://u:p@www.example.test/e", "play.example.test", null));
            Assert.IsNull(TelemetryEndpoint.Validate("https://www.example.test:8443/e", "play.example.test", null));
        }

        [TestMethod]
        public void NullEmptyRelative_AreRefused()
        {
            Assert.IsNull(TelemetryEndpoint.Validate(null, "play.example.test", null));
            Assert.IsNull(TelemetryEndpoint.Validate("  ", "play.example.test", null));
            Assert.IsNull(TelemetryEndpoint.Validate("/tools/api", "play.example.test", null));
        }

        [TestMethod]
        public void IpAddresses_AreNeverAcceptedAsEndpoints_AndNeedTheAllowListAsServerHosts()
        {
            // An IP endpoint is never accepted, even when the server host is the same IP.
            Assert.IsNull(TelemetryEndpoint.Validate("https://203.0.113.5/e", "203.0.113.5", new[] { "203.0.113.5" }));
            // An IP server host has no registrable domain, so only the allow list can authorise.
            Assert.IsNull(TelemetryEndpoint.Validate(Ok, "203.0.113.5", null));
            Assert.IsNotNull(TelemetryEndpoint.Validate(Ok, "203.0.113.5", new[] { "www.example.test" }));
        }

        [TestMethod]
        public void LoopbackHttp_OnlyWithTestSwitch()
        {
            Assert.IsNull(TelemetryEndpoint.Validate("http://127.0.0.1:5000/e", "play.example.test", null));
            Assert.IsNotNull(TelemetryEndpoint.Validate("http://127.0.0.1:5000/e", "play.example.test", null, true));
            Assert.IsNull(TelemetryEndpoint.Validate("http://example.test/e", "play.example.test", null, true));
        }

        [TestMethod]
        public void ReviewProbeUrls_NeverResolveOutsideTheServerDomain()
        {
            const string srv = "play.acdreamweave.com";
            foreach (string e in new[]
            {
                "https://evil.com.acdreamweave.com.attacker.net/e", "https://acdreamweave.com.evil.tld/e",
                "https://ACDREAMWEAVE.COM/e", "https://www.acdreamweave.com./e", "https://www.acdreamweave.com.:443/e",
                "https://www.acdr\u0435amweave.com/e", "https://www.xn--acdreamweave-x.com/e", "https://xn--acdreamweave.com/e",
                "https://www.acdreamweave.com\\@evil.com/e", "https://evil.com#@www.acdreamweave.com/e", "https://www.acdreamweave.com:443/e",
                "https://www.acdreamweave.com/e?", "https://%61cdreamweave.com/e", "https://a.b.acdreamweave.com/e",
            })
            {
                Uri u = TelemetryEndpoint.Validate(e, srv, null);
                if (u != null)
                {
                    string host = u.IdnHost;
                    Assert.IsTrue(host == "acdreamweave.com" || host.EndsWith(".acdreamweave.com", StringComparison.Ordinal), e + " resolved to " + host);
                }
            }
        }

        [TestMethod]
        public void TrailingDot_Punycode_AndCase()
        {
            const string srv = "play.acdreamweave.com";
            Assert.IsNull(TelemetryEndpoint.Validate("https://www.acdreamweave.com./e", srv, null), "trailing dot on the endpoint is refused");
            Assert.IsNull(TelemetryEndpoint.Validate("https://www.xn--acdreamweave-x.com/e", srv, null));
            Assert.IsNull(TelemetryEndpoint.Validate("https://www.acdr\u0435amweave.com/e", srv, null), "cyrillic look-alike");
            Assert.IsNotNull(TelemetryEndpoint.Validate("https://ACDREAMWEAVE.COM/e", srv, null));
            Assert.IsNotNull(TelemetryEndpoint.Validate("https://www.acdreamweave.com/e", "PLAY.ACDREAMWEAVE.COM", null), "upper-case server host");
            Assert.IsNotNull(TelemetryEndpoint.Validate("https://www.acdreamweave.com/e", "play.acdreamweave.com.", null), "trailing dot on the server host is trimmed");
            Assert.IsNotNull(TelemetryEndpoint.Validate("https://www.acdreamweave.com/e", "play.acdreamweave.com:9000", null));
            Assert.IsNotNull(TelemetryEndpoint.Validate("https://www.b\u00fccher.example/e", "play.b\u00fccher.example", null), "an IDN host matches itself in punycode form");
        }

        [TestMethod]
        public void SharedHostingDomains_NeverMatchByDomain_ButCanBeAllowListed()
        {
            Assert.IsNull(TelemetryEndpoint.Validate("https://x.duckdns.org/e", "mine.duckdns.org", null));
            Assert.IsNull(TelemetryEndpoint.Validate("https://x.github.io/e", "mine.github.io", null));
            Assert.IsNull(TelemetryEndpoint.Validate("https://x.herokuapp.com/e", "mine.herokuapp.com", null));
            Assert.IsNull(TelemetryEndpoint.Validate("https://x.ddns.net/e", "mine.ddns.net", null));
            Assert.IsNull(TelemetryEndpoint.Validate("https://x.no-ip.org/e", "mine.no-ip.org", null));
            Assert.IsNotNull(TelemetryEndpoint.Validate("https://x.duckdns.org/e", "mine.duckdns.org", new[] { "x.duckdns.org" }));
        }

        [TestMethod]
        public void MoreTwoPartSuffixes_AreHandled()
        {
            Assert.IsNull(TelemetryEndpoint.Validate("https://evil.com.ng/e", "play.my.com.ng", null));
            Assert.IsNotNull(TelemetryEndpoint.Validate("https://www.my.com.ng/e", "play.my.com.ng", null));
            Assert.IsNull(TelemetryEndpoint.Validate("https://www.other.com.ph/e", "play.my.com.ph", null));
        }
    }
}
