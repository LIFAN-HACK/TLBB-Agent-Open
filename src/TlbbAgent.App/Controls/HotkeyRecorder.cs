using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace TlbbAgent.App.Controls;

public sealed class HotkeyRecorder : Control
{
    static HotkeyRecorder()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(HotkeyRecorder),
            new FrameworkPropertyMetadata(typeof(HotkeyRecorder)));
    }

    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(string), typeof(HotkeyRecorder),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            null,
            null,
            false,
            UpdateSourceTrigger.PropertyChanged));

    public string Hotkey
    {
        get => (string)GetValue(HotkeyProperty);
        set => SetCurrentValue(HotkeyProperty, value);
    }

    public static readonly DependencyProperty IsRecordingProperty = DependencyProperty.Register(
        nameof(IsRecording), typeof(bool), typeof(HotkeyRecorder), new PropertyMetadata(false));

    public bool IsRecording
    {
        get => (bool)GetValue(IsRecordingProperty);
        private set => SetValue(IsRecordingProperty, value);
    }

    public HotkeyRecorder()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        InputMethod.SetIsInputMethodEnabled(this, false);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        IsRecording = true;
        Keyboard.Focus(this);
        Focus();
        e.Handled = true;
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        IsRecording = false;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!IsRecording) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;

        var key = ResolveKey(e.Key, e.SystemKey, e.ImeProcessedKey, e.DeadCharProcessedKey);
        if (key == Key.None)
            return;

        if (IsModifierKey(key))
            return;

        if (key == Key.Escape) { IsRecording = false; return; }
        if (key is Key.Back or Key.Delete)
        {
            SetCurrentValue(HotkeyProperty, string.Empty);
            IsRecording = false;
            return;
        }

        SetCurrentValue(HotkeyProperty, FormatHotkey(key, Keyboard.Modifiers));
        IsRecording = false;
    }

    public static Key ResolveKey(Key key, Key systemKey, Key imeProcessedKey, Key deadCharProcessedKey)
    {
        if (key == Key.System && systemKey != Key.None)
            return systemKey;
        if (key == Key.ImeProcessed && imeProcessedKey != Key.None)
            return imeProcessedKey;
        if (key == Key.DeadCharProcessed && deadCharProcessedKey != Key.None)
            return deadCharProcessedKey;
        if (key == Key.None && imeProcessedKey != Key.None)
            return imeProcessedKey;
        if (key == Key.None && systemKey != Key.None)
            return systemKey;
        return key;
    }

    public static string FormatHotkey(Key key, ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyToText(key));
        return string.Join("+", parts);
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin;

    private static string KeyToText(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (key - Key.NumPad0),
        >= Key.F1 and <= Key.F24 => key.ToString(),
        >= Key.A and <= Key.Z => key.ToString(),
        Key.Space => "Space",
        Key.Tab => "Tab",
        Key.Enter => "Enter",
        Key.Insert => "Insert",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe => "\\",
        Key.OemTilde => "`",
        _ => key.ToString()
    };
}
