using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GtaSaFpsLock
{
	/// <summary>The window: the game's status, the target and the measured frame rate. Everything here runs on the UI thread.</summary>
	sealed class MainForm : Form
	{
		const int HeldDefault = 30;
		const int HeldWhenSetTo30 = 60;

		readonly GameMemory game = new GameMemory();
		readonly FrameMeter meter;
		readonly KeyboardHook hook;
		readonly string hookError;
		readonly Timer timer = new Timer { Interval = 250 };
		readonly NotifyIcon tray = new NotifyIcon();
		readonly Label statusLabel = NewLabel();
		readonly Label targetLabel = NewLabel(bold: true);
		readonly Label measuredLabel = NewLabel();
		readonly Label keysLabel = NewLabel();

		int setting = 30;
		int refreshRate = 60;

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		static extern bool EnumDisplaySettings(string device, int mode, ref DevMode devMode);

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		struct DevMode
		{
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
			public short SpecVersion, DriverVersion, Size, DriverExtra;
			public int Fields, PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
			public short Color, Duplex, YResolution, TTOption, Collate;
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
			public short LogPixels;
			public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
		}

		/// <summary>The primary screen's refresh rate, or the last one read if Windows won't say.</summary>
		int ReadRefreshRate()
		{
			var mode = new DevMode { Size = (short)Marshal.SizeOf(typeof(DevMode)) };
			if (EnumDisplaySettings(null, -1, ref mode) && mode.DisplayFrequency > 1)
				return mode.DisplayFrequency;
			return refreshRate;
		}
		bool held;

		int Target => held ? (setting == HeldDefault ? HeldWhenSetTo30 : HeldDefault) : setting;

		public MainForm()
		{
			Text = "GTA SA FPS Lock";
			FormBorderStyle = FormBorderStyle.FixedSingle;
			MaximizeBox = false;
			StartPosition = FormStartPosition.CenterScreen;
			AutoSize = true;
			AutoSizeMode = AutoSizeMode.GrowAndShrink;
			Font = SystemFonts.MessageBoxFont;

			var layout = new TableLayoutPanel
			{
				ColumnCount = 1,
				AutoSize = true,
				AutoSizeMode = AutoSizeMode.GrowAndShrink,
				Padding = new Padding(10),
			};
			layout.Controls.Add(statusLabel);
			layout.Controls.Add(targetLabel);
			layout.Controls.Add(measuredLabel);
			layout.Controls.Add(keysLabel);
			Controls.Add(layout);
			keysLabel.ForeColor = SystemColors.GrayText;
			keysLabel.Text = "F5: 30   F6: 60   F7: 120\nHold Num+: 30 (60 while set to 30)";

			tray.Icon = Icon;
			tray.Text = Text;
			tray.DoubleClick += (s, e) => ShowFromTray();
			tray.ContextMenuStrip = new ContextMenuStrip();
			tray.ContextMenuStrip.Items.Add("Show", null, (s, e) => ShowFromTray());
			tray.ContextMenuStrip.Items.Add("Exit", null, (s, e) => Close());

			meter = new FrameMeter(game);
			try
			{
				hook = new KeyboardHook();
				hook.KeyChanged += OnKey;
			}
			catch (Exception e)
			{
				hookError = "Hotkeys unavailable: " + e.Message;
			}

			timer.Tick += (s, e) => Tick();
			timer.Start();
			Tick();
		}

		static Label NewLabel(bool bold = false)
		{
			var label = new Label { AutoSize = true, Margin = new Padding(0, 2, 0, 2), MinimumSize = new Size(280, 0) };
			if (bold)
				label.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 11, FontStyle.Bold);
			return label;
		}

		void OnKey(Keys key, bool down)
		{
			switch (key)
			{
				case Keys.F5 when down:
					setting = 30;
					break;
				case Keys.F6 when down:
					setting = 60;
					break;
				case Keys.F7 when down:
					setting = 120;
					break;
				case Keys.Add when down != held:
					held = down;
					break;
				default:
					return;
			}
			game.Apply(Target, refreshRate);
			ShowState();
		}

		void Tick()
		{
			try
			{
				game.Refresh();
				refreshRate = ReadRefreshRate();
				game.Apply(Target, refreshRate);
				ShowState();
			}
			catch (Exception e)
			{
				statusLabel.Text = "Error: " + e.Message;
			}
		}

		void ShowState()
		{
			string status;
			switch (game.Status)
			{
				case GameStatus.Found:
					if (!game.LimiterOn)
						status = "Frame Limiter is off in the game's menu: leaving the game alone";
					else if (!game.CodeRecognised)
						status = "Locked, but some of the game's code isn't 1.0's, so the frame delay or vsync was left in";
					else if (Target > refreshRate)
						status = $"Locked, vsync off (above the screen's {refreshRate} Hz)";
					else
						status = "Locked";
					break;
				case GameStatus.NoAccess:
					status = "Can't open the game: if it runs as administrator, run this as administrator too";
					break;
				case GameStatus.Unsupported:
					status = "This version of the game isn't supported: only PC 1.0 is";
					break;
				default:
					status = "Waiting for gta_sa.exe";
					break;
			}
			if (hookError != null)
				status += "\n" + hookError;
			SetText(statusLabel, status);

			SetText(targetLabel, held ? $"{Target} FPS   (Num+ held, set to {setting})" : $"{Target} FPS");

			FrameReading r = meter.Read();
			SetText(measuredLabel, game.Status == GameStatus.Found && r.Frames > 0
				? $"Measured: {r.Fps:0.0} FPS, frames {r.MinMs:0.0} to {r.MaxMs:0.0} ms"
				: "Measured: no frames");
		}

		static void SetText(Label label, string text)
		{
			if (label.Text != text)
				label.Text = text;
		}

		void ShowFromTray()
		{
			Show();
			WindowState = FormWindowState.Normal;
			Activate();
			tray.Visible = false;
		}

		protected override void OnResize(EventArgs e)
		{
			base.OnResize(e);
			if (WindowState == FormWindowState.Minimized)
			{
				tray.Visible = true;
				Hide();
			}
		}

		protected override void OnFormClosed(FormClosedEventArgs e)
		{
			timer.Stop();
			hook?.Dispose();
			meter.Dispose();
			game.Restore();
			game.Dispose();
			tray.Visible = false;
			tray.Dispose();
			base.OnFormClosed(e);
		}
	}
}
