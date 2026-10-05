using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VoiceCommander.Core.Windows;

namespace VoiceCommander.App.Infrastructure;

/// <summary>
/// A read-only box that records a key combination instead of typing text. Backspace/Delete clears it
/// (= hotkey off); Tab and Esc keep their normal focus behaviour so keyboard users are never trapped.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) =>
        {
            var box = (HotkeyBox)d;
            box.Text = (string?)e.NewValue ?? "";
        }));

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        Cursor = Cursors.Hand;
        ContextMenu = null;
        InputMethod.SetIsInputMethodEnabled(this, false);
    }

    public string Hotkey
    {
        get => (string)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            e.Handled = true; // wait for the real key
            return;
        }

        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None)
        {
            if (key is Key.Tab or Key.Escape) return;
            if (key is Key.Back or Key.Delete)
            {
                Hotkey = "";
                e.Handled = true;
                return;
            }
        }

        uint m = 0;
        if (mods.HasFlag(ModifierKeys.Alt)) m |= KeyNames.MOD_ALT;
        if (mods.HasFlag(ModifierKeys.Control)) m |= KeyNames.MOD_CONTROL;
        if (mods.HasFlag(ModifierKeys.Shift)) m |= KeyNames.MOD_SHIFT;
        if (mods.HasFlag(ModifierKeys.Windows)) m |= KeyNames.MOD_WIN;

        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk != 0) Hotkey = KeyNames.Format(m, vk);
        e.Handled = true;
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e) => e.Handled = true;
}
