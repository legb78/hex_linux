using System.Text.RegularExpressions;

namespace HexLinux.Input;

/// <summary>
/// Why a key HexWin accepts in its shortcut is refused here, in words the
/// user can act on.
///
/// <para>Since its pull request #68, HexWin takes any key — letters, digits,
/// arrows, the numpad, media keys, even a raw <c>VK_</c> code — because
/// Windows withholds the keys of the shortcut from the application. On Linux
/// the keys are observed, not withheld (see <see cref="ChordDetector"/>):
/// whatever a key does when pressed, it still does while held as the
/// shortcut, and auto-repeat does it again and again for the whole dictation.
/// So a key is accepted only when that costs little (<see cref="LinuxKeys"/>),
/// and every other one is refused rather than silently widened: the reasons
/// below are what <c>--doctor</c> and the log show.</para>
///
/// <para>The names are HexWin's, so that a settings.json carried over from
/// Windows is told exactly why its shortcut fell back to the default. Pure.</para>
/// </summary>
public static partial class RefusedKeys
{
    private const string NotWithheld = "HexLinux cannot withhold keys from the desktop";

    private static readonly Dictionary<string, string> Reasons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = $"it would type spaces while held: {NotWithheld}",
        ["CapsLock"] = $"it would toggle capitals at every press: {NotWithheld}",
        ["NumLock"] = $"it would toggle the numeric keypad at every press: {NotWithheld}",
        ["ScrollLock"] = $"it would toggle Scroll Lock at every press: {NotWithheld}",
        ["Insert"] = $"it would toggle overwrite mode in editors at every press: {NotWithheld}",
        ["Escape"] = $"the focused application would close or cancel something at every press: {NotWithheld}",
        ["PrintScreen"] = "the desktop takes a screenshot at every press",
        ["Sleep"] = "the desktop puts the computer to sleep",
        ["Apps"] = $"it would open the context menu at every press: {NotWithheld}",
    };

    /// <summary>
    /// The reason <paramref name="name"/> is refused, or null for a name that
    /// is not refused on purpose — one Linux accepts, or no key at all.
    /// </summary>
    public static string? Reason(string? name)
    {
        string trimmed = name?.Trim() ?? string.Empty;

        if (Reasons.TryGetValue(trimmed, out string? reason))
        {
            return reason;
        }

        if (TypingKey().IsMatch(trimmed))
        {
            return $"it would type or delete text while held: {NotWithheld}";
        }

        if (MovingKey().IsMatch(trimmed))
        {
            return $"it would move the cursor at every press and repeat: {NotWithheld}";
        }

        if (DesktopKey().IsMatch(trimmed))
        {
            return "the desktop acts on it itself at every press (music, volume, browser, launcher)";
        }

        if (VirtualKeyCode().IsMatch(trimmed))
        {
            return "a Windows virtual-key code: Linux keys have other codes, and hexlinux --watch-hotkey names the ones a shortcut can use";
        }

        return null;
    }

    /// <summary>
    /// Letters, digits, the numpad (digits, operators and Enter), HexWin's
    /// punctuation keys by position (Oem1…), and the keys that edit text.
    /// </summary>
    [GeneratedRegex(@"^(?:[A-Z]|[0-9]|Numpad\w*|Oem\w*|Tab|Enter|Backspace|Delete|Clear)$", RegexOptions.IgnoreCase)]
    private static partial Regex TypingKey();

    [GeneratedRegex(@"^(?:Left|Right|Up|Down|Home|End|PageUp|PageDown)$", RegexOptions.IgnoreCase)]
    private static partial Regex MovingKey();

    [GeneratedRegex(@"^(?:Media\w+|Volume\w+|Browser\w+|Launch\w+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DesktopKey();

    [GeneratedRegex(@"^VK_[0-9A-F]{1,2}$", RegexOptions.IgnoreCase)]
    private static partial Regex VirtualKeyCode();
}
