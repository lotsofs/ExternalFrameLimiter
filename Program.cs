using System;
using System.Threading;
using System.Windows.Forms;

namespace GtaSaFpsLock
{
	static class Program
	{
		[STAThread]
		static void Main()
		{
			using (var mutex = new Mutex(true, "GtaSaFpsLock-single-instance", out bool first))
			{
				if (!first)
				{
					MessageBox.Show("GTA SA FPS Lock is already running.", "GTA SA FPS Lock", MessageBoxButtons.OK, MessageBoxIcon.Information);
					return;
				}
				Application.EnableVisualStyles();
				Application.SetCompatibleTextRenderingDefault(false);
				Application.Run(new MainForm());
			}
		}
	}
}
