using System.Collections.Generic;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.SelfInstall;

namespace DreamWeave.Launcher.App
{
    /// <summary>
    /// The text the uninstall window shows for a result. Built only from the result's STRUCTURED fields
    /// (code, leftover folder paths, the credentials flag, the client outcome): never from
    /// <see cref="UninstallResult.Failures"/>, whose lines carry exception text and account names.
    /// </summary>
    internal static class UninstallReport
    {
        /// <summary>True when there is nothing to tell the player and the window should close at once.</summary>
        public static bool CloseImmediately(UninstallResult result)
        {
            return result != null
                && result.Complete
                && result.AcClient != AcClientUninstallOutcome.NotFound
                && result.AcClient != AcClientUninstallOutcome.Declined
                && result.AcClient != AcClientUninstallOutcome.Failed;
        }

        /// <summary>The lines to show, in order.</summary>
        public static IReadOnlyList<string> Lines(UninstallResult result)
        {
            var lines = new List<string>();
            if (result == null)
            {
                lines.Add(ErrorMessages.For(LauncherError.Unknown));
                return lines;
            }

            if (result.Complete)
            {
                lines.Add(UiText.UninstallDone);
            }
            else
            {
                lines.Add(ErrorMessages.For(result.Code));
                lines.Add(UiText.UninstallLeftOver);
                lines.AddRange(result.LeftoverFolders);
                if (result.CredentialsLeftOver)
                {
                    lines.Add(UiText.UninstallCredentialsLeft);
                }
            }

            switch (result.AcClient)
            {
                case AcClientUninstallOutcome.NotFound:
                    lines.Add(UiText.UninstallClientNotFound);
                    break;
                case AcClientUninstallOutcome.Declined:
                    lines.Add(UiText.UninstallClientDeclined);
                    break;
                case AcClientUninstallOutcome.Failed:
                    lines.Add(UiText.UninstallClientFailed);
                    break;
            }

            return lines;
        }
    }
}
