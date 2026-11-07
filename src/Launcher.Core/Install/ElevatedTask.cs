using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

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
        /// The current user cannot write the game folder. Nothing in it was changed; the caller runs
        /// the launcher's <c>--elevated-copy &lt;TaskPath&gt;</c> mode elevated, then cleans up.
        /// </summary>
        ElevationRequired = 2,
    }

    /// <summary>The operation an <see cref="ElevatedTaskItem"/> asks for.</summary>
    public static class ElevatedOperations
    {
        /// <summary>Copy staged zip-overlay files into the game folder.</summary>
        public const string Overlay = "overlay";

        /// <summary>Apply a byte patch.</summary>
        public const string PatchApply = "patch-apply";

        /// <summary>Revert a byte patch.</summary>
        public const string PatchRevert = "patch-revert";
    }

    /// <summary>
    /// The work handed to the elevated helper. Deliberately carries NO hashes, sizes or bytes: only
    /// where things are (the launcher data root, so the helper finds the cached signed manifest; the
    /// game folder; staged file paths) and what to do with which component id. The helper takes
    /// every integrity fact from the manifest it verifies itself and re-hashes every staged file.
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class ElevatedTask
    {
        /// <summary>The only task format this launcher reads.</summary>
        public const string CurrentFormat = "dreamweave-elevated-task-1";

        /// <summary>Hard cap on a task file.</summary>
        public const int MaxFileBytes = 64 * 1024;

        /// <summary>Format marker; must equal <see cref="CurrentFormat"/>.</summary>
        [JsonProperty("format")]
        public string Format { get; set; } = CurrentFormat;

        /// <summary><see cref="AppPaths.Root"/> of the launcher that wrote the task (locates its manifest cache).</summary>
        [JsonProperty("appRoot")]
        public string AppRoot { get; set; }

        /// <summary>The game folder to write into.</summary>
        [JsonProperty("acFolder")]
        public string AcFolder { get; set; }

        /// <summary>Work items, in order.</summary>
        [JsonProperty("items")]
        public List<ElevatedTaskItem> Items { get; set; } = new List<ElevatedTaskItem>();

        /// <summary>Writes the task atomically to a new file in <see cref="AppPaths.StagingDir"/> and returns its path.</summary>
        public string Save(AppPaths paths)
        {
            if (paths == null)
            {
                throw new ArgumentNullException(nameof(paths));
            }

            string path = Path.Combine(paths.StagingDir, "elevated-task-" + Guid.NewGuid().ToString("N") + ".json");
            AtomicFile.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(this, JsonDefaults.Create())));
            return path;
        }

        /// <summary>Reads a task file (size-capped, strict format).</summary>
        /// <exception cref="LauncherException">ElevatedTaskInvalid.</exception>
        public static ElevatedTask Load(string path)
        {
            byte[] bytes;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    throw new LauncherException(LauncherError.ElevatedTaskInvalid, "The elevated task file does not exist.");
                }

                if (info.Length > MaxFileBytes)
                {
                    throw new LauncherException(LauncherError.ElevatedTaskInvalid, "The elevated task file is too large.");
                }

                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                throw new LauncherException(LauncherError.ElevatedTaskInvalid, "The elevated task file could not be read: " + ex.Message, ex);
            }

            ElevatedTask task;
            try
            {
                task = JsonConvert.DeserializeObject<ElevatedTask>(new UTF8Encoding(false, true).GetString(bytes), JsonDefaults.Create());
            }
            catch (Exception ex) when (ex is JsonException || ex is DecoderFallbackException || ex is ArgumentException)
            {
                throw new LauncherException(LauncherError.ElevatedTaskInvalid, "The elevated task file is not valid: " + ex.Message, ex);
            }

            if (task == null || task.Format != CurrentFormat)
            {
                throw new LauncherException(LauncherError.ElevatedTaskInvalid, "The elevated task file has an unknown format.");
            }

            if (string.IsNullOrWhiteSpace(task.AppRoot) || !Path.IsPathRooted(task.AppRoot)
                || string.IsNullOrWhiteSpace(task.AcFolder) || !Path.IsPathRooted(task.AcFolder)
                || task.Items == null || task.Items.Count == 0)
            {
                throw new LauncherException(LauncherError.ElevatedTaskInvalid, "The elevated task file is incomplete.");
            }

            return task;
        }
    }

    /// <summary>One component's worth of elevated work.</summary>
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class ElevatedTaskItem
    {
        /// <summary>Manifest component id.</summary>
        [JsonProperty("componentId")]
        public string ComponentId { get; set; }

        /// <summary>One of <see cref="ElevatedOperations"/>.</summary>
        [JsonProperty("operation")]
        public string Operation { get; set; }

        /// <summary>Overlay only: staged files to copy.</summary>
        [JsonProperty("files")]
        public List<ElevatedTaskFile> Files { get; set; } = new List<ElevatedTaskFile>();
    }

    /// <summary>A staged file and the manifest path it is for.</summary>
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class ElevatedTaskFile
    {
        /// <summary>Manifest-relative target path (must match a <c>files[].path</c> of the component).</summary>
        [JsonProperty("path")]
        public string Path { get; set; }

        /// <summary>Absolute path of the staged copy.</summary>
        [JsonProperty("staged")]
        public string Staged { get; set; }
    }

    /// <summary>Result of <see cref="ZipOverlayInstaller.Install"/>.</summary>
    public sealed class ZipOverlayResult
    {
        internal ZipOverlayResult(InstallOutcome outcome, IReadOnlyList<string> written, IReadOnlyList<string> skipped, IReadOnlyList<string> unexpectedEntries, ElevatedTask task, string taskPath, string stagingFolder)
        {
            Outcome = outcome;
            Written = written;
            Skipped = skipped;
            UnexpectedEntries = unexpectedEntries;
            Task = task;
            TaskPath = taskPath;
            StagingFolder = stagingFolder;
        }

        /// <summary>How it ended.</summary>
        public InstallOutcome Outcome { get; }

        /// <summary>Manifest paths written into the game folder.</summary>
        public IReadOnlyList<string> Written { get; }

        /// <summary>Manifest paths left alone because they were already correct.</summary>
        public IReadOnlyList<string> Skipped { get; }

        /// <summary>Archive entries that match no manifest file (not extracted; log them). Empty when the archive was not opened.</summary>
        public IReadOnlyList<string> UnexpectedEntries { get; }

        /// <summary>ElevationRequired only: the task handed to the elevated helper.</summary>
        public ElevatedTask Task { get; }

        /// <summary>ElevationRequired only: the saved task file (argument of <c>--elevated-copy</c>).</summary>
        public string TaskPath { get; }

        /// <summary>ElevationRequired only: the folder holding the staged files.</summary>
        public string StagingFolder { get; }

        /// <summary>After the elevated helper has finished: deletes the staged files and the task file.</summary>
        public void CleanUp()
        {
            AcWriter.TryDeleteFolder(StagingFolder);
            if (TaskPath != null)
            {
                AcWriter.TryDelete(TaskPath);
            }
        }
    }

    /// <summary>Result of <see cref="BytePatcher.Apply"/> / <see cref="BytePatcher.Revert"/>.</summary>
    public sealed class BytePatchResult
    {
        internal BytePatchResult(InstallOutcome outcome, ElevatedTask task, string taskPath)
        {
            Outcome = outcome;
            Task = task;
            TaskPath = taskPath;
        }

        /// <summary>How it ended.</summary>
        public InstallOutcome Outcome { get; }

        /// <summary>ElevationRequired only: the task handed to the elevated helper.</summary>
        public ElevatedTask Task { get; }

        /// <summary>ElevationRequired only: the saved task file.</summary>
        public string TaskPath { get; }
    }
}
