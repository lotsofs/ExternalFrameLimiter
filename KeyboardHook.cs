using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ExternalFrameLimiter
{
	/// <summary>
	/// A low-level keyboard hook: sees every key going down and up, whichever window has focus, and passes each
	/// one on. Install it on a thread with a message loop. Windows drops a hook that is slow to answer, so
	/// <see cref="KeyChanged"/> must be quick.
	/// </summary>
	sealed class KeyboardHook : IDisposable
	{
		const int WhKeyboardLl = 13;
		const int WmKeyDown = 0x0100;
		const int WmKeyUp = 0x0101;
		const int WmSysKeyDown = 0x0104;
		const int WmSysKeyUp = 0x0105;

		delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

		[DllImport("user32.dll", SetLastError = true)]
		static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);

		[DllImport("user32.dll", SetLastError = true)]
		static extern bool UnhookWindowsHookEx(IntPtr hook);

		[DllImport("user32.dll")]
		static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
		static extern IntPtr GetModuleHandle(string name);

		// Kept in a field: the hook holds only a pointer to it, so the GC mustn't collect it
		readonly HookProc proc;
		IntPtr hook;

		/// <summary>A key went down (true) or up (false). Auto-repeat sends more downs.</summary>
		public event Action<Keys, bool> KeyChanged;

		public KeyboardHook()
		{
			proc = Callback;
			hook = SetWindowsHookEx(WhKeyboardLl, proc, GetModuleHandle(null), 0);
			if (hook == IntPtr.Zero)
				throw new Win32Exception(Marshal.GetLastWin32Error());
		}

		IntPtr Callback(int code, IntPtr message, IntPtr data)
		{
			if (code >= 0)
			{
				int m = message.ToInt32();
				bool down = m == WmKeyDown || m == WmSysKeyDown;
				if (down || m == WmKeyUp || m == WmSysKeyUp)
				{
					try
					{
						KeyChanged?.Invoke((Keys)Marshal.ReadInt32(data), down);
					}
					catch (Exception)
					{
						// Never let an exception into the hook chain
					}
				}
			}
			return CallNextHookEx(hook, code, message, data);
		}

		public void Dispose()
		{
			if (hook != IntPtr.Zero)
				UnhookWindowsHookEx(hook);
			hook = IntPtr.Zero;
		}
	}
}
