using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FNBoost.Core;

namespace FNBoost.Views
{
    /// <summary>
    /// Casella che "registra" una combinazione di tasti: clic, poi premi ad es. Ctrl+Alt+X.
    /// Esc annulla la modifica, Backspace/Canc disattiva la scorciatoia. Servono almeno Ctrl, Alt o Shift
    /// (una scorciatoia globale senza modificatori ruberebbe il tasto al gioco).
    /// Mentre si modifica, le scorciatoie globali di FN Boost vengono sospese, altrimenti Windows
    /// consegnerebbe la combinazione all'app invece che alla casella.
    /// Legge solo i tasti premuti nella propria finestra (eventi WPF): nessun hook di tastiera.
    /// </summary>
    public sealed class HotkeyBox : TextBox
    {
        private HotkeySetting _hotkey = new() { Enabled = false };
        private bool _editing;
        private bool _suspended;

        /// <summary>La combinazione è stata cambiata dall'utente (non scatta quando si imposta <see cref="Hotkey"/> da codice).</summary>
        public event EventHandler? HotkeyChanged;

        public HotkeyBox()
        {
            // Lo stile implicito "TextBox" non si applica alle sottoclassi: lo agganciamo esplicitamente.
            SetResourceReference(StyleProperty, typeof(TextBox));
            IsReadOnly = true;
            IsReadOnlyCaretVisible = false;
            IsUndoEnabled = false;
            AllowDrop = false;
            Cursor = Cursors.Hand;
            MinWidth = 150;
            ToolTip = "Clicca e premi la nuova combinazione (es. Ctrl+Alt+X).\nEsc annulla · Backspace/Canc disattiva la scorciatoia.";
            InputMethod.SetIsInputMethodEnabled(this, false);

            ContextMenuOpening += (_, e) => e.Handled = true;
            Unloaded += (_, _) => EndEdit();
            UpdateText();
        }

        /// <summary>Combinazione corrente (sempre una copia: modificarla non tocca le impostazioni finché non le si salva).</summary>
        public HotkeySetting Hotkey
        {
            get => _hotkey.Clone();
            set
            {
                _hotkey = value?.Clone() ?? new HotkeySetting { Enabled = false };
                if (_editing) EndEdit();
                else UpdateText();
            }
        }

        public bool IsEditing => _editing;

        private void UpdateText()
        {
            Text = _editing ? "Premi la combinazione…  (Esc annulla)" : _hotkey.ToString();
            SelectionLength = 0;
        }

        private void BeginEdit()
        {
            if (_editing) return;
            _editing = true;
            if (!_suspended)
            {
                _suspended = true;
                App.SuspendHotkeys(true);
            }
            UpdateText();
        }

        private void EndEdit()
        {
            _editing = false;
            if (_suspended)
            {
                _suspended = false;
                App.SuspendHotkeys(false);
            }
            UpdateText();
        }

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            // Niente selezione del testo o cursore: un clic avvia sempre la registrazione.
            e.Handled = true;
            if (!IsKeyboardFocusWithin) Focus();
            BeginEdit();
        }

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnGotKeyboardFocus(e);
            BeginEdit();
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            // Uscire dalla casella (clic altrove, Alt+Tab) annulla la modifica in corso.
            EndEdit();
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            // Alt+tasto arriva come Key.System: il tasto vero è in SystemKey.
            var key = e.Key switch
            {
                Key.System => e.SystemKey,
                Key.ImeProcessed => e.ImeProcessedKey,
                Key.DeadCharProcessed => e.DeadCharProcessedKey,
                _ => e.Key
            };
            var mods = Keyboard.Modifiers;
            bool ctrl = (mods & ModifierKeys.Control) != 0;
            bool alt = (mods & ModifierKeys.Alt) != 0;
            bool shift = (mods & ModifierKeys.Shift) != 0;
            bool noMods = !ctrl && !alt && !shift;

            // Tab senza modificatori resta la navigazione da tastiera tra i controlli.
            if (key == Key.Tab && noMods)
            {
                base.OnPreviewKeyDown(e);
                return;
            }
            e.Handled = true;

            if (!_editing)
            {
                // Con il focus ma fuori dalla modifica: Invio/Spazio la riavviano, il resto si ignora.
                if (noMods && (key == Key.Enter || key == Key.Space)) BeginEdit();
                return;
            }

            if (noMods && key == Key.Escape)
            {
                EndEdit();
                return;
            }
            if (noMods && (key == Key.Back || key == Key.Delete))
            {
                _hotkey = _hotkey.Clone();
                _hotkey.Enabled = false;
                EndEdit();
                HotkeyChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (IsModifierKey(key))
            {
                // Anteprima mentre si tengono premuti i modificatori.
                var partial = $"{(ctrl ? "Ctrl+" : "")}{(alt ? "Alt+" : "")}{(shift ? "Shift+" : "")}";
                Text = partial.Length > 0 ? partial + "…" : "Premi la combinazione…  (Esc annulla)";
                return;
            }
            if ((mods & ModifierKeys.Windows) != 0)
            {
                Text = "Il tasto Windows non è supportato: usa Ctrl/Alt/Shift";
                return;
            }
            if (noMods)
            {
                Text = "Aggiungi almeno Ctrl, Alt o Shift";
                return;
            }
            if (key == Key.None || KeyInterop.VirtualKeyFromKey(key) == 0)
            {
                Text = "Tasto non utilizzabile, provane un altro";
                return;
            }

            _hotkey = new HotkeySetting { Ctrl = ctrl, Alt = alt, Shift = shift, Key = key.ToString(), Enabled = true };
            EndEdit();
            HotkeyChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnPreviewTextInput(TextCompositionEventArgs e) => e.Handled = true;

        private static bool IsModifierKey(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.Apps;
    }
}
