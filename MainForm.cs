using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace ExternalFrameLimiter
{
	/// <summary>The window: the game's status, the target and the measured frame rate. Everything here runs on the UI thread.</summary>
	sealed class MainForm : Form
	{
		enum Rate { Fps30, Fps60, Uncapped }

		/// <summary>
		/// The game's cap for each rate. With the 14 ms delay on, 60 is capped at 103: with vsync on a 60 Hz screen that
		/// gives 60 on screen, where a cap of 60 gives about 43. Uncapped is 1000, not higher, because the game's
		/// timers freeze above 1000 FPS.
		/// </summary>
		int Cap(Rate rate) => rate == Rate.Fps30 ? 30 : rate == Rate.Fps60 ? (removeDelay ? 60 : 103) : 1000;

		string Name(Rate rate) => rate == Rate.Fps30 ? "30 FPS" : rate == Rate.Uncapped ? "Uncapped" : removeDelay ? "60 FPS" : "60 FPS (cap 103)";

		const long F8HoldMs = 2000;

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

		readonly Stopwatch clock = Stopwatch.StartNew();

		Rate setting = Rate.Fps60;
		bool held;
		bool removeDelay;
		// When F8 went down, in ms on the clock, or -1 while it is up; and whether this hold has toggled the delay yet
		long f8DownAt = -1;
		bool f8Toggled;

		Rate Target => held ? (setting == Rate.Fps30 ? Rate.Fps60 : Rate.Fps30) : setting;

		public MainForm()
		{
			Text = "External Frame Limiter";
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
			keysLabel.Text = "F5: 30   F6: 60   F7: uncapped\nHold F8 for 2 s: 14 ms delay on/off\nHold Num+: 30 (60 while set to 30)";

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
					setting = Rate.Fps30;
					break;
				case Keys.F6 when down:
					setting = Rate.Fps60;
					break;
				case Keys.F7 when down:
					setting = Rate.Uncapped;
					break;
				case Keys.F8:
					if (!down)
					{
						f8DownAt = -1;
						f8Toggled = false;
					}
					else if (f8DownAt < 0)
						f8DownAt = clock.ElapsedMilliseconds;
					// Auto-repeat while held checks the time too, as does the timer
					CheckF8Hold();
					break;
				case Keys.Add when down != held:
					held = down;
					break;
				default:
					return;
			}
			Apply();
			ShowState();
		}

		/// <summary>Toggles the delay once F8 has been held for 2 seconds, once per hold.</summary>
		void CheckF8Hold()
		{
			if (f8DownAt >= 0 && !f8Toggled && clock.ElapsedMilliseconds - f8DownAt >= F8HoldMs)
			{
				f8Toggled = true;
				removeDelay = !removeDelay;
			}
		}

		void Apply() => game.Apply(Cap(Target), Target == Rate.Uncapped, removeDelay);

		void Tick()
		{
			try
			{
				game.Refresh();
				CheckF8Hold();
				Apply();
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
					else if (Target == Rate.Uncapped)
						status = "Uncapped, vsync off";
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

			string target = held ? $"{Name(Target)}   (Num+ held, set to {Name(setting)})" : Name(Target);
			target += removeDelay ? "\n14 ms delay off" : "\n14 ms delay on";
			if (f8DownAt >= 0 && !f8Toggled)
				target += removeDelay ? ": keep holding F8 to switch it on" : ": keep holding F8 to switch it off";
			SetText(targetLabel, target);

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
