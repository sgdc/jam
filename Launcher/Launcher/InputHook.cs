using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace Launcher
{
    // Built/based off http://learnwpf.com/post/2011/08/03/Adding-a-system-wide-keyboard-hook-to-your-WPF-Application.aspx
    public class InputHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;

        private IntPtr hookId = IntPtr.Zero;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        #region Interop

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true, CallingConvention = CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process(IntPtr process, [Out] out bool wow64Process);

        #endregion

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
            {
                if (KeyDown != null)
                {
                    int vkCode = Marshal.ReadInt32(lParam);
                    var keyPressed = KeyInterop.KeyFromVirtualKey(vkCode);
                    KeyDown(null, keyPressed);
                }
            }
            return CallNextHookEx(hookId, nCode, wParam, lParam);
        }

        private static bool IsWin64Process(Process proc)
        {
            // http://stackoverflow.com/questions/1953377/how-to-know-a-process-is-32-bit-or-64-bit-programmatically
            if ((Environment.OSVersion.Version.Major > 5) || ((Environment.OSVersion.Version.Major == 5) && (Environment.OSVersion.Version.Minor >= 1)))
            {
                try
                {
                    bool retVal;

                    return IsWow64Process(proc.Handle, out retVal) && retVal;
                }
                catch
                {
                    return false; // access is denied to the process
                }
            }

            return false; // not on 64-bit Windows
        }

        #region Public

        public event EventHandler<Key> KeyDown;

        public bool IsHooked
        {
            get
            {
                return hookId != IntPtr.Zero;
            }
        }

        public bool Hookup(Process proc)
        {
            if (IsHooked)
            {
                return false;
            }
            using (var mod = proc.MainModule)
            {
                hookId = SetWindowsHookEx(WH_KEYBOARD_LL, HookCallback, GetModuleHandle(mod.ModuleName), 0);
            }
            return IsHooked;
        }

        public bool Unhook()
        {
            if (!IsHooked)
            {
                return false;
            }
            var res = UnhookWindowsHookEx(hookId);
            hookId = IntPtr.Zero;
            return res;
        }

        public void Dispose()
        {
            if (IsHooked)
            {
                UnhookWindowsHookEx(hookId);
            }
        }

        public static bool CanHookProcess(Process proc, out Exception error)
        {
            // Indicates if the started process is the same size (32 bit, 64 bit) as the currently running process.

            error = null;
            if (Environment.Is64BitProcess == IsWin64Process(proc))
            {
                // Liteweight test completed. In order for this to work, the main module needs to be retrieved
                try
                {
                    using (var mod = proc.MainModule)
                    {
                        return true;
                    }
                }
                catch (Exception e)
                {
                    error = e;
                }
            }
            return false;
        }

        #endregion
    }
}
