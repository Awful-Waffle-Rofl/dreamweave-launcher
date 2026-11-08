using DreamWeave.Launcher.App;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.App
{
    /// <summary>Owner-approved wording of the source disclosure and the self-install prompt.</summary>
    [TestClass]
    public class UiTextSourceTests
    {
        [TestMethod]
        public void WelcomeSources_NamesMegaAsTheSource_AndTheArchiveAsTheInstallersBackup()
        {
            Assert.AreEqual(
                "It downloads the original game installer and the final game data files from community uploads on mega.nz. The installer also has a backup copy on the Internet Archive. Nothing is downloaded until you choose it on the next screens.",
                UiText.WelcomeSources);
        }

        [TestMethod]
        public void SelfInstallPrompt_EndsAtAppsAndFeatures()
        {
            Assert.AreEqual(
                "Install DreamWeave Launcher on this PC? It adds a Start menu shortcut and an entry in Apps and Features.",
                UiText.SelfInstallPrompt);
        }
    }
}
