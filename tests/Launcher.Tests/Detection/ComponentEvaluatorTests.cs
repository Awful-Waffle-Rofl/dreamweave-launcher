using System.IO;
using System.Linq;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Detection;
using DreamWeave.Launcher.Core.Manifests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Detection
{
    [TestClass]
    public class ComponentEvaluatorTests
    {
        private static readonly byte[] ExeOriginal = DetectionData.Bytes("original acclient bytes");
        private static readonly byte[] ExePatched = DetectionData.Bytes("patched!! acclient bytes");
        private static readonly byte[] DatA = DetectionData.Bytes("dat A contents");
        private static readonly byte[] DatB = DetectionData.Bytes("dat B contents, longer");

        private static string F(string rel)
        {
            return DetectionData.Full(rel);
        }

        private static LauncherManifest SampleLike()
        {
            string exeIn = DetectionData.Hash(ExeOriginal);
            string exeOut = DetectionData.Hash(ExePatched);
            return DetectionData.Manifest(
                DetectionData.Installer("ac-client", "acclient.exe", exeIn, exeOut),
                DetectionData.Overlay("eor-dats", 5000, DetectionData.File("a.dat", DatA), DetectionData.File("b.dat", DatB), DetectionData.File("acclient.exe", ExeOriginal)),
                DetectionData.Patch("palette-leak-fix", "acclient.exe", exeIn, exeOut));
        }

        private static ComponentStatus Find(System.Collections.Generic.IReadOnlyList<ComponentStatus> all, string id)
        {
            return all.Single(s => s.Id == id);
        }

        private static FakeFs Healthy(byte[] exe)
        {
            var fs = new FakeFs();
            fs.SetFile(F("acclient.exe"), exe);
            fs.SetFile(F("a.dat"), DatA);
            fs.SetFile(F("b.dat"), DatB);
            return fs;
        }

        private static System.Collections.Generic.IReadOnlyList<ComponentStatus> Run(FakeFs fs, LauncherManifest m, string folder = DetectionData.Folder)
        {
            using (var dir = new TempDir())
            {
                return new ComponentEvaluator(new FileVerifier(fs, DetectionData.NewState(dir))).EvaluateAll(m, folder);
            }
        }

        [TestMethod]
        public void Healthy_Unpatched_Install_Is_Satisfied_And_Patch_Is_Not_Applied()
        {
            var all = Run(Healthy(ExeOriginal), SampleLike());
            Assert.AreEqual(ComponentState.Satisfied, Find(all, "ac-client").State);
            ComponentStatus dats = Find(all, "eor-dats");
            Assert.AreEqual(ComponentState.Satisfied, dats.State);
            Assert.AreEqual(0L, dats.BytesToDownload);
            Assert.AreEqual(ComponentState.Missing, Find(all, "palette-leak-fix").State, "unpatched means 'not applied'");
            Assert.AreEqual(0L, Find(all, "palette-leak-fix").BytesToDownload);
        }

        [TestMethod]
        public void Patched_Acclient_Is_Not_Needs_Repair()
        {
            var all = Run(Healthy(ExePatched), SampleLike());
            Assert.AreEqual(ComponentState.Satisfied, Find(all, "ac-client").State);
            ComponentStatus dats = Find(all, "eor-dats");
            Assert.AreEqual(ComponentState.Satisfied, dats.State);
            StringAssert.Contains(dats.Files.Single(f => f.Path == "acclient.exe").Note, "palette-leak-fix");
            Assert.AreEqual(ComponentState.Satisfied, Find(all, "palette-leak-fix").State, "patch applied");
        }

        [TestMethod]
        public void Patched_Exe_Plus_Overlay_Routes_To_Launcher()
        {
            var m = SampleLike();
            var all = Run(Healthy(ExePatched), m);
            Assert.AreEqual(DreamWeave.Launcher.App.StartRoute.Launcher, DreamWeave.Launcher.App.RouteDecider.Decide(m, all));
        }

        [TestMethod]
        public void Patched_Exe_With_Wrong_Output_Hash_Is_Still_NeedsRepair()
        {
            byte[] other = DetectionData.Bytes("some other acclient build");
            var fs = Healthy(other);
            ComponentStatus dats = Find(Run(fs, SampleLike()), "eor-dats");
            Assert.AreEqual(ComponentState.NeedsRepair, dats.State);
            Assert.AreEqual(FileState.Mismatch, dats.Files.Single(f => f.Path == "acclient.exe").State);
        }

        [TestMethod]
        public void Patch_Whose_Input_Hash_Does_Not_Match_The_Entry_Grants_Nothing()
        {
            // The patch targets acclient.exe and its output is on disk, but it was made for a different input build.
            string wrongIn = DetectionData.Hash(DetectionData.Bytes("a different original build"));
            string exeOut = DetectionData.Hash(ExePatched);
            var m = DetectionData.Manifest(
                DetectionData.Overlay("eor-dats", 5000, DetectionData.File("a.dat", DatA), DetectionData.File("b.dat", DatB), DetectionData.File("acclient.exe", ExeOriginal)),
                DetectionData.Patch("palette-leak-fix", "acclient.exe", wrongIn, exeOut));
            var all = Run(Healthy(ExePatched), m);
            Assert.AreEqual(ComponentState.NeedsRepair, Find(all, "eor-dats").State);
        }

        [TestMethod]
        public void Patch_Target_Match_Is_Case_And_Slash_Insensitive()
        {
            string exeIn = DetectionData.Hash(ExeOriginal);
            string exeOut = DetectionData.Hash(ExePatched);
            var m = DetectionData.Manifest(
                DetectionData.Overlay("eor-dats", 5000, DetectionData.File("a.dat", DatA), DetectionData.File("b.dat", DatB), DetectionData.File("acclient.exe", ExeOriginal)),
                DetectionData.Patch("palette-leak-fix", "ACClient.EXE", exeIn, exeOut));
            Assert.AreEqual(ComponentState.Satisfied, Find(Run(Healthy(ExePatched), m), "eor-dats").State);

            // Slash style differs between the file entry and the patch target.
            var fs = Healthy(ExePatched);
            fs.SetFile(F("sub\\x.dat"), ExePatched);
            var nested = DetectionData.Manifest(
                DetectionData.Overlay("eor-dats", 5000, DetectionData.File("sub/x.dat", ExeOriginal)),
                DetectionData.Patch("palette-leak-fix", "SUB\\X.dat", exeIn, exeOut));
            Assert.AreEqual(ComponentState.Satisfied, Find(Run(fs, nested), "eor-dats").State);
        }

        [TestMethod]
        public void Patched_Output_Hash_Is_Only_Accepted_When_A_Patch_Targets_The_File()
        {
            var m = DetectionData.Manifest(DetectionData.Overlay("eor-dats", 5000, DetectionData.File("acclient.exe", ExeOriginal)));
            var fs = Healthy(ExePatched);
            Assert.AreEqual(ComponentState.NeedsRepair, Run(fs, m)[0].State);
        }

        [TestMethod]
        public void Shared_File_Is_Hashed_Once_Per_Evaluation()
        {
            var fs = Healthy(ExeOriginal);
            Run(fs, SampleLike());
            // acclient.exe, a.dat, b.dat: one read each despite acclient.exe appearing in three components.
            Assert.AreEqual(3, fs.OpenCount);
        }

        [TestMethod]
        public void Overlay_Lists_Only_The_Mismatched_Files()
        {
            var fs = Healthy(ExeOriginal);
            fs.SetFile(F("b.dat"), DetectionData.Bytes("dat B contents, CHANGED!"));
            ComponentStatus dats = Find(Run(fs, SampleLike()), "eor-dats");
            Assert.AreEqual(ComponentState.NeedsRepair, dats.State);
            Assert.AreEqual(5000L, dats.BytesToDownload);
            CollectionAssert.AreEqual(new[] { "b.dat" }, dats.Files.Where(f => f.State == FileState.Mismatch).Select(f => f.Path).ToArray());
            Assert.AreEqual(FileState.Ok, dats.Files.Single(f => f.Path == "a.dat").State);
        }

        [TestMethod]
        public void Overlay_Same_Size_Different_Content_Is_Mismatch_Via_Hash()
        {
            var fs = Healthy(ExeOriginal);
            byte[] sameLen = (byte[])DatA.Clone();
            sameLen[0] ^= 0xFF;
            fs.SetFile(F("a.dat"), sameLen);
            ComponentStatus dats = Find(Run(fs, SampleLike()), "eor-dats");
            Assert.AreEqual(ComponentState.NeedsRepair, dats.State);
            Assert.IsNotNull(dats.Files.Single(f => f.Path == "a.dat").ActualSha256);
        }

        [TestMethod]
        public void Overlay_Wrong_Size_Is_Mismatch_Without_Reading_The_File()
        {
            var fs = Healthy(ExeOriginal);
            fs.SetFile(F("b.dat"), new byte[3]);
            ComponentStatus dats = Find(Run(fs, SampleLike()), "eor-dats");
            Assert.AreEqual(FileState.Mismatch, dats.Files.Single(f => f.Path == "b.dat").State);
            Assert.AreEqual(2, fs.OpenCount, "acclient.exe and a.dat only");
        }

        [TestMethod]
        public void Overlay_With_One_Missing_File_Needs_Repair_And_With_None_Is_Missing()
        {
            var fs = Healthy(ExeOriginal);
            fs.Remove(F("a.dat"));
            Assert.AreEqual(ComponentState.NeedsRepair, Find(Run(fs, SampleLike()), "eor-dats").State);

            var empty = new FakeFs();
            empty.AddDir(DetectionData.Folder);
            ComponentStatus none = Find(Run(empty, SampleLike()), "eor-dats");
            Assert.AreEqual(ComponentState.Missing, none.State);
            Assert.AreEqual(5000L, none.BytesToDownload);
        }

        [TestMethod]
        public void Locked_Overlay_File_Makes_The_Component_Unknown_Not_A_Crash()
        {
            var fs = Healthy(ExeOriginal);
            fs.OpenFailures[F("b.dat")] = new IOException("in use", unchecked((int)0x80070020));
            ComponentStatus dats = Find(Run(fs, SampleLike()), "eor-dats");
            Assert.AreEqual(ComponentState.Unknown, dats.State);
            Assert.AreEqual(FileState.Locked, dats.Files.Single(f => f.Path == "b.dat").State);
            Assert.AreEqual(0L, dats.BytesToDownload);
        }

        [TestMethod]
        public void Definite_Mismatch_Outranks_A_Locked_File()
        {
            var fs = Healthy(ExeOriginal);
            fs.SetFile(F("a.dat"), DetectionData.Bytes("something else"));
            fs.OpenFailures[F("b.dat")] = new IOException("in use", unchecked((int)0x80070020));
            Assert.AreEqual(ComponentState.NeedsRepair, Find(Run(fs, SampleLike()), "eor-dats").State);
        }

        [TestMethod]
        public void Installer_Without_Folder_Or_Client_Exe_Is_Missing()
        {
            ComponentStatus noFolder = Find(Run(new FakeFs(), SampleLike(), null), "ac-client");
            Assert.AreEqual(ComponentState.Missing, noFolder.State);
            Assert.AreEqual(1000L, noFolder.BytesToDownload);

            var fs = new FakeFs();
            fs.AddDir(DetectionData.Folder);
            Assert.AreEqual(ComponentState.Missing, Find(Run(fs, SampleLike()), "ac-client").State);
        }

        [TestMethod]
        public void Installer_Hash_Not_In_List_Needs_Repair()
        {
            var fs = Healthy(DetectionData.Bytes("some other exe"));
            ComponentStatus c = Find(Run(fs, SampleLike()), "ac-client");
            Assert.AreEqual(ComponentState.NeedsRepair, c.State);
            Assert.AreEqual(1000L, c.BytesToDownload);
        }

        [TestMethod]
        public void Installer_Locked_Client_Exe_Is_Unknown()
        {
            var fs = Healthy(ExeOriginal);
            fs.OpenFailures[F("acclient.exe")] = new IOException("in use", unchecked((int)0x80070020));
            var all = Run(fs, SampleLike());
            Assert.AreEqual(ComponentState.Unknown, Find(all, "ac-client").State);
            Assert.AreEqual(ComponentState.Unknown, Find(all, "palette-leak-fix").State);
        }

        [TestMethod]
        public void Patch_With_Unrecognised_Target_Hash_Or_Missing_Target_Is_NotApplicable()
        {
            var other = Healthy(DetectionData.Bytes("EoR not applied yet"));
            Assert.AreEqual(ComponentState.NotApplicable, Find(Run(other, SampleLike()), "palette-leak-fix").State);

            var fs = new FakeFs();
            fs.AddDir(DetectionData.Folder);
            Assert.AreEqual(ComponentState.NotApplicable, Find(Run(fs, SampleLike()), "palette-leak-fix").State);
            Assert.AreEqual(ComponentState.NotApplicable, Find(Run(fs, SampleLike(), null), "palette-leak-fix").State);
        }

        private static ComponentState InstallerState(FakeFs fs, params SatisfiedWhenRule[] rules)
        {
            return Run(fs, DetectionData.Manifest(DetectionData.InstallerRules("ac-client", rules)))[0].State;
        }

        [TestMethod]
        public void Installer_AnyRule_Matching_Is_Satisfied_Even_When_Another_Rule_Is_Absent()
        {
            var fs = Healthy(ExeOriginal);
            string h = DetectionData.Hash(ExeOriginal);
            Assert.AreEqual(ComponentState.Satisfied, InstallerState(fs, DetectionData.Rule("acclient.exe", h), DetectionData.Rule("never-there.exe", h)));
            Assert.AreEqual(ComponentState.Satisfied, InstallerState(fs, DetectionData.Rule("never-there.exe", h), DetectionData.Rule("acclient.exe", h)));
        }

        [TestMethod]
        public void Installer_Mismatching_Rule_Plus_Matching_Rule_Is_Satisfied()
        {
            var fs = Healthy(ExeOriginal);
            Assert.AreEqual(ComponentState.Satisfied, InstallerState(fs,
                DetectionData.Rule("a.dat", new string('0', 64)),
                DetectionData.Rule("acclient.exe", DetectionData.Hash(ExeOriginal))));
        }

        [TestMethod]
        public void Installer_All_Rules_Missing_Is_Missing()
        {
            var fs = Healthy(ExeOriginal);
            string h = DetectionData.Hash(ExeOriginal);
            Assert.AreEqual(ComponentState.Missing, InstallerState(fs, DetectionData.Rule("x1.exe", h), DetectionData.Rule("x2.exe", h)));
        }

        [TestMethod]
        public void Installer_All_Rules_Mismatching_Is_NeedsRepair()
        {
            var fs = Healthy(ExeOriginal);
            string bad = new string('0', 64);
            Assert.AreEqual(ComponentState.NeedsRepair, InstallerState(fs, DetectionData.Rule("acclient.exe", bad), DetectionData.Rule("a.dat", bad)));
        }

        [TestMethod]
        public void Installer_Mismatch_Plus_Absent_Is_NeedsRepair()
        {
            var fs = Healthy(ExeOriginal);
            string bad = new string('0', 64);
            Assert.AreEqual(ComponentState.NeedsRepair, InstallerState(fs, DetectionData.Rule("acclient.exe", bad), DetectionData.Rule("never-there.exe", bad)));
        }

        [TestMethod]
        public void Installer_Zero_Rules_Is_Satisfied_Only_If_Client_Exe_Exists()
        {
            Assert.AreEqual(ComponentState.Satisfied, InstallerState(Healthy(ExeOriginal)));
            var noExe = new FakeFs();
            noExe.AddDir(DetectionData.Folder);
            Assert.AreEqual(ComponentState.Missing, InstallerState(noExe));
        }

        [TestMethod]
        public void Installer_Unreadable_Rule_With_No_Match_Is_Unknown_But_A_Match_Still_Wins()
        {
            var fs = Healthy(ExeOriginal);
            fs.OpenFailures[F("a.dat")] = new IOException("in use", unchecked((int)0x80070020));
            string h = DetectionData.Hash(ExeOriginal);
            Assert.AreEqual(ComponentState.Unknown, InstallerState(fs, DetectionData.Rule("a.dat", h), DetectionData.Rule("never-there.exe", h)));
            Assert.AreEqual(ComponentState.Satisfied, InstallerState(fs, DetectionData.Rule("a.dat", h), DetectionData.Rule("acclient.exe", h)));
        }

        [TestMethod]
        public void Lower_Case_Manifest_Hashes_Still_Match()
        {
            var fs = Healthy(ExeOriginal);
            Assert.AreEqual(ComponentState.Satisfied, InstallerState(fs, DetectionData.Rule("acclient.exe", DetectionData.Hash(ExeOriginal).ToLowerInvariant())));
        }

        [TestMethod]
        public void Overlay_With_Zero_Files_Is_Unknown()
        {
            var m = DetectionData.Manifest(DetectionData.Overlay("empty", 100));
            Assert.AreEqual(ComponentState.Unknown, Run(Healthy(ExeOriginal), m)[0].State);
        }
        [TestMethod]
        public void Unknown_Kind_Is_Unknown()
        {
            var m = DetectionData.Manifest(new ManifestComponent { Id = "future", Kind = "teleporter" });
            ComponentStatus s = Run(new FakeFs(), m)[0];
            Assert.AreEqual(ComponentState.Unknown, s.State);
            Assert.AreEqual("future", s.Id);
        }

        [TestMethod]
        public void Unsafe_Manifest_Path_Is_ManifestInvalid()
        {
            var m = DetectionData.Manifest(DetectionData.Overlay("bad", 1, new ManifestFile { Path = @"..\evil.dll", Size = 1, Sha256 = new string('A', 64) }));
            LauncherException ex = Assert.ThrowsExactly<LauncherException>(() => Run(Healthy(ExeOriginal), m));
            Assert.AreEqual(LauncherError.ManifestInvalid, ex.Code);

            var dev = DetectionData.Manifest(DetectionData.Patch("p", "NUL", new string('A', 64), new string('B', 64)));
            Assert.AreEqual(LauncherError.ManifestInvalid, Assert.ThrowsExactly<LauncherException>(() => Run(Healthy(ExeOriginal), dev)).Code);
        }

        [TestMethod]
        public void Evaluation_Never_Writes_Into_The_Game_Folder_On_Real_Disk()
        {
            using (var game = new TempDir())
            using (var data = new TempDir())
            {
                File.WriteAllBytes(Path.Combine(game.Path, "acclient.exe"), ExeOriginal);
                File.WriteAllBytes(Path.Combine(game.Path, "a.dat"), DatA);
                File.WriteAllBytes(Path.Combine(game.Path, "b.dat"), DatB);
                var before = Directory.GetFiles(game.Path).Select(p => p + "|" + new FileInfo(p).Length + "|" + File.GetLastWriteTimeUtc(p).Ticks).OrderBy(x => x).ToArray();

                var eval = new ComponentEvaluator(new FileVerifier(new RealAcFileSystem(), DetectionData.NewState(data)));
                var all = eval.EvaluateAll(SampleLike(), game.Path);

                Assert.AreEqual(ComponentState.Satisfied, Find(all, "eor-dats").State);
                var after = Directory.GetFiles(game.Path).Select(p => p + "|" + new FileInfo(p).Length + "|" + File.GetLastWriteTimeUtc(p).Ticks).OrderBy(x => x).ToArray();
                CollectionAssert.AreEqual(before, after);
            }
        }
    }
}
