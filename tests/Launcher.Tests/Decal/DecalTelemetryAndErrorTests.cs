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
        }

        [TestMethod]
        public void EveryErrorCode_StillHasAMessage()
        {
            var missing = Enum.GetValues(typeof(LauncherError)).Cast<LauncherError>().Where(e => !ErrorMessages.Has(e)).ToList();
            Assert.AreEqual(0, missing.Count, "codes without a message: " + string.Join(", ", missing));
        }
    }
}
