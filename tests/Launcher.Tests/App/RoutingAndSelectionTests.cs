using System.Linq;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core.Detection;
using DreamWeave.Launcher.Core.Manifests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.App
{
    [TestClass]
    public class RouteDeciderTests
    {
        [TestMethod]
        public void AllRequiredSatisfied_GoesToLauncher_EvenWithOptionalMissing()
        {
            LauncherManifest m = AppFixtures.Sample();
            var statuses = new[]
            {
                AppFixtures.Status("ac-client", ComponentState.Satisfied),
                AppFixtures.Status("eor-dats", ComponentState.Satisfied),
                AppFixtures.Status("palette-leak-fix", ComponentState.Missing),
            };
            Assert.AreEqual(StartRoute.Launcher, RouteDecider.Decide(m, statuses));
        }

        [TestMethod]
        public void AnyRequiredNotSatisfied_GoesToWizard()
        {
            LauncherManifest m = AppFixtures.Sample();
            foreach (ComponentState bad in new[] { ComponentState.Missing, ComponentState.NeedsRepair, ComponentState.Unknown, ComponentState.NotApplicable })
            {
                var statuses = new[]
                {
                    AppFixtures.Status("ac-client", ComponentState.Satisfied),
                    AppFixtures.Status("eor-dats", bad),
                };
                Assert.AreEqual(StartRoute.Wizard, RouteDecider.Decide(m, statuses), bad.ToString());
            }
        }

        [TestMethod]
        public void RequiredWithNoStatus_GoesToWizard_AndNoManifest_IsNoManifest()
        {
            LauncherManifest m = AppFixtures.Sample();
            Assert.AreEqual(StartRoute.Wizard, RouteDecider.Decide(m, new[] { AppFixtures.Status("ac-client", ComponentState.Satisfied) }));
            Assert.AreEqual(StartRoute.Wizard, RouteDecider.Decide(m, null));
            Assert.AreEqual(StartRoute.NoManifest, RouteDecider.Decide(null, null));
        }
    }

    [TestClass]
    public class ComponentSelectionTests
    {
        private static ComponentSelection FreshInstall()
        {
            return new ComponentSelection(AppFixtures.Sample(), new[]
            {
                AppFixtures.Status("ac-client", ComponentState.Missing),
                AppFixtures.Status("eor-dats", ComponentState.Missing),
                AppFixtures.Status("palette-leak-fix", ComponentState.NotApplicable),
            });
        }

        [TestMethod]
        public void OptionalComponent_DefaultsOff_EvenWhenManifestSaysDefaultSelected()
        {
            ComponentSelection s = FreshInstall();
            ComponentRow patch = s.Rows.Single(r => r.Id == "palette-leak-fix");
            Assert.IsFalse(patch.Checked);
            Assert.IsTrue(patch.CanToggle);
            Assert.IsNull(patch.Note, "the source label has its own line; the manifest has no description to repeat");

            var selected = new ComponentSelection(AppFixtures.Manifest(AppFixtures.Installer(), AppFixtures.Overlay(), AppFixtures.Patch(defaultSelected: true)), new[]
            {
                AppFixtures.Status("ac-client", ComponentState.Missing),
                AppFixtures.Status("eor-dats", ComponentState.Missing),
                AppFixtures.Status("palette-leak-fix", ComponentState.Missing),
            });
            Assert.IsFalse(selected.Rows.Single(r => r.Id == "palette-leak-fix").Checked);
        }

        [TestMethod]
        public void FreshInstall_RequiredChecked_NextAllowed()
        {
            ComponentSelection s = FreshInstall();
            Assert.IsTrue(s.Rows.Single(r => r.Id == "ac-client").Checked);
            Assert.IsTrue(s.Rows.Single(r => r.Id == "eor-dats").Checked);
            Assert.IsTrue(s.CanContinue);
            CollectionAssert.AreEqual(new[] { "ac-client", "eor-dats" }, s.Selected.Select(r => r.Id).ToArray());
            Assert.AreEqual("Downloaded from: Original Turbine installer", s.Rows[0].SourceText);
        }

        [TestMethod]
        public void UncheckingRequiredComponent_BlocksNext_WithReason()
        {
            ComponentSelection s = FreshInstall();
            s.SetChecked("eor-dats", false);
            Assert.IsFalse(s.CanContinue);
            StringAssert.Contains(s.NextBlockedReason, "End-of-Retail game data");
            s.SetChecked("eor-dats", true);
            Assert.IsTrue(s.CanContinue);
        }

        [TestMethod]
        public void SatisfiedRows_AreDone_CheckedAndNotToggleable()
        {
            var s = new ComponentSelection(AppFixtures.Sample(), new[]
            {
                AppFixtures.Status("ac-client", ComponentState.Satisfied),
                AppFixtures.Status("eor-dats", ComponentState.NeedsRepair, AppFixtures.File("client_portal.dat", FileState.Mismatch)),
                AppFixtures.Status("palette-leak-fix", ComponentState.Missing),
            });
            ComponentRow client = s.Rows.Single(r => r.Id == "ac-client");
            Assert.AreEqual(RowStatus.Done, client.RowStatus);
            Assert.IsTrue(client.Checked);
            Assert.IsFalse(client.CanToggle);
            s.SetChecked("ac-client", false);
            Assert.IsTrue(client.Checked, "a Done row ignores toggles");
            Assert.IsTrue(s.Rows.Single(r => r.Id == "eor-dats").Checked, "a required repair starts checked");
            CollectionAssert.AreEqual(new[] { "eor-dats" }, s.Selected.Select(r => r.Id).ToArray());
        }

        [TestMethod]
        public void ClientExeHashOnlyMismatch_InstallerNotAutoChecked_ShowsC1_AndDoesNotBlock()
        {
            var s = new ComponentSelection(AppFixtures.Sample(), new[]
            {
                AppFixtures.Status("ac-client", ComponentState.NeedsRepair, AppFixtures.File("acclient.exe", FileState.Mismatch)),
                AppFixtures.Status("eor-dats", ComponentState.NeedsRepair, AppFixtures.File("acclient.exe", FileState.Mismatch)),
                AppFixtures.Status("palette-leak-fix", ComponentState.NotApplicable),
            });
            ComponentRow client = s.Rows.Single(r => r.Id == "ac-client");
            Assert.IsFalse(client.Checked, "the 600 MB installer must not be auto-checked for an exe-only mismatch");
            Assert.IsTrue(client.CoveredByOverlay);
            Assert.AreEqual(UiText.ClientExeMismatch, client.Note);
            Assert.IsTrue(s.CanContinue, "the overlay replaces acclient.exe, so the unchecked installer does not block");
            CollectionAssert.AreEqual(new[] { "eor-dats" }, s.Selected.Select(r => r.Id).ToArray());

            s.SetChecked("eor-dats", false);
            Assert.IsFalse(s.CanContinue, "with the overlay unticked nothing would fix acclient.exe");
        }

        [TestMethod]
        public void ClientExeMismatch_WithoutCoveringOverlay_IsAutoChecked()
        {
            LauncherManifest m = AppFixtures.Manifest(AppFixtures.Installer(), AppFixtures.Overlay(includesClientExe: false));
            var s = new ComponentSelection(m, new[]
            {
                AppFixtures.Status("ac-client", ComponentState.NeedsRepair, AppFixtures.File("acclient.exe", FileState.Mismatch)),
                AppFixtures.Status("eor-dats", ComponentState.Satisfied),
            });
            ComponentRow client = s.Rows.Single(r => r.Id == "ac-client");
            Assert.IsFalse(client.CoveredByOverlay);
            Assert.IsTrue(client.Checked);
            Assert.IsNull(client.Note);
        }

        [TestMethod]
        public void UnreadableRequiredComponent_BlocksWithCheckAgainText()
        {
            var s = new ComponentSelection(AppFixtures.Sample(), new[]
            {
                AppFixtures.Status("ac-client", ComponentState.Satisfied),
                AppFixtures.Status("eor-dats", ComponentState.Unknown, AppFixtures.File("client_portal.dat", FileState.Locked)),
            });
            Assert.IsFalse(s.CanContinue);
            Assert.AreEqual("\"End-of-Retail game data\" could not be checked, usually because Asheron's Call is running. Close the game, then click Check again.", s.NextBlockedReason);
            Assert.IsTrue(s.OffersCheckAgain);
            s.SetChecked("eor-dats", true);
            Assert.IsTrue(s.CanContinue, "ticking it (a repair) unblocks");
            Assert.IsFalse(s.OffersCheckAgain);
        }

        [TestMethod]
        public void SkippedRequiredKind_OrDownloadsNotAllowed_BlocksNext()
        {
            var statuses = new[]
            {
                AppFixtures.Status("ac-client", ComponentState.Missing),
                AppFixtures.Status("eor-dats", ComponentState.Missing),
            };
            Assert.IsFalse(new ComponentSelection(AppFixtures.Sample(), statuses, downloadsAllowed: false).CanContinue);

            LauncherManifest m = AppFixtures.Sample();
            m.SkippedRequired = new[] { "future-kind" };
            Assert.AreEqual(UiText.NeedsNewerLauncher, new ComponentSelection(m, statuses).NextBlockedReason);
        }
    }
}
