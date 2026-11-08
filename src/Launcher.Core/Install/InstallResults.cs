using System;
using System.Collections.Generic;

namespace DreamWeave.Launcher.Core.Install
{
    /// <summary>How an install step ended.</summary>
    public enum InstallOutcome
    {
        /// <summary>The game folder was changed as requested.</summary>
        Completed = 0,

        /// <summary>Everything was already in the requested state; nothing was written.</summary>
        NothingToDo = 1,

        /// <summary>
        /// The current user cannot write the game folder. Nothing in it was changed; the verified
        /// files wait in the component's <c>ready</c> folder, and the caller runs the launcher's
        /// <c>--place-verified</c> mode elevated for the result's <see cref="PlacementRequest"/>.
        /// </summary>
        ElevationRequired = 2,
    }

    /// <summary>
    /// What the elevated helper is asked to do: place the verified files staged for one component
    /// into one game folder. Deliberately carries NO file list, hashes, sizes or operation: the
    /// helper finds the staged files at a fixed path (<see cref="ReadyFolder"/>) and takes every
    /// integrity fact from the signed manifest it verifies itself.
    /// </summary>
    public sealed class PlacementRequest
    {
        /// <summary>Creates a request.</summary>
        /// <param name="appRoot"><see cref="AppPaths.Root"/> of the launcher that staged the files (locates its manifest cache and staging).</param>
        /// <param name="acFolder">The game folder to write into.</param>
        /// <param name="componentId">The manifest component whose staged files are placed.</param>
        public PlacementRequest(string appRoot, string acFolder, string componentId)
        {
            AppRoot = appRoot ?? throw new ArgumentNullException(nameof(appRoot));
            AcFolder = acFolder ?? throw new ArgumentNullException(nameof(acFolder));
            ComponentId = componentId ?? throw new ArgumentNullException(nameof(componentId));
        }

        /// <summary>The launcher data root (an over-the-shoulder administrator still finds the player's data).</summary>
        public string AppRoot { get; }

        /// <summary>The game folder.</summary>
        public string AcFolder { get; }

        /// <summary>The manifest component id.</summary>
        public string ComponentId { get; }

        /// <summary>Where the verified files wait: <c>StagingDir\&lt;componentId&gt;\ready</c>.</summary>
        public string ReadyFolder => VerifiedPlacementRunner.ReadyFolder(new AppPaths(AppRoot), ComponentId);

        /// <summary>
        /// After a SUCCESSFUL placement: deletes the ready folder. After a failure the caller keeps it,
        /// so a retry reuses the verified files instead of extracting them again.
        /// </summary>
        public void CleanUp()
        {
            // Links first, so a junction planted in the folder is removed itself and the delete
            // neither reaches through it nor stops on it and leaves the staged files behind.
            try
            {
                VerifiedPlacementRunner.RemoveLinks(ReadyFolder);
            }
            catch (LauncherException)
            {
                // Best effort, as the delete below.
            }

            AcWriter.TryDeleteFolder(ReadyFolder);
        }
    }

    /// <summary>Result of <see cref="ZipOverlayInstaller.Install"/>.</summary>
    public sealed class ZipOverlayResult
    {
        internal ZipOverlayResult(InstallOutcome outcome, IReadOnlyList<string> written, IReadOnlyList<string> skipped, IReadOnlyList<string> unexpectedEntries, PlacementRequest placement)
        {
            Outcome = outcome;
            Written = written;
            Skipped = skipped;
            UnexpectedEntries = unexpectedEntries;
            Placement = placement;
        }

        /// <summary>How it ended.</summary>
        public InstallOutcome Outcome { get; }

        /// <summary>Manifest paths written into the game folder.</summary>
        public IReadOnlyList<string> Written { get; }

        /// <summary>Manifest paths left alone because they were already correct.</summary>
        public IReadOnlyList<string> Skipped { get; }

        /// <summary>Archive entries that match no manifest file (not extracted; log them). Empty when the archive was not opened.</summary>
        public IReadOnlyList<string> UnexpectedEntries { get; }

        /// <summary>ElevationRequired only: what the elevated helper must place.</summary>
        public PlacementRequest Placement { get; }

        /// <summary>After a successful elevated placement: deletes the staged files (a no-op without a placement).</summary>
        public void CleanUp()
        {
            Placement?.CleanUp();
        }
    }

    /// <summary>Result of <see cref="BytePatcher.Apply"/> / <see cref="BytePatcher.Revert"/>.</summary>
    public sealed class BytePatchResult
    {
        internal BytePatchResult(InstallOutcome outcome, PlacementRequest placement)
        {
            Outcome = outcome;
            Placement = placement;
        }

        /// <summary>How it ended.</summary>
        public InstallOutcome Outcome { get; }

        /// <summary>ElevationRequired only: what the elevated helper must place (the staged, already patched or reverted target).</summary>
        public PlacementRequest Placement { get; }

        /// <summary>After a successful elevated placement: deletes the staged file (a no-op without a placement).</summary>
        public void CleanUp()
        {
            Placement?.CleanUp();
        }
    }
}
