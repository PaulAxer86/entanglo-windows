using Entanglo.Core.Protocol;
using Xunit;

namespace Entanglo.Core.Tests;

public class KeyMapTests
{
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
}
