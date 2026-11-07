using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WindowsNotch
{
    public sealed class GlobalHotkeyService : IDisposable
    {
        private const int WmHotkey = 0x0312;
        private const int MainHotkeyId = 0xBEEF;
        private const int FocusHotkeyId = 0xBEF0;
        private const uint NoRepeat = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private HwndSource? _source;
        private bool _mainRegistered;
        private bool _focusRegistered;
        private bool _disposed;

        public event EventHandler? HotkeyPressed;
        public event EventHandler? FocusHotkeyPressed;

        public void Initialize(Window window)
        {
            if (_disposed) return;

            var helper = new WindowInteropHelper(window);
            _source = HwndSource.FromHwnd(helper.Handle);
            _source?.AddHook(WndProc);
        }

        public bool Register(uint modifiers, uint virtualKey)
        {
            return RegisterMain(modifiers, virtualKey);
        }

        public bool RegisterMain(uint modifiers, uint virtualKey)
        {
            UnregisterMain();

            if (_disposed || _source == null || virtualKey == 0) return false;

            _mainRegistered = RegisterHotKey(
                _source.Handle, MainHotkeyId, modifiers | NoRepeat, virtualKey);
            return _mainRegistered;
        }

        public bool RegisterFocus(uint modifiers, uint virtualKey)
        {
            UnregisterFocus();

            if (_disposed || _source == null || virtualKey == 0) return false;

            _focusRegistered = RegisterHotKey(
                _source.Handle, FocusHotkeyId, modifiers | NoRepeat, virtualKey);
            return _focusRegistered;
        }

        public void Unregister()
        {
            UnregisterMain();
        }

        public void UnregisterMain()
        {
            if (_mainRegistered && _source != null)
            {
                UnregisterHotKey(_source.Handle, MainHotkeyId);
                _mainRegistered = false;
            }
        }

        public void UnregisterFocus()
        {
            if (_focusRegistered && _source != null)
            {
                UnregisterHotKey(_source.Handle, FocusHotkeyId);
                _focusRegistered = false;
            }
        }

        public void UnregisterAll()
        {
            UnregisterMain();
            UnregisterFocus();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            UnregisterAll();
            _source?.RemoveHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WmHotkey) return IntPtr.Zero;

            switch (wParam.ToInt32())
            {
                case MainHotkeyId:
                    HotkeyPressed?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    break;

                case FocusHotkeyId:
                    FocusHotkeyPressed?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    break;
            }

            return IntPtr.Zero;
        }
    }
}
