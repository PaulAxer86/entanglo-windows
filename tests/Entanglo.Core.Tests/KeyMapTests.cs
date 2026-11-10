using Entanglo.Core.Protocol;
using Xunit;

namespace Entanglo.Core.Tests;

public class KeyMapTests
{
    // ── Original smoke-tests ──────────────────────────────────────────────────

    [Theory]
    [InlineData(0,   0x41)] // A
    [InlineData(36,  0x0D)] // Return
    [InlineData(48,  0x09)] // Tab
    [InlineData(49,  0x20)] // Space
    [InlineData(51,  0x08)] // Backspace
    [InlineData(53,  0x1B)] // Escape
    [InlineData(123, 0x25)] // Left arrow
    [InlineData(124, 0x27)] // Right arrow
    [InlineData(125, 0x28)] // Down arrow
    [InlineData(126, 0x26)] // Up arrow
    [InlineData(122, 0x70)] // F1
    public void Mac_to_Win_known_keys(int macKey, int expectedVk)
    {
        Assert.Equal(expectedVk, KeyMap.MacToWinVk(macKey));
    }

    [Fact]
    public void Unknown_mac_key_returns_null()
    {
        Assert.Null(KeyMap.MacToWinVk(999));
    }

    [Fact]
    public void Win_to_mac_roundtrips_for_return()
    {
        int macReturn = 36;
        int winReturn = 0x0D;
        Assert.Equal(winReturn, KeyMap.MacToWinVk(macReturn));
        Assert.Equal(macReturn, KeyMap.WinVkToMac(winReturn));
    }

    // ── Digit row: verified against HIToolbox/Events.h ────────────────────────
    // Linux had 5, 6, 7 swapped — these assert the corrected values.

    [Theory]
    [InlineData(18, 0x31)] // Mac 18 → VK '1'
    [InlineData(19, 0x32)] // Mac 19 → VK '2'
    [InlineData(20, 0x33)] // Mac 20 → VK '3'
    [InlineData(21, 0x34)] // Mac 21 → VK '4'
    [InlineData(23, 0x35)] // Mac 23 → VK '5'  (not 22 or 26 — verify order)
    [InlineData(22, 0x36)] // Mac 22 → VK '6'
    [InlineData(26, 0x37)] // Mac 26 → VK '7'
    [InlineData(28, 0x38)] // Mac 28 → VK '8'
    [InlineData(25, 0x39)] // Mac 25 → VK '9'
    [InlineData(29, 0x30)] // Mac 29 → VK '0'
    public void Digit_row_mac_to_win(int macKey, int expectedVk)
    {
        Assert.Equal(expectedVk, KeyMap.MacToWinVk(macKey));
    }

    // ── Numpad: digits should map regardless of NumLock state ─────────────────

    [Theory]
    [InlineData(82, 0x60)] // Numpad 0 → VK_NUMPAD0
    [InlineData(83, 0x61)] // Numpad 1 → VK_NUMPAD1
    [InlineData(84, 0x62)] // Numpad 2 → VK_NUMPAD2
    [InlineData(85, 0x63)] // Numpad 3 → VK_NUMPAD3
    [InlineData(86, 0x64)] // Numpad 4 → VK_NUMPAD4
    [InlineData(87, 0x65)] // Numpad 5 → VK_NUMPAD5
    [InlineData(88, 0x66)] // Numpad 6 → VK_NUMPAD6
    [InlineData(89, 0x67)] // Numpad 7 → VK_NUMPAD7
    [InlineData(91, 0x68)] // Numpad 8 → VK_NUMPAD8
    [InlineData(92, 0x69)] // Numpad 9 → VK_NUMPAD9
    [InlineData(65, 0x6E)] // Numpad Decimal → VK_DECIMAL
    [InlineData(67, 0x6A)] // Numpad * → VK_MULTIPLY
    [InlineData(69, 0x6B)] // Numpad + → VK_ADD
    [InlineData(78, 0x6D)] // Numpad - → VK_SUBTRACT
    [InlineData(75, 0x6F)] // Numpad / → VK_DIVIDE
    public void Numpad_digits_and_operators(int macKey, int expectedVk)
    {
        Assert.Equal(expectedVk, KeyMap.MacToWinVk(macKey));
    }

    [Fact]
    public void Numpad_enter_maps_to_enter()
    {
        // Mac keypad Enter (76) must produce VK_RETURN (0x0D) so the receiver
        // treats it the same as the main Return key.
        Assert.Equal(0x0D, KeyMap.MacToWinVk(76));
    }

    // ── Modifier flag bit values (macOS CGEventFlags) ─────────────────────────

    [Fact]
    public void Modifier_CapsLock_bit()
    {
        Assert.Equal(0x10000UL, Entanglo.Core.Protocol.ModifierFlags.CapsLock);
    }

    [Fact]
    public void Modifier_Shift_bit()
    {
        Assert.Equal(0x20000UL, Entanglo.Core.Protocol.ModifierFlags.Shift);
    }

    [Fact]
    public void Modifier_Control_bit()
    {
        Assert.Equal(0x40000UL, Entanglo.Core.Protocol.ModifierFlags.Control);
    }

    [Fact]
    public void Modifier_Option_bit()
    {
        Assert.Equal(0x80000UL, Entanglo.Core.Protocol.ModifierFlags.Option);
    }

    [Fact]
    public void Modifier_Command_bit()
    {
        Assert.Equal(0x100000UL, Entanglo.Core.Protocol.ModifierFlags.Command);
    }

    [Fact]
    public void Modifier_NumericPad_bit()
    {
        Assert.Equal(0x200000UL, Entanglo.Core.Protocol.ModifierFlags.NumericPad);
    }

    [Fact]
    public void Modifier_Function_bit()
    {
        Assert.Equal(0x800000UL, Entanglo.Core.Protocol.ModifierFlags.Function);
    }

    [Fact]
    public void Modifier_bits_are_disjoint()
    {
        ulong all = Entanglo.Core.Protocol.ModifierFlags.CapsLock
                  | Entanglo.Core.Protocol.ModifierFlags.Shift
                  | Entanglo.Core.Protocol.ModifierFlags.Control
                  | Entanglo.Core.Protocol.ModifierFlags.Option
                  | Entanglo.Core.Protocol.ModifierFlags.Command
                  | Entanglo.Core.Protocol.ModifierFlags.NumericPad
                  | Entanglo.Core.Protocol.ModifierFlags.Help
                  | Entanglo.Core.Protocol.ModifierFlags.Function;
        // Count set bits — should equal 8 (one per flag)
        int count = 0;
        for (ulong v = all; v != 0; v &= v - 1) count++;
        Assert.Equal(8, count);
    }

    // ── Modifier key VK codes roundtrip ───────────────────────────────────────

    [Theory]
    [InlineData(56, 0xA0)] // Left Shift  → VK_LSHIFT
    [InlineData(60, 0xA1)] // Right Shift → VK_RSHIFT
    [InlineData(59, 0xA2)] // Left Control → VK_LCONTROL
    [InlineData(62, 0xA3)] // Right Control → VK_RCONTROL
    [InlineData(58, 0xA4)] // Left Option → VK_LMENU
    [InlineData(61, 0xA5)] // Right Option → VK_RMENU
    [InlineData(55, 0x5B)] // Left Command → LWIN
    [InlineData(54, 0x5C)] // Right Command → RWIN
    public void Modifier_keys_mac_to_win(int macKey, int expectedVk)
    {
        Assert.Equal(expectedVk, KeyMap.MacToWinVk(macKey));
    }
}
