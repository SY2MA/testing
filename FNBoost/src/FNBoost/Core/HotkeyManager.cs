using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;

namespace FNBoost.Core
{
    /// <summary>
    /// Scorciatoie globali tramite RegisterHotKey (API standard di Windows).
    /// Non è un keylogger né un hook: Windows notifica l'app solo quando premi la combinazione.
    /// Si possono ri-registrare in qualsiasi momento (UnregisterAll + Register) dopo una modifica nelle impostazioni.
    /// </summary>
    public sealed class HotkeyManager : IDisposable
    {
        private readonly HwndSource _source;
        private readonly Dictionary<int, Action> _actions = new();
        private readonly List<string> _failures = new();
        private int _nextId = 0xB000;
        private bool _disposed;

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

        /// <summary>Motivo dell'ultimo Register fallito (vuoto se è andato a buon fine).</summary>
        public string LastError { get; private set; } = "";

        /// <summary>Combinazioni non registrate dall'ultimo UnregisterAll (testo già pronto per l'utente).</summary>
        public IReadOnlyList<string> Failures => _failures;

        /// <summary>Numero di scorciatoie attualmente registrate.</summary>
        public int Count => _actions.Count;

        /// <summary>
        /// Converte il nome del tasto salvato nelle impostazioni nel tasto WPF. Rifiuta tasti vuoti,
        /// valori numerici (Enum.TryParse li accetterebbe) e tasti senza codice virtuale.
        /// </summary>
        public static bool TryGetKey(HotkeySetting? hk, out Key key)
        {
            key = Key.None;
            if (hk == null || string.IsNullOrWhiteSpace(hk.Key)) return false;
            var name = hk.Key.Trim();
            if (char.IsDigit(name[0]) || name[0] == '-') return false;
            if (!Enum.TryParse(name, true, out key) || key == Key.None) return false;
            return KeyInterop.VirtualKeyFromKey(key) != 0;
        }

        /// <summary>Firma normalizzata della combinazione (per trovare i doppioni), null se il tasto non è valido.</summary>
        public static string? Signature(HotkeySetting? hk)
        {
            if (hk == null || !TryGetKey(hk, out var key)) return null;
            return $"{(hk.Ctrl ? "C" : "")}{(hk.Alt ? "A" : "")}{(hk.Shift ? "S" : "")}:{KeyInterop.VirtualKeyFromKey(key)}";
        }

        /// <summary>Registra la combinazione. Se fallisce restituisce false, imposta <see cref="LastError"/> e la aggiunge a <see cref="Failures"/>.</summary>
        public bool Register(HotkeySetting hk, Action action)
        {
            LastError = "";
            if (_disposed) return Fail(hk, "gestore delle scorciatoie chiuso");
            if (hk == null) return Fail(null, "scorciatoia mancante");
            if (!TryGetKey(hk, out var key)) return Fail(hk, $"tasto \"{hk.Key}\" non valido");
            // Senza modificatori la combinazione ruberebbe il tasto a tutte le app (gioco compreso).
            if (!hk.Ctrl && !hk.Alt && !hk.Shift) return Fail(hk, "serve almeno Ctrl, Alt o Shift");

            uint mods = Native.MOD_NOREPEAT;
            if (hk.Ctrl) mods |= Native.MOD_CONTROL;
            if (hk.Alt) mods |= Native.MOD_ALT;
            if (hk.Shift) mods |= Native.MOD_SHIFT;
            var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            var id = _nextId++;
            if (!Native.RegisterHotKey(_source.Handle, id, mods, vk))
            {
                Log.Warn($"Scorciatoia {hk} già usata da un'altra app.");
                return Fail(hk, "già usata da un'altra app o riservata da Windows");
            }
            _actions[id] = action;
            return true;
        }

        private bool Fail(HotkeySetting? hk, string reason)
        {
            LastError = reason;
            _failures.Add(hk == null ? reason : $"{hk}: {reason}");
            return false;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
            {
                handled = true;
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    // Un errore in un'azione non deve far cadere il ciclo dei messaggi.
                    Log.Error("Azione della scorciatoia", ex);
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>Libera tutte le combinazioni (e azzera l'elenco degli errori) prima di una nuova registrazione.</summary>
        public void UnregisterAll()
        {
            if (!_disposed)
                foreach (var id in _actions.Keys) Native.UnregisterHotKey(_source.Handle, id);
            _actions.Clear();
            _failures.Clear();
            LastError = "";
        }

        public void Dispose()
        {
            if (_disposed) return;
            UnregisterAll();
            _disposed = true;
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
    }
}
