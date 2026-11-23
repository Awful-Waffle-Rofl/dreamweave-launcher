using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Cleanup
{
    /// <summary>
    /// installer/shipped-files.txt is the one list of files the build produces and the setup ships. These tests fail when
    /// the .iss [Files] section, check-build-output.ps1, build.yml or the real Launcher build output disagree with it.
    /// </summary>
    [TestClass]
    public class ShippedFilesTests
    {
        private static string RepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir, "DreamWeaveLauncher.sln")))
                {
                    return dir;
                }

                dir = Path.GetDirectoryName(dir);
            }

            Assert.Fail("Could not find the repository root by walking up from " + AppContext.BaseDirectory);
            return null;
        }

        private static string ReadRepoFile(params string[] parts)
        {
            string path = Path.Combine(RepoRoot(), Path.Combine(parts));
            Assert.IsTrue(File.Exists(path), "Missing " + path);
            return File.ReadAllText(path);
        }

        private sealed class Manifest
        {
            public List<string> Ship = new List<string>();
            public List<string> BuildOnly = new List<string>();
            public long MaxExeBytes;
            public long MaxTotalBytes;
        }

        private static Manifest Load()
        {
            var m = new Manifest();
            foreach (string raw in ReadRepoFile("installer", "shipped-files.txt").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] parts = Regex.Split(line, @"\s+");
                Assert.AreEqual(2, parts.Length, "bad line: " + line);
                switch (parts[0])
                {
                    case "ship": m.Ship.Add(parts[1]); break;
                    case "build-only": m.BuildOnly.Add(parts[1]); break;
                    case "max-exe-bytes": m.MaxExeBytes = long.Parse(parts[1]); break;
                    case "max-total-bytes": m.MaxTotalBytes = long.Parse(parts[1]); break;
                    default: Assert.Fail("unknown keyword: " + line); break;
                }
            }

            return m;
        }

        [TestMethod]
        public void List_IsWellFormed()
        {
            Manifest m = Load();
            CollectionAssert.Contains(m.Ship, "DreamWeaveLauncher.exe");
            CollectionAssert.Contains(m.Ship, "DreamWeaveLauncher.exe.config");
            Assert.AreEqual(m.Ship.Count, m.Ship.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "duplicate ship entry");
            Assert.AreEqual(0, m.Ship.Intersect(m.BuildOnly, StringComparer.OrdinalIgnoreCase).Count(), "a file is both ship and build-only");
            foreach (string name in m.Ship.Concat(m.BuildOnly))
            {
                Assert.IsTrue(Regex.IsMatch(name, @"^[A-Za-z0-9_.\-]+$"), "not a plain file name: " + name);
                Assert.IsFalse(name.Contains("*") || name.Contains("?"), "wildcard: " + name);
            }

            foreach (string name in m.Ship)
            {
                Assert.IsFalse(name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase), "never ship " + name);
            }

            Assert.IsTrue(m.MaxExeBytes > 0 && m.MaxTotalBytes >= m.MaxExeBytes, "size caps");
        }

        [TestMethod]
        public void Iss_FilesSection_HasOneExplicitSourceLinePerShippedFile_AndNothingElse()
        {
            Manifest m = Load();
            string iss = ReadRepoFile("installer", "DreamWeaveLauncher.iss");
            Match section = Regex.Match(iss, @"^\[Files\]\s*$(.*?)(?=^\[|\z)", RegexOptions.Multiline | RegexOptions.Singleline);
            Assert.IsTrue(section.Success, "missing [Files]");

            var sources = new List<string>();
            foreach (string line in section.Groups[1].Value.Split('\n'))
            {
                string entry = line.Trim();
                if (entry.Length == 0 || entry.StartsWith(";", StringComparison.Ordinal))
                {
                    continue;
                }

                Match mm = Regex.Match(entry, @"^Source:\s*""\{#ExeDir\}\\([^""\\]+)"";\s*DestDir:\s*""\{app\}"";\s*Flags:\s*ignoreversion$");
                Assert.IsTrue(mm.Success, "[Files] entry is not an explicit '{#ExeDir}\\<name>' line to {app}: " + entry);
                Assert.IsFalse(entry.Contains("*") || entry.Contains("?"), "wildcard in [Files]: " + entry);
                Assert.IsFalse(entry.Contains("recursesubdirs"), "recursesubdirs in [Files]: " + entry);
                sources.Add(mm.Groups[1].Value);
            }

            CollectionAssert.AreEquivalent(m.Ship, sources, "[Files] must list exactly the 'ship' entries of installer/shipped-files.txt");
            Assert.IsFalse(iss.IndexOf(".pdb", StringComparison.OrdinalIgnoreCase) >= 0, "the .iss must not mention a .pdb");
        }

        [TestMethod]
        public void CheckScriptAndWorkflows_ReadTheList_AndCarryNoCopyOfIt()
        {
            string script = ReadRepoFile("tools", "release", "check-build-output.ps1");
            StringAssert.Contains(script, "installer/shipped-files.txt");
            foreach (string forbidden in new[] { ".dll", ".pdb", ".config", "Newtonsoft", "Launcher.Core", "3145728" })
            {
                Assert.IsFalse(script.Contains(forbidden), "check-build-output.ps1 hard-codes " + forbidden);
            }

            foreach (string wf in new[] { "build.yml", "release.yml" })
            {
                string yml = ReadRepoFile(".github", "workflows", wf);
                StringAssert.Contains(yml, "check-build-output.ps1", wf);
                Assert.IsFalse(yml.Contains("-notcontains"), wf + " has an inline allowlist");
                Assert.IsFalse(yml.Contains("3145728"), wf + " has an inline size cap");
                Assert.IsFalse(yml.Contains("DreamWeaveLauncher.pdb"), wf + " names a build file");
            }
        }

        [TestMethod]
        public void ReleaseSigning_SignsBinariesBeforeTheSetupIsBuilt_AndTheSetupBeforePackaging()
        {
            string a = ReadRepoFile(".github", "actions", "package-release", "action.yml").Replace("\r\n", "\n");
            int stage = a.IndexOf("name: Stage the binaries to sign", StringComparison.Ordinal);
            int pass1 = a.IndexOf("name: Sign launcher binaries", StringComparison.Ordinal);
            int apply1 = a.IndexOf("name: Replace the binaries with the signed ones", StringComparison.Ordinal);
            int build = a.IndexOf("./tools/release/build-setup.ps1", StringComparison.Ordinal);
            int up2 = a.IndexOf("name: Upload the unsigned setup", StringComparison.Ordinal);
            int pass2 = a.IndexOf("name: Sign setup installer", StringComparison.Ordinal);
            int apply2 = a.IndexOf("name: Replace the setup with the signed one", StringComparison.Ordinal);
            int verify = a.IndexOf("./tools/release/assert-signed.ps1", StringComparison.Ordinal);
            int version = a.IndexOf("./tools/release/assert-version.ps1", StringComparison.Ordinal);
            int package = a.IndexOf("./tools/release/package-release.ps1", StringComparison.Ordinal);
            Assert.IsTrue(stage >= 0 && pass1 >= 0 && apply1 >= 0 && build >= 0 && up2 >= 0 && pass2 >= 0 && apply2 >= 0 && verify >= 0 && version >= 0 && package >= 0, "a signing or packaging step is missing");
            Assert.IsTrue(stage < pass1 && pass1 < apply1 && apply1 < build, "the binaries must be signed and swapped in BEFORE build-setup.ps1 packs them");
            Assert.IsTrue(build < up2 && up2 < pass2 && pass2 < apply2, "the setup is signed after it is built");
            Assert.IsTrue(apply2 < verify && verify < version && version < package, "the setup must be signed and verified before assert-version and package-release (SHA256SUMS covers the signed setup)");

            // Pass 1 uploads ONLY the three binaries, by name.
            string stageBlock = a.Substring(stage, pass1 - stage);
            foreach (string name in new[] { "DreamWeaveLauncher.exe", "Launcher.Core.dll", "DreamWeaveDecalLaunch.exe" })
            {
                StringAssert.Contains(stageBlock, name);
            }

            Assert.IsFalse(a.Contains("Newtonsoft"), "third-party files must never be signed or named in the signing steps");
            Assert.IsFalse(stageBlock.Contains("Get-ChildItem"), "stage the three named files, not a folder listing");

            // Both passes go to SignPath with their own artifact configuration and a timeout long enough for a human approval.
            Assert.AreEqual(2, Regex.Matches(a, "uses: SignPath/github-action-submit-signing-request@[0-9a-f]{40} ").Count, "exactly two SignPath submit steps, pinned to a full sha");
            string pass1Block = a.Substring(pass1, apply1 - pass1);
            string pass2Block = a.Substring(pass2, apply2 - pass2);
            StringAssert.Contains(pass1Block, "artifact-configuration-slug: ${{ inputs.signpath-artifact-config-binaries }}");
            StringAssert.Contains(pass2Block, "artifact-configuration-slug: ${{ inputs.signpath-artifact-config-setup }}");
            StringAssert.Contains(pass1Block, "wait-for-completion-timeout-in-seconds: 3600");
            StringAssert.Contains(pass2Block, "wait-for-completion-timeout-in-seconds: 3600");
            StringAssert.Contains(a.Substring(up2, pass2 - up2), "setup-out/DreamWeaveLauncher-Setup.exe");
            StringAssert.Contains(a.Substring(apply2, verify - apply2), "-TargetDir setup-out");
            StringAssert.Contains(a.Substring(apply1, build - apply1), "-TargetDir launcher-bin");
            Assert.IsFalse(a.Contains("secrets."), "a composite action cannot read secrets; the token arrives as an input");

            int verifyEnd = a.IndexOf("name: Assert tag equals", StringComparison.Ordinal);
            string verifyBlock = a.Substring(verify, verifyEnd - verify);
            foreach (string path in new[] { "launcher-bin/DreamWeaveLauncher.exe", "launcher-bin/Launcher.Core.dll", "launcher-bin/DreamWeaveDecalLaunch.exe", "setup-out/DreamWeaveLauncher-Setup.exe" })
            {
                StringAssert.Contains(verifyBlock, path, "assert-signed.ps1 must be given every signed file");
            }

            Assert.AreEqual(7, Regex.Matches(a, "if: inputs[.]sign == 'true'").Count, "every signing step (stage, upload, submit, replace; twice) is gated on inputs.sign");
            Assert.IsFalse(a.IndexOf("az" + "ure", StringComparison.OrdinalIgnoreCase) >= 0, "no leftover of the retired signer");
        }

        [TestMethod]
        public void ReleaseWorkflow_UsesTheSigningEnvironmentOnlyOnTheSignedJob()
        {
            string yml = ReadRepoFile(".github", "workflows", "release.yml").Replace("\r\n", "\n");
            int unsigned = yml.IndexOf("  package-unsigned:", StringComparison.Ordinal);
            int signed = yml.IndexOf("  package-signed:", StringComparison.Ordinal);
            int draft = yml.IndexOf("  draft-release:", StringComparison.Ordinal);
            Assert.IsTrue(unsigned > 0 && signed > unsigned && draft > signed, "expected build, package-unsigned, package-signed, draft-release in that order");
            string unsignedJob = yml.Substring(unsigned, signed - unsigned);
            string signedJob = yml.Substring(signed, draft - signed);
            StringAssert.Contains(unsignedJob, "vars.SIGNPATH_ORGANIZATION_ID == ''");
            StringAssert.Contains(signedJob, "vars.SIGNPATH_ORGANIZATION_ID != ''");
            StringAssert.Contains(signedJob, "environment: signing");
            StringAssert.Contains(signedJob, "timeout-minutes: 150");
            StringAssert.Contains(signedJob, "sign: 'true'");
            StringAssert.Contains(unsignedJob, "sign: 'false'");
            Assert.IsFalse(unsignedJob.Contains("sign: 'true'"), "the unsigned job must not sign");
            Assert.IsFalse(signedJob.Contains("sign: 'false'"), "the signed job must sign");
            Assert.IsFalse(unsignedJob.Contains("environment:"), "the unsigned job must not need the signing environment");
            Assert.IsFalse(yml.Contains("id-token"), "SignPath needs no OIDC token anywhere");
            Assert.IsFalse(yml.IndexOf("az" + "ure", StringComparison.OrdinalIgnoreCase) >= 0, "no leftover of the retired signer");
            Assert.AreEqual(1, Regex.Matches(yml, "secrets[.]SIGNPATH_API_TOKEN").Count, "the SignPath token is read exactly once");
            Assert.IsTrue(signedJob.Contains("signpath-api-token: ${{ secrets.SIGNPATH_API_TOKEN }}"), "the token is passed only by package-signed");
            int jobsAt = yml.IndexOf("jobs:", StringComparison.Ordinal);
            Assert.IsFalse(yml.Substring(jobsAt, signed - jobsAt).Contains("SIGNPATH_API_TOKEN"), "no job before package-signed may touch the token");
            Assert.IsFalse(yml.Contains("uses: SignPath/"), "the SignPath action lives in the composite action, which only package-signed calls with sign: 'true'");

            // The tag must be on main before any signing approval prompt can appear.
            int verify = yml.IndexOf("  verify-tag:", StringComparison.Ordinal);
            Assert.IsTrue(verify > 0 && verify < unsigned, "verify-tag job missing or misplaced");
            StringAssert.Contains(yml.Substring(verify, unsigned - verify), "merge-base --is-ancestor");
            StringAssert.Contains(signedJob, "needs: [build, verify-tag]");
            StringAssert.Contains(unsignedJob, "needs: [build, verify-tag]");

            // draft-release needs an explicit success from a package job.
            string draftJob = yml.Substring(draft);
            StringAssert.Contains(draftJob, "needs: [package-unsigned, package-signed]");
            StringAssert.Contains(draftJob, "needs.package-signed.result == 'success'");
            StringAssert.Contains(draftJob, "needs.package-unsigned.result == 'success'");
            Assert.IsFalse(draftJob.Contains("!failure()"), "draft-release must gate on an explicit success, not on !failure()");
            StringAssert.Contains(draftJob, "Code signing policy: https://github.com/Awful-Waffle-Rofl/dreamweave-launcher/blob/main/Docs/code-signing-policy.md");
        }

        [TestMethod]
        public void BuildOutput_MatchesTheList_WhenTheLauncherIsBuilt()
        {
            // The test assembly sits in tests/Launcher.Tests/bin/<Configuration>/<tfm>; the launcher in
            // src/Launcher/bin/<same Configuration>/<tfm>. Not built in this configuration: inconclusive, not a pass.
            string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string tfm = Path.GetFileName(baseDir);
            string config = Path.GetFileName(Path.GetDirectoryName(baseDir));
            string outDir = Path.Combine(RepoRoot(), "src", "Launcher", "bin", config, tfm);
            if (!File.Exists(Path.Combine(outDir, "DreamWeaveLauncher.exe")))
            {
                string message = "No launcher build output at " + outDir;
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
                {
                    // CI always builds first: a missing output there means this check would silently not run.
                    Assert.Fail(message);
                }

                Assert.Inconclusive(message);
            }

            Manifest m = Load();
            var present = new DirectoryInfo(outDir).GetFileSystemInfos().Select(f => f.Name).ToList();
            var allowed = m.Ship.Concat(m.BuildOnly).ToList();
            CollectionAssert.IsSubsetOf(m.Ship, present, "a shipped file is missing from the build output");
            CollectionAssert.IsSubsetOf(present, allowed, "the build output holds a file that is not in installer/shipped-files.txt");

            long total = m.Ship.Sum(n => new FileInfo(Path.Combine(outDir, n)).Length);
            Assert.IsTrue(new FileInfo(Path.Combine(outDir, "DreamWeaveLauncher.exe")).Length <= m.MaxExeBytes, "exe over its cap");
            Assert.IsTrue(total <= m.MaxTotalBytes, "shipped files over the total cap: " + total);
        }

        /// <summary>Markdown markers removed and runs of whitespace collapsed, so only the words are compared.</summary>
        private static string Normalize(string text)
        {
            string t = text.Replace("\r\n", "\n");
            t = Regex.Replace(t, @"^#+\s+", string.Empty, RegexOptions.Multiline);
            t = t.Replace("`", string.Empty);
            return Regex.Replace(t, @"\s+", " ").Trim();
        }

        [TestMethod]
        public void NoticesText_IsTheShippedSectionOfTheMarkdownNotices_AndNamesOnlyShippedThirdPartyFiles()
        {
            string md = ReadRepoFile("THIRD-PARTY-NOTICES.md").Replace("\r\n", "\n");
            int start = md.IndexOf("## Shipped beside the exe", StringComparison.Ordinal);
            int end = md.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0 && end > start, "the shipped section of THIRD-PARTY-NOTICES.md was not found");
            string expected = Normalize("# Third-party notices\n\n" + md.Substring(start, end - start));
            string txt = ReadRepoFile("src", "Launcher", "THIRD-PARTY-NOTICES.txt");
            Assert.AreEqual(expected, Normalize(txt), "src/Launcher/THIRD-PARTY-NOTICES.txt must carry the same words as the shipped section of THIRD-PARTY-NOTICES.md (headings without '#', no backticks).");

            foreach (string retired in new[] { "Costura", "Fody" })
            {
                Assert.IsFalse(txt.Contains(retired), "retired package in the shipped notices: " + retired);
            }

            // Every shipped third-party DLL (anything but our own files) has a notice entry.
            foreach (string name in Load().Ship.Where(n => n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("Launcher.", StringComparison.Ordinal)))
            {
                StringAssert.Contains(txt, name, "no notice for the shipped " + name);
            }
        }

        [TestMethod]
        public void NoticesText_IsShippedBesideTheExe()
        {
            CollectionAssert.Contains(Load().Ship, "THIRD-PARTY-NOTICES.txt");
            StringAssert.Contains(ReadRepoFile("src", "Launcher", "Launcher.csproj"), "THIRD-PARTY-NOTICES.txt");
            foreach (char c in ReadRepoFile("src", "Launcher", "THIRD-PARTY-NOTICES.txt"))
            {
                Assert.IsTrue(c == '\r' || c == '\n' || c == '\t' || (c >= ' ' && c < 127), "non-ASCII or control character: U+" + ((int)c).ToString("X4"));
            }
        }
        [TestMethod]
        public void List_IsAscii()
        {
            foreach (char c in ReadRepoFile("installer", "shipped-files.txt"))
            {
                Assert.IsTrue(c == '\r' || c == '\n' || (c >= ' ' && c < 127), "non-ASCII or control character: U+" + ((int)c).ToString("X4"));
            }
        }
    }
}