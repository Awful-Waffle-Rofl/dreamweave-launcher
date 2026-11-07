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
    }
}
