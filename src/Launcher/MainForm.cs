using System.Drawing;
using System.Windows.Forms;
using DreamWeave.Launcher.Core;

namespace DreamWeave.Launcher
{
    internal sealed class MainForm : Form
    {
        public MainForm()
        {
            Text = LauncherInfo.ProductName;
            ClientSize = new Size(640, 400);
            StartPosition = FormStartPosition.CenterScreen;

            var disclaimer = new Label
            {
                Text = "Not affiliated with WB Games or Turbine.",
                AutoSize = true,
                Dock = DockStyle.Bottom,
                Padding = new Padding(8)
            };
            Controls.Add(disclaimer);
        }
    }
}
