using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace ExternalFrameLimiter
{
	/// <summary>The frames of the last second, as measured from the game's frame counter.</summary>
	struct FrameReading
	{
		public int Frames;
		public double Fps;
		public double MinMs;
		public double MaxMs;
	}

	/// <summary>
	/// Reads the game's frame counter about once a millisecond on a thread of its own, and keeps each frame's
	/// length over the last second, so the pacing can be seen and not only the average.
	/// </summary>
	sealed class FrameMeter : IDisposable
	{
		const double WindowMs = 1000;

		[DllImport("winmm.dll")]
		static extern uint timeBeginPeriod(uint ms);

		[DllImport("winmm.dll")]
		static extern uint timeEndPeriod(uint ms);

		readonly GameMemory game;
		readonly Thread thread;
		readonly object sync = new object();
		readonly Stopwatch clock = Stopwatch.StartNew();
		// Each frame's end, in ms since start, and its length
		readonly Queue<(double end, double length)> frames = new Queue<(double, double)>();
		volatile bool stop;

		public FrameMeter(GameMemory game)
		{
			this.game = game;
			thread = new Thread(Run) { IsBackground = true, Name = "Frame meter" };
			thread.Start();
		}

		void Run()
		{
			timeBeginPeriod(1);
			try
			{
				int last = 0;
				double lastMs = 0;
				bool have = false;
				while (!stop)
				{
					double now = clock.Elapsed.TotalMilliseconds;
					if (game.TryReadFrameCounter(out int count))
					{
						int delta = count - last;
						if (!have || delta < 0 || delta > 1000)
						{
							// First read, or a save was loaded: start over from here
							have = true;
							last = count;
							lastMs = now;
							Clear();
						}
						else if (delta > 0)
						{
							double length = (now - lastMs) / delta;
							lock (sync)
							{
								for (int i = delta - 1; i >= 0; i--)
									frames.Enqueue((now - i * length, length));
								Trim(now);
							}
							last = count;
							lastMs = now;
						}
					}
					else if (have)
					{
						have = false;
						Clear();
					}
					Thread.Sleep(1);
				}
			}
			catch (Exception)
			{
				// Never take the tool down for a measurement
			}
			finally
			{
				timeEndPeriod(1);
			}
		}

		void Clear()
		{
			lock (sync)
				frames.Clear();
		}

		void Trim(double now)
		{
			while (frames.Count > 0 && frames.Peek().end < now - WindowMs)
				frames.Dequeue();
		}

		/// <summary>The frames that ended in the last second.</summary>
		public FrameReading Read()
		{
			lock (sync)
			{
				Trim(clock.Elapsed.TotalMilliseconds);
				if (frames.Count == 0)
					return default;
				var reading = new FrameReading { MinMs = double.MaxValue };
				double total = 0;
				foreach (var f in frames)
				{
					total += f.length;
					reading.MinMs = Math.Min(reading.MinMs, f.length);
					reading.MaxMs = Math.Max(reading.MaxMs, f.length);
				}
				reading.Frames = frames.Count;
				reading.Fps = total > 0 ? frames.Count * 1000 / total : 0;
				return reading;
			}
		}

		public void Dispose()
		{
			stop = true;
			thread.Join(500);
		}
	}
}
