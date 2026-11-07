using System;
using DreamWeave.Launcher.Core;

namespace DreamWeave.ManifestTool
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.Error.WriteLine(LauncherInfo.ProductName + " manifest tool");
            Console.Error.WriteLine("Usage:");
            Console.Error.WriteLine("  ManifestTool keygen   Generate a signing key pair (not implemented)");
            Console.Error.WriteLine("  ManifestTool sign     Sign a manifest (not implemented)");
            Console.Error.WriteLine("  ManifestTool verify   Verify a signed manifest (not implemented)");
            return 1;
        }
    }
}
