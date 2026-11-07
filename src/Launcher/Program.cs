using System;
using System.Windows.Forms;
using DreamWeave.Launcher.Core;

namespace DreamWeave.Launcher
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // TODO(single-instance): take a per-user named mutex here before touching state or
            // downloads. StateStore already serialises its own file across instances, but two
            // launchers installing at once would still fight over the AC folder and staging dir.
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string mode = args.Length > 0 ? args[0] : string.Empty;
            switch (mode)
            {
                case "--uninstall":
                case "--updated":
                case "--elevated-copy":
                    // --elevated-copy takes a <task> argument (args[1]) once implemented.
                    // TODO(install-ui): for --elevated-copy, return
                    // DreamWeave.Launcher.Core.Install.ElevatedTaskRunner.Run(args[1]) (no UI, no MessageBox;
                    // the exit code is the LauncherError value). The UI step starts this exe with verb
                    // "runas", waits for exit, then calls ZipOverlayResult.CleanUp() / deletes the task file.
                    MessageBox.Show("Not implemented yet", LauncherInfo.ProductName);
                    return 0;
                default:
                    Application.Run(new MainForm());
                    return 0;
            }
        }
    }
}
