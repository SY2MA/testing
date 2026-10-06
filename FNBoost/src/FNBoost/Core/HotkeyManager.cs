using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;

namespace FNBoost.Core
{
    /// <summary>
    /// Scorciatoie globali tramite RegisterHotKey (API standard di Windows).
    /// Non è un keylogger né un hook: Windows notifica l'app solo quando premi la combinazione.
    /// </summary>
    public sealed class HotkeyManager : IDisposable
    {
        private readonly HwndSource _source;
        private readonly Dictionary<int, Action> _actions = new();
        private int _nextId = 0xB000;

        public HotkeyManager()
        {
            var p = new HwndSourceParameters("FNBoostHotkeys")
            {
                ParentWindow = new IntPtr(-3), // HWND_MESSAGE: finestra invisibile solo messaggi
                WindowStyle = 0
            };
            _source = new HwndSource(p);
            _source.AddHook(WndProc);
        }

        public bool Register(HotkeySetting hk, Action action)
        {
            if (!Enum.TryParse<Key>(hk.Key, true, out var key)) return false;
            uint mods = Native.MOD_NOREPEAT;
            if (hk.Ctrl) mods |= Native.MOD_CONTROL;
            if (hk.Alt) mods |= Native.MOD_ALT;
            if (hk.Shift) mods |= Native.MOD_SHIFT;
            var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            var id = _nextId++;
            if (!Native.RegisterHotKey(_source.Handle, id, mods, vk))
            {
                Log.Warn($"Scorciatoia {hk} già usata da un'altra app.");
                return false;
            }
            _actions[id] = action;
            return true;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
            {
                handled = true;
                action();
            }
            return IntPtr.Zero;
        }

        public void UnregisterAll()
        {
            foreach (var id in _actions.Keys) Native.UnregisterHotKey(_source.Handle, id);
            _actions.Clear();
        }

        public void Dispose()
        {
            UnregisterAll();
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
    }
}
