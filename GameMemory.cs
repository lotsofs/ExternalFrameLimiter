using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GtaSaFpsLock
{
	enum GameStatus { Waiting, NoAccess, Unsupported, Found }

	/// <summary>
	/// Attaches to GTA SA PC 1.0 and sets its frame limiter. Owned by the UI thread, except
	/// <see cref="TryReadFrameCounter"/>, which the frame meter's thread calls; the lock keeps the handle
	/// from being closed while it reads.
	/// </summary>
	sealed class GameMemory : IDisposable
	{
		static readonly string[] ProcessNames = { "gta_sa", "gta-sa" };

		// The 1.0 check, as in SplitSpammer's GameVersion: the int isn't there yet while the game checks the CD, hence the size
		const int ModuleSize = 18313216;
		const int VersionValue = 38079;
		const int VersionOffsetEu = 0x4245BC;
		const int VersionOffsetUs = 0x42457C;

		// Module offsets for 1.0 (module base 0x400000); see README.md for where each comes from
		const int FrameLimitOffset = 0x81704C;   // RsGlobal.frameLimit, 0xC1704C: the game writes 30 at startup
		const int LimiterOnOffset = 0x7A6794;    // FrontEndMenuManager.m_bFrameLimiter, 0xBA6794: the menu's Frame Limiter
		const int FrameCounterOffset = 0x77CB4C; // CTimer::m_FrameCounter, 0xB7CB4C
		const int GameFrameLimit = 30;

		// Idle's 14 ms frame delay, 0x53E94A: cmp eax,0Eh; jb loop. Comparing with 0 instead never loops.
		static readonly CodePatch NoFrameDelay = new CodePatch(0x13E94A, new byte[] { 0x83, 0xF8, 0x0E, 0x72, 0xE1 }, 2, 0x00);

		// psCameraShowRaster, 0x745240: mov al,[0xBA6794]; test al,al; jne push1. Presents with vsync while Frame Limiter is on.
		// A jne to the next instruction leaves only the other flag (0xBAB318) to ask for vsync.
		static readonly CodePatch NoLimiterVsync = new CodePatch(0x345240, new byte[] { 0xA0, 0x94, 0x67, 0xBA, 0x00, 0x84, 0xC0, 0x75, 0x0D }, 8, 0x00);

		/// <summary>One byte of code to change, checked against the code around it first. A single byte is written atomically, so the game can run meanwhile.</summary>
		sealed class CodePatch
		{
			public readonly int Offset;
			public readonly byte[] Original;
			public readonly int Index;
			public readonly byte Patched;

			public CodePatch(int offset, byte[] original, int index, byte patched)
			{
				Offset = offset;
				Original = original;
				Index = index;
				Patched = patched;
			}
		}

		const uint ProcessVmOperation = 0x0008;
		const uint ProcessVmRead = 0x0010;
		const uint ProcessVmWrite = 0x0020;
		const uint ProcessQueryInformation = 0x0400;
		const uint PageExecuteReadWrite = 0x40;

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool CloseHandle(IntPtr handle);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr written);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool VirtualProtectEx(IntPtr process, IntPtr address, IntPtr size, uint protect, out uint oldProtect);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool FlushInstructionCache(IntPtr process, IntPtr address, IntPtr size);

		readonly object sync = new object();
		Process process;
		IntPtr handle;
		long moduleBase;
		int moduleSize;

		// Written by the UI thread, read by the frame meter's too
		volatile GameStatus status = GameStatus.Waiting;
		public GameStatus Status { get => status; private set => status = value; }

		/// <summary>False when the code of the frame delay or the vsync check isn't what 1.0 has, so it was left alone.</summary>
		public bool CodeRecognised { get; private set; } = true;

		/// <summary>Attaches when the game appears, detaches when it exits, and retries the version check until it passes.</summary>
		public void Refresh()
		{
			try
			{
				if (process != null && process.HasExited)
					Detach();
				if (process == null && !Attach())
					return;
				if (Status != GameStatus.Found)
				{
					bool supported = moduleSize == ModuleSize || IsVersion(VersionOffsetEu) || IsVersion(VersionOffsetUs);
					Status = supported ? GameStatus.Found : GameStatus.Unsupported;
				}
			}
			catch (Exception)
			{
				Detach();
			}
		}

		bool IsVersion(int offset) => TryReadInt(offset, out int v) && v == VersionValue;

		bool Attach()
		{
			bool denied = false;
			foreach (string name in ProcessNames)
			{
				foreach (Process candidate in Process.GetProcessesByName(name))
				{
					if (process != null)
					{
						candidate.Dispose();
						continue;
					}
					try
					{
						ProcessModule module = candidate.MainModule;
						IntPtr h = OpenProcess(ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation, false, candidate.Id);
						if (h == IntPtr.Zero)
						{
							denied = true;
							candidate.Dispose();
							continue;
						}
						lock (sync)
						{
							process = candidate;
							handle = h;
							moduleBase = module.BaseAddress.ToInt64();
							moduleSize = module.ModuleMemorySize;
						}
						CodeRecognised = true;
					}
					catch (Win32Exception)
					{
						// Access denied (the game runs as administrator), or it is starting or exiting: try again on the next refresh
						denied = true;
						candidate.Dispose();
					}
					catch (InvalidOperationException)
					{
						candidate.Dispose();
					}
				}
			}
			if (process == null)
				Status = denied ? GameStatus.NoAccess : GameStatus.Waiting;
			return process != null;
		}

		void Detach()
		{
			lock (sync)
			{
				if (handle != IntPtr.Zero)
					CloseHandle(handle);
				handle = IntPtr.Zero;
				process?.Dispose();
				process = null;
			}
			Status = GameStatus.Waiting;
		}

		/// <summary>The game's Frame Limiter setting, as last read by <see cref="Apply"/>.</summary>
		public bool LimiterOn { get; private set; }

		/// <summary>
		/// While the menu's Frame Limiter is on: sets the cap and switches the frame delay off, and switches off the
		/// limiter's vsync while the cap is above <paramref name="refreshRate"/>. While it is off: leaves the game as it is
		/// without the tool (cap 30, the delay and vsync as the game has them). Writes only what differs.
		/// </summary>
		public void Apply(int fps, int refreshRate)
		{
			if (Status != GameStatus.Found)
				return;
			try
			{
				if (!TryReadInt(FrameLimitOffset, out int limit) || !TryReadByte(LimiterOnOffset, out byte limiterOn))
				{
					Detach();
					return;
				}
				LimiterOn = limiterOn != 0;
				if (!LimiterOn)
					fps = GameFrameLimit;
				if (limit != fps)
					WriteInt(FrameLimitOffset, fps);
				bool recognised = Set(NoFrameDelay, LimiterOn);
				recognised &= Set(NoLimiterVsync, LimiterOn && fps > refreshRate);
				CodeRecognised = recognised;
			}
			catch (Exception)
			{
				Detach();
			}
		}

		/// <summary>Puts the game back as it was: cap 30, the frame delay and vsync as the game has them.</summary>
		public void Restore()
		{
			if (Status != GameStatus.Found || process == null)
				return;
			try
			{
				if (process.HasExited)
					return;
				WriteInt(FrameLimitOffset, GameFrameLimit);
				Set(NoFrameDelay, false);
				Set(NoLimiterVsync, false);
			}
			catch (Exception)
			{
				// The game may be exiting: nothing to restore then
			}
		}

		/// <summary>Applies or undoes a patch. False, and nothing written, when the code around it isn't 1.0's.</summary>
		bool Set(CodePatch patch, bool on)
		{
			var code = new byte[patch.Original.Length];
			if (!TryReadBytes(patch.Offset, code))
				return true;
			byte current = code[patch.Index];
			byte original = patch.Original[patch.Index];
			code[patch.Index] = original;
			if ((current != original && current != patch.Patched) || !Equal(code, patch.Original))
				return false;
			byte value = on ? patch.Patched : original;
			if (current != value)
				WriteCode(patch.Offset + patch.Index, value);
			return true;
		}

		void WriteCode(int offset, byte value)
		{
			var address = new IntPtr(moduleBase + offset);
			var size = new IntPtr(1);
			if (!VirtualProtectEx(handle, address, size, PageExecuteReadWrite, out uint old))
				return;
			try
			{
				WriteProcessMemory(handle, address, new[] { value }, size, out IntPtr _);
			}
			finally
			{
				VirtualProtectEx(handle, address, size, old, out uint _);
				FlushInstructionCache(handle, address, size);
			}
		}

		static bool Equal(byte[] a, byte[] b)
		{
			for (int i = 0; i < a.Length; i++)
				if (a[i] != b[i])
					return false;
			return true;
		}

		/// <summary>The game's frame counter. Safe to call from any thread.</summary>
		public bool TryReadFrameCounter(out int frames)
		{
			lock (sync)
			{
				if (Status != GameStatus.Found)
				{
					frames = 0;
					return false;
				}
				return TryReadInt(FrameCounterOffset, out frames);
			}
		}

		bool TryReadInt(int offset, out int value)
		{
			var buffer = new byte[4];
			bool ok = TryReadBytes(offset, buffer);
			value = ok ? BitConverter.ToInt32(buffer, 0) : 0;
			return ok;
		}

		bool TryReadByte(int offset, out byte value)
		{
			var buffer = new byte[1];
			bool ok = TryReadBytes(offset, buffer);
			value = buffer[0];
			return ok;
		}

		bool TryReadBytes(int offset, byte[] buffer)
		{
			lock (sync)
			{
				if (handle == IntPtr.Zero)
					return false;
				return ReadProcessMemory(handle, new IntPtr(moduleBase + offset), buffer, new IntPtr(buffer.Length), out IntPtr read)
					&& read.ToInt64() == buffer.Length;
			}
		}

		void WriteInt(int offset, int value) => Write(offset, BitConverter.GetBytes(value));

		void WriteByte(int offset, byte value) => Write(offset, new[] { value });

		void Write(int offset, byte[] data)
		{
			lock (sync)
			{
				if (handle != IntPtr.Zero)
					WriteProcessMemory(handle, new IntPtr(moduleBase + offset), data, new IntPtr(data.Length), out IntPtr _);
			}
		}

		public void Dispose() => Detach();
	}
}
