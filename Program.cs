using System;
using System.Threading;
using System.Windows.Forms;

namespace ExternalFrameLimiter
{
	static class Program
	{
		[STAThread]
		static void Main()
		{
			using (var mutex = new Mutex(true, "ExternalFrameLimiter-single-instance", out bool first))
			{
				if (!first)
				{
					MessageBox.Show("External Frame Limiter is already running.", "External Frame Limiter", MessageBoxButtons.OK, MessageBoxIcon.Information);
					return;
				}
				Application.EnableVisualStyles();
				Application.SetCompatibleTextRenderingDefault(false);
				Application.Run(new MainForm());
			}
		}
	}
}
