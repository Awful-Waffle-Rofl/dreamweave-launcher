using System;
using System.Threading;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.App
{
    /// <summary>R1/R2: a Stop or Cancel never cancels a write into the game folder.</summary>
    [TestClass]
    public class InstallStopGateTests
    {
        [TestMethod]
        public void StopOrCancel_WhileCritical_LeavesTheTokenUncancelled()
        {
            var gate = new InstallStopGate();
            using (var cts = new CancellationTokenSource())
            {
                gate.SetPhase(InstallPhase.Stoppable);
                gate.EnterCritical(cts.Token);
                Assert.AreEqual(InstallPhase.Critical, gate.Phase);

                // Cancel button, then the "Stop the install?" answer: both refused.
                Assert.AreEqual(StopOutcome.Blocked, gate.RequestStop(cts));
                Assert.AreEqual(StopOutcome.Blocked, gate.RequestStop(cts));
                Assert.IsFalse(cts.IsCancellationRequested, "a stop during the commit must not cancel it");
                Assert.AreEqual(CloseDecision.Block, InstallCloseRules.Decide(gate.Phase));

                // Once the commit is over, the same request stops the run.
                gate.SetPhase(InstallPhase.Stoppable);
                Assert.AreEqual(StopOutcome.Stopping, gate.RequestStop(cts));
                Assert.IsTrue(cts.IsCancellationRequested);
            }
        }

        [TestMethod]
        public void StopFirst_RefusesTheCommit()
        {
            var gate = new InstallStopGate();
            using (var cts = new CancellationTokenSource())
            {
                gate.SetPhase(InstallPhase.Stoppable);
                Assert.AreEqual(StopOutcome.Stopping, gate.RequestStop(cts));
                var ex = Assert.ThrowsExactly<LauncherException>(() => gate.EnterCritical(cts.Token));
                Assert.AreEqual(LauncherError.Cancelled, ex.Code);
                Assert.AreEqual(InstallPhase.Stoppable, gate.Phase, "a refused commit never becomes Critical");
            }
        }

        [TestMethod]
        public void StopAfterTheRunReturned_IsNotRunning()
        {
            var gate = new InstallStopGate();
            using (var cts = new CancellationTokenSource())
            {
                Assert.AreEqual(StopOutcome.NotRunning, gate.RequestStop(cts));
                Assert.AreEqual(StopOutcome.NotRunning, gate.RequestStop(null));
            }
        }

        [TestMethod]
        public void CriticalIsEnteredOnlyThroughTheGate()
        {
            Assert.ThrowsExactly<ArgumentException>(() => new InstallStopGate().SetPhase(InstallPhase.Critical));
        }
    }
}
