using System;
using System.Collections.Generic;
using System.IO;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Manifests;

namespace DreamWeave.ManifestTool
{
    /// <summary>
    /// Offline manifest signing tool. Uses the launcher's own <see cref="ManifestVerifier"/> and
    /// <see cref="SigningKey"/> so the signer and the launcher cannot drift.
    /// Exit codes: 0 success, 1 verification or I/O failure, 2 usage error.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                return Usage();
            }

            Dictionary<string, string> opts;
            try
            {
                opts = ParseOptions(args);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return Usage();
            }

            try
            {
                switch (args[0])
                {
                    case "keygen":
                        return Keygen(Require(opts, "out"));
                    case "sign":
                        return Sign(Require(opts, "key"), Require(opts, "manifest"));
                    case "verify":
                        return Verify(Require(opts, "pub"), Require(opts, "manifest"));
                    default:
                        return Usage();
                }
            }
            catch (UsageException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return Usage();
            }
            catch (LauncherException ex)
            {
                Console.Error.WriteLine("FAILED (" + ex.Code + "): " + ex.Detail);
                return 1;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                Console.Error.WriteLine("FAILED: " + ex.Message);
                return 1;
            }
        }

        private static int Keygen(string outPath)
        {
            if (File.Exists(outPath))
            {
                Console.Error.WriteLine("Refusing to overwrite existing key file: " + outPath);
                return 1;
            }

            SigningKey key = SigningKey.Generate();
            using (var fs = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write))
            using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                w.Write(key.ToJson());
            }

            Console.WriteLine("keyId:  " + key.PublicKey.KeyId);
            Console.WriteLine("public: " + key.PublicKey.ToPublicString());
            Console.WriteLine();
            Console.WriteLine("Add to src/Launcher.Core/Manifests/TrustedKeys.cs:");
            Console.WriteLine("    TrustedKey.Parse(\"" + key.PublicKey.ToPublicString() + "\"),");
            Console.WriteLine();
            Console.WriteLine("WARNING: " + outPath + " holds the PRIVATE signing key. Keep it OFFLINE:");
            Console.WriteLine("never commit it, never copy it to a server or CI secret, and back it up");
            Console.WriteLine("somewhere only you control. Anyone holding it can push files to every player.");
            return 0;
        }

        private static int Sign(string keyPath, string manifestPath)
        {
            SigningKey key = SigningKey.FromJson(File.ReadAllText(keyPath));
            byte[] manifest = File.ReadAllBytes(manifestPath);
            ManifestSignature sig = key.Sign(manifest);
            byte[] sigBytes = sig.ToFileBytes();

            // Self-check with the launcher's verifier before writing anything.
            VerifiedManifest verified = new ManifestVerifier(new[] { key.PublicKey }).Verify(manifest, sigBytes, 0);
            string sigPath = manifestPath + ".sig";
            File.WriteAllBytes(sigPath, sigBytes);
            Console.WriteLine("Signed serial " + verified.Serial + " with " + key.PublicKey.KeyId + " -> " + sigPath);
            PrintWarnings(verified);
            return 0;
        }

        private static int Verify(string pub, string manifestPath)
        {
            TrustedKey key = LoadPublicKey(pub);
            byte[] manifest = File.ReadAllBytes(manifestPath);
            byte[] sig = File.ReadAllBytes(manifestPath + ".sig");
            VerifiedManifest verified = new ManifestVerifier(new[] { key }).Verify(manifest, sig, 0);
            Console.WriteLine("OK: serial " + verified.Serial + ", schema " + verified.Manifest.Schema + ", key " + verified.KeyId + ", " + verified.Manifest.Components.Count + " component(s).");
            PrintWarnings(verified);
            return 0;
        }

        private static void PrintWarnings(VerifiedManifest verified)
        {
            foreach (string w in verified.Manifest.Warnings)
            {
                Console.WriteLine("warning: " + w);
            }
        }

        /// <summary>Accepts <c>keyId:X:Y</c> directly, or a file holding either that line or a key JSON file.</summary>
        private static TrustedKey LoadPublicKey(string pub)
        {
            if (File.Exists(pub))
            {
                string text = File.ReadAllText(pub).Trim();
                return text.StartsWith("{", StringComparison.Ordinal) ? SigningKey.FromJson(text).PublicKey : TrustedKey.Parse(text);
            }

            return TrustedKey.Parse(pub);
        }

        private static Dictionary<string, string> ParseOptions(string[] args)
        {
            var opts = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (!a.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                {
                    throw new ArgumentException("Unexpected argument: " + a);
                }

                opts[a.Substring(2)] = args[++i];
            }

            return opts;
        }

        private static string Require(Dictionary<string, string> opts, string name)
        {
            string v;
            if (!opts.TryGetValue(name, out v) || string.IsNullOrEmpty(v))
            {
                throw new UsageException("Missing --" + name);
            }

            return v;
        }

        private static int Usage()
        {
            Console.Error.WriteLine(LauncherInfo.ProductName + " manifest tool");
            Console.Error.WriteLine("Usage:");
            Console.Error.WriteLine("  ManifestTool keygen --out <privatekey.json>");
            Console.Error.WriteLine("  ManifestTool sign   --key <privatekey.json> --manifest <manifest.json>   (writes <manifest.json>.sig)");
            Console.Error.WriteLine("  ManifestTool verify --pub <keyId:X:Y | file> --manifest <manifest.json>  (reads <manifest.json>.sig)");
            return 2;
        }

        private sealed class UsageException : Exception
        {
            public UsageException(string message)
                : base(message)
            {
            }
        }
    }
}
