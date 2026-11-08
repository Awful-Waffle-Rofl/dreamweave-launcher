using System;
using System.Linq;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Telemetry;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Decal
{
    [TestClass]
    public class DecalTelemetryAndErrorTests
    {
        [TestMethod]
        public void LaunchDecalStep_IsKnownAndValid()
        {
            Assert.AreEqual("launch-decal", TelemetrySteps.LaunchDecal);
            Assert.IsTrue(TelemetrySteps.IsKnown(TelemetrySteps.LaunchDecal));
            Assert.IsTrue(TelemetryEvent.IsStepValid(TelemetrySteps.LaunchDecal));
        }

        [TestMethod]
        public void ErrorTable_CoversTheNewDecalCodes()
        {
            Assert.IsTrue(ErrorMessages.Has(LauncherError.DecalInjectFailed));
            Assert.IsTrue(ErrorMessages.Has(LauncherError.DecalNotFound));
            Assert.AreEqual(75, (int)LauncherError.DecalInjectFailed);
            Assert.AreEqual(76, (int)LauncherError.DecalNotFound);
            Assert.IsTrue(ErrorMessages.Has(LauncherError.DecalHelperFailed));
            Assert.AreEqual(77, (int)LauncherError.DecalHelperFailed);
        }

        [TestMethod]
        public void HelperTimeoutMessage_IsDedicated_AndDoesNotSendThePlayerToVerifyFiles()
        {
            Assert.AreEqual(78, (int)LauncherError.DecalHelperTimeout);
            Assert.IsTrue(ErrorMessages.Has(LauncherError.DecalHelperTimeout));
            string text = ErrorMessages.For(LauncherError.DecalHelperTimeout);

            Assert.AreEqual("Decal is taking too long to start the game. Check whether Asheron's Call opened before trying again.", text);
            Assert.IsFalse(text.Contains("Verify files"), text);
        }

        [TestMethod]
        public void HelperFailedMessage_DoesNotClaimTheGameStarted()
        {
            string text = ErrorMessages.For(LauncherError.DecalHelperFailed);

            Assert.IsFalse(text.Contains("game started"), text);
            Assert.IsFalse(text.Contains("started, but"), text);
        }

        [TestMethod]
        public void EveryErrorCode_StillHasAMessage()
        {
            var missing = Enum.GetValues(typeof(LauncherError)).Cast<LauncherError>().Where(e => !ErrorMessages.Has(e)).ToList();
            Assert.AreEqual(0, missing.Count, "codes without a message: " + string.Join(", ", missing));
        }
    }
}
