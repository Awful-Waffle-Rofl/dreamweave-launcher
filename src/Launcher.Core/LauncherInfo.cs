using DreamWeave.Launcher.Core.Update;

namespace DreamWeave.Launcher.Core
{
    /// <summary>Product-wide constants.</summary>
    public static class LauncherInfo
    {
        /// <summary>Display name of the product.</summary>
        public const string ProductName = "DreamWeave Launcher";

        private static readonly SemVersion RunningVersion = SemVersion.FromAssembly(typeof(LauncherInfo).Assembly);

        /// <summary>
        /// The launcher's version. Every project takes <c>&lt;Version&gt;</c> from Directory.Build.props,
        /// so this assembly's informational version is the shipped exe's version (build metadata such
        /// as <c>+commit</c> is dropped).
        /// </summary>
        public static SemVersion SemanticVersion => RunningVersion;

        /// <summary><see cref="SemanticVersion"/> as text, for example <c>1.0.0</c>.</summary>
        public static string Version => RunningVersion.ToString();
    }
}
