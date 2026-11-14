// Copyright (c) 2026 Cloudflare, Inc.
// Licensed under the Apache 2.0 license found in the LICENSE file or at:
// https://opensource.org/licenses/Apache-2.0

using System.Windows.Input;

namespace Kitesurf.Windows.Core;

public readonly record struct KeyDescriptor(string Key, string Code, int VirtualKey, int Location = 0);

/// <summary>
/// Maps Windows input onto the key descriptions the DevTools Protocol expects. The engine turns
/// these into editing operations rather than KeyboardEvents, so <c>text</c> carries the meaning.
/// </summary>
public static class KeyboardMap
{
    private static readonly Dictionary<Key, KeyDescriptor> Keys = BuildTable();

    private static readonly Dictionary<char, KeyDescriptor> TypedCharacters = BuildCharacterTable();

    public static bool TryGet(Key key, out KeyDescriptor descriptor) => Keys.TryGetValue(key, out descriptor);

    public static bool TryGetTyped(char character, out KeyDescriptor descriptor) =>
        TypedCharacters.TryGetValue(character, out descriptor);

    public static int Modifiers(ModifierKeys modifiers)
    {
        var value = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt)) value |= 1;
        if (modifiers.HasFlag(ModifierKeys.Control)) value |= 2;
        if (modifiers.HasFlag(ModifierKeys.Windows)) value |= 4;
        if (modifiers.HasFlag(ModifierKeys.Shift)) value |= 8;
        return value;
    }

    /// <summary>Keys that never produce text: they are dispatched as key down/up pairs.</summary>
    public static bool IsEditingKey(Key key) => key is
        Key.Back or Key.Delete or Key.Enter or Key.Return or Key.Tab or Key.Escape or
        Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or
        Key.PageUp or Key.PageDown or Key.Insert;

    private static Dictionary<Key, KeyDescriptor> BuildTable()
    {
        var map = new Dictionary<Key, KeyDescriptor>();

        for (var letter = Key.A; letter <= Key.Z; letter++)
        {
            var offset = letter - Key.A;
            var text = (char)('a' + offset);
            map[letter] = new KeyDescriptor(text.ToString(), $"Key{(char)('A' + offset)}", 0x41 + offset);
        }

        for (var digit = Key.D0; digit <= Key.D9; digit++)
        {
            var offset = digit - Key.D0;
            map[digit] = new KeyDescriptor(offset.ToString(), $"Digit{offset}", 0x30 + offset);
        }

        for (var digit = Key.NumPad0; digit <= Key.NumPad9; digit++)
        {
            var offset = digit - Key.NumPad0;
            map[digit] = new KeyDescriptor(offset.ToString(), $"Numpad{offset}", 0x60 + offset, 3);
        }

        map[Key.Space] = new KeyDescriptor(" ", "Space", 0x20);
        map[Key.Enter] = new KeyDescriptor("Enter", "Enter", 0x0D);
        map[Key.Return] = new KeyDescriptor("Enter", "Enter", 0x0D);
        map[Key.Tab] = new KeyDescriptor("Tab", "Tab", 0x09);
        map[Key.Back] = new KeyDescriptor("Backspace", "Backspace", 0x08);
        map[Key.Delete] = new KeyDescriptor("Delete", "Delete", 0x2E);
        map[Key.Escape] = new KeyDescriptor("Escape", "Escape", 0x1B);
        map[Key.Insert] = new KeyDescriptor("Insert", "Insert", 0x2D);
        map[Key.Home] = new KeyDescriptor("Home", "Home", 0x24);
        map[Key.End] = new KeyDescriptor("End", "End", 0x23);
        map[Key.PageUp] = new KeyDescriptor("PageUp", "PageUp", 0x21);
        map[Key.PageDown] = new KeyDescriptor("PageDown", "PageDown", 0x22);
        map[Key.Left] = new KeyDescriptor("ArrowLeft", "ArrowLeft", 0x25);
        map[Key.Up] = new KeyDescriptor("ArrowUp", "ArrowUp", 0x26);
        map[Key.Right] = new KeyDescriptor("ArrowRight", "ArrowRight", 0x27);
        map[Key.Down] = new KeyDescriptor("ArrowDown", "ArrowDown", 0x28);

        map[Key.OemMinus] = new KeyDescriptor("-", "Minus", 0xBD);
        map[Key.OemPlus] = new KeyDescriptor("=", "Equal", 0xBB);
        map[Key.OemOpenBrackets] = new KeyDescriptor("[", "BracketLeft", 0xDB);
        map[Key.Oem6] = new KeyDescriptor("]", "BracketRight", 0xDD);
        map[Key.Oem5] = new KeyDescriptor("\\", "Backslash", 0xDC);
        map[Key.Oem1] = new KeyDescriptor(";", "Semicolon", 0xBA);
        map[Key.OemQuotes] = new KeyDescriptor("'", "Quote", 0xDE);
        map[Key.OemComma] = new KeyDescriptor(",", "Comma", 0xBC);
        map[Key.OemPeriod] = new KeyDescriptor(".", "Period", 0xBE);
        map[Key.OemQuestion] = new KeyDescriptor("/", "Slash", 0xBF);
        map[Key.OemTilde] = new KeyDescriptor("`", "Backquote", 0xC0);

        map[Key.LeftShift] = new KeyDescriptor("Shift", "ShiftLeft", 0x10, 1);
        map[Key.RightShift] = new KeyDescriptor("Shift", "ShiftRight", 0x10, 2);
        map[Key.LeftCtrl] = new KeyDescriptor("Control", "ControlLeft", 0x11, 1);
        map[Key.RightCtrl] = new KeyDescriptor("Control", "ControlRight", 0x11, 2);
        map[Key.LeftAlt] = new KeyDescriptor("Alt", "AltLeft", 0x12, 1);
        map[Key.RightAlt] = new KeyDescriptor("Alt", "AltRight", 0x12, 2);
        map[Key.LWin] = new KeyDescriptor("Meta", "MetaLeft", 0x5B, 1);
        map[Key.RWin] = new KeyDescriptor("Meta", "MetaRight", 0x5C, 2);
        map[Key.Capital] = new KeyDescriptor("CapsLock", "CapsLock", 0x14);
        map[Key.Apps] = new KeyDescriptor("ContextMenu", "ContextMenu", 0x5D);
        map[Key.PrintScreen] = new KeyDescriptor("PrintScreen", "PrintScreen", 0x2C);
        map[Key.Scroll] = new KeyDescriptor("ScrollLock", "ScrollLock", 0x91);
        map[Key.Pause] = new KeyDescriptor("Pause", "Pause", 0x13);
        map[Key.NumLock] = new KeyDescriptor("NumLock", "NumLock", 0x90, 3);

        for (var index = 0; index < 12; index++)
        {
            var key = Key.F1 + index;
            map[key] = new KeyDescriptor($"F{index + 1}", $"F{index + 1}", 0x70 + index);
        }

        return map;
    }

    private static Dictionary<char, KeyDescriptor> BuildCharacterTable()
    {
        var map = new Dictionary<char, KeyDescriptor>();

        for (var character = 'a'; character <= 'z'; character++)
        {
            var upper = char.ToUpperInvariant(character);
            map[character] = new KeyDescriptor(character.ToString(), $"Key{upper}", 0x41 + (character - 'a'));
            map[upper] = new KeyDescriptor(upper.ToString(), $"Key{upper}", 0x41 + (character - 'a'));
        }

        const string digits = "0123456789";
        for (var index = 0; index < digits.Length; index++)
        {
            map[digits[index]] = new KeyDescriptor(digits[index].ToString(), $"Digit{index}", 0x30 + index);
        }

        var shiftedDigits = ")!@#$%^&*(";
        for (var index = 0; index < shiftedDigits.Length; index++)
        {
            map[shiftedDigits[index]] = new KeyDescriptor(shiftedDigits[index].ToString(), $"Digit{index}", 0x30 + index);
        }

        AddPair(map, ' ', ' ', "Space", 0x20);
        AddPair(map, '-', '_', "Minus", 0xBD);
        AddPair(map, '=', '+', "Equal", 0xBB);
        AddPair(map, '[', '{', "BracketLeft", 0xDB);
        AddPair(map, ']', '}', "BracketRight", 0xDD);
        AddPair(map, '\\', '|', "Backslash", 0xDC);
        AddPair(map, ';', ':', "Semicolon", 0xBA);
        AddPair(map, '\'', '"', "Quote", 0xDE);
        AddPair(map, ',', '<', "Comma", 0xBC);
        AddPair(map, '.', '>', "Period", 0xBE);
        AddPair(map, '/', '?', "Slash", 0xBF);
        AddPair(map, '`', '~', "Backquote", 0xC0);

        return map;
    }

    private static void AddPair(Dictionary<char, KeyDescriptor> map, char plain, char shifted, string code, int virtualKey)
    {
        map[plain] = new KeyDescriptor(plain.ToString(), code, virtualKey);
        map[shifted] = new KeyDescriptor(shifted.ToString(), code, virtualKey);
    }
}
