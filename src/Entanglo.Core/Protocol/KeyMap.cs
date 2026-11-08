namespace Entanglo.Core.Protocol;

/// <summary>
/// Bidirectional table: macOS HIToolbox virtual key code ↔ Win32 VK code.
/// Source: HIToolbox/Events.h (Mac) and Virtual-Key Codes MSDN (Windows).
/// PROTOCOL.md §7.
/// </summary>
public static class KeyMap
{
    static readonly Dictionary<int, int> MacToWin = new()
    {
        // Letters (Mac ANSI layout — physical position, not character)
        { 0,   0x41 }, // A
        { 1,   0x53 }, // S
        { 2,   0x44 }, // D
        { 3,   0x46 }, // F
        { 4,   0x48 }, // H
        { 5,   0x47 }, // G
        { 6,   0x5A }, // Z
        { 7,   0x58 }, // X
        { 8,   0x43 }, // C
        { 9,   0x56 }, // V
        { 11,  0x42 }, // B
        { 12,  0x51 }, // Q
        { 13,  0x57 }, // W
        { 14,  0x45 }, // E
        { 15,  0x52 }, // R
        { 16,  0x59 }, // Y
        { 17,  0x54 }, // T
        { 31,  0x4F }, // O
        { 32,  0x55 }, // U
        { 34,  0x49 }, // I
        { 35,  0x50 }, // P
        { 37,  0x4C }, // L
        { 38,  0x4A }, // J
        { 40,  0x4B }, // K
        { 45,  0x4E }, // N
        { 46,  0x4D }, // M

        // Digits
        { 18,  0x31 }, // 1
        { 19,  0x32 }, // 2
        { 20,  0x33 }, // 3
        { 21,  0x34 }, // 4
        { 22,  0x36 }, // 6
        { 23,  0x35 }, // 5
        { 25,  0x39 }, // 9
        { 26,  0x37 }, // 7
        { 28,  0x38 }, // 8
        { 29,  0x30 }, // 0

        // Punctuation
        { 24,  0xBB }, // = (VK_OEM_PLUS)
        { 27,  0xBD }, // - (VK_OEM_MINUS)
        { 30,  0xDD }, // ] (VK_OEM_6)
        { 33,  0xDB }, // [ (VK_OEM_4)
        { 39,  0xDE }, // ' (VK_OEM_7)
        { 41,  0xBA }, // ; (VK_OEM_1)
        { 42,  0xDC }, // \ (VK_OEM_5)
        { 43,  0xBC }, // , (VK_OEM_COMMA)
        { 44,  0xBF }, // / (VK_OEM_2)
        { 47,  0xBE }, // . (VK_OEM_PERIOD)
        { 50,  0xC0 }, // ` (VK_OEM_3)

        // Control keys
        { 36,  0x0D }, // Return
        { 48,  0x09 }, // Tab
        { 49,  0x20 }, // Space
        { 51,  0x08 }, // Delete (Backspace)
        { 53,  0x1B }, // Escape
        { 57,  0x14 }, // Caps Lock
        { 117, 0x2E }, // Forward Delete (Del)

        // Modifier keys
        { 55,  0x5B }, // Command (left) → LWIN
        { 56,  0xA0 }, // Shift (left)   → VK_LSHIFT
        { 58,  0xA4 }, // Option (left)  → VK_LMENU (Alt)
        { 59,  0xA2 }, // Control (left) → VK_LCONTROL
        { 60,  0xA1 }, // Right Shift    → VK_RSHIFT
        { 61,  0xA5 }, // Right Option   → VK_RMENU
        { 62,  0xA3 }, // Right Control  → VK_RCONTROL
        { 54,  0x5C }, // Right Command  → RWIN

        // Navigation
        { 115, 0x24 }, // Home
        { 116, 0x21 }, // Page Up
        { 119, 0x23 }, // End
        { 121, 0x22 }, // Page Down
        { 123, 0x25 }, // Left Arrow
        { 124, 0x27 }, // Right Arrow
        { 125, 0x28 }, // Down Arrow
        { 126, 0x26 }, // Up Arrow

        // F-keys (Mac kVK_F*)
        { 122, 0x70 }, // F1
        { 120, 0x71 }, // F2
        { 99,  0x72 }, // F3
        { 118, 0x73 }, // F4
        { 96,  0x74 }, // F5
        { 97,  0x75 }, // F6
        { 98,  0x76 }, // F7
        { 100, 0x77 }, // F8
        { 101, 0x78 }, // F9
        { 109, 0x79 }, // F10
        { 103, 0x7A }, // F11
        { 111, 0x7B }, // F12

        // Numpad
        { 82,  0x60 }, // Numpad 0
        { 83,  0x61 }, // Numpad 1
        { 84,  0x62 }, // Numpad 2
        { 85,  0x63 }, // Numpad 3
        { 86,  0x64 }, // Numpad 4
        { 87,  0x65 }, // Numpad 5
        { 88,  0x66 }, // Numpad 6
        { 89,  0x67 }, // Numpad 7
        { 91,  0x68 }, // Numpad 8
        { 92,  0x69 }, // Numpad 9
        { 65,  0x6E }, // Numpad Decimal
        { 67,  0x6A }, // Numpad *
        { 69,  0x6B }, // Numpad +
        { 71,  0x0C }, // Numpad Clear → VK_CLEAR
        { 75,  0x6F }, // Numpad /
        { 76,  0x0D }, // Numpad Enter (same VK as Return)
        { 78,  0x6D }, // Numpad -
    };

    static readonly Lazy<Dictionary<int, int>> WinToMac = new(() =>
    {
        var d = new Dictionary<int, int>();
        foreach (var kvp in MacToWin)
            d.TryAdd(kvp.Value, kvp.Key);
        return d;
    });

    public static int? MacToWinVk(int macKeyCode) =>
        MacToWin.TryGetValue(macKeyCode, out var vk) ? vk : null;

    public static int? WinVkToMac(int winVk) =>
        WinToMac.Value.TryGetValue(winVk, out var mac) ? mac : null;
}
