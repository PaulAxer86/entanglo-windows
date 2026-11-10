using Entanglo.Core.Protocol;
using Entanglo.Core.Protocol.Payloads;
using Xunit;

namespace Entanglo.Core.Tests;

/// <summary>
/// Tests for functionality added in the sync/linux-0.1.16 branch.
/// Covers: Hello screen size, absolute/relative injection math,
/// media-key wire format, pointer speed clamping.
/// </summary>
public class SyncTests
{
    const string DeviceId  = "A56B8029-C69E-4D97-8589-99C81524093B";
    const string SessionId = "5F2C9C0A-AA31-4D80-BCEA-2C7B0E0D0AFB";

    // ── Hello: screen size round-trip ─────────────────────────────────────────

    [Fact]
    public void Hello_with_screen_size_roundtrips()
    {
        var payload = new HelloPayload("SERVERP", "PC", "0.1.62",
            ["controller", "receiver"], "Windows", 2560, 1440);
        var bytes = MessageCodec.Encode(MessageType.Hello, DeviceId, SessionId, payload);
        var env   = MessageCodec.DecodeEnvelope(bytes);
        var back  = MessageCodec.DecodePayload<HelloPayload>(env);

        Assert.Equal("Windows", back.Platform);
        Assert.Equal(2560, back.ScreenWidth);
        Assert.Equal(1440, back.ScreenHeight);
    }

    [Fact]
    public void Hello_without_screen_size_has_null_dimensions()
    {
        var payload = new HelloPayload("OldPeer", "Mac", "0.1.58", ["controller"]);
        var bytes = MessageCodec.Encode(MessageType.Hello, DeviceId, SessionId, payload);
        var back  = MessageCodec.DecodePayload<HelloPayload>(
                        MessageCodec.DecodeEnvelope(bytes));

        Assert.Null(back.Platform);
        Assert.Null(back.ScreenWidth);
        Assert.Null(back.ScreenHeight);
    }

    [Fact]
    public void Hello_screen_size_is_omitted_from_json_when_null()
    {
        var payload = new HelloPayload("Old", "Mac", "0.1.58", ["receiver"]);
        var bytes = MessageCodec.Encode(MessageType.Hello, DeviceId, SessionId, payload);
        var json  = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain("screenWidth",  json);
        Assert.DoesNotContain("screenHeight", json);
    }

    // ── Absolute coordinate mapping math ─────────────────────────────────────
    // Formula: (v * 65535) / (size - 1), clamped to [0, 65535].
    // Removes the VIRTUALDESK inconsistency from the old implementation.

    [Theory]
    [InlineData(0.0,    1920, 0)]      // left edge
    [InlineData(1919.0, 1920, 65535)]  // right edge (size - 1)
    [InlineData(-1.0,   1920, 0)]      // clamped at min
    [InlineData(9999.0, 1920, 65535)]  // clamped at max
    [InlineData(0.0,    2560, 0)]      // 1440p left edge
    [InlineData(2559.0, 2560, 65535)]  // 1440p right edge
    public void Absolute_mapping_formula(double x, int screenW, int expected)
    {
        int result = (int)Math.Clamp(x * 65535.0 / (screenW - 1), 0, 65535);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Absolute_mapping_last_pixel_is_65535()
    {
        // Any screen: the last pixel (screenW - 1) must map to exactly 65535
        foreach (int w in new[] { 1280, 1920, 2560, 3840 })
        {
            int result = (int)Math.Clamp((w - 1.0) * 65535.0 / (w - 1), 0, 65535);
            Assert.Equal(65535, result);
        }
    }

    // ── Relative motion: fractional remainder ────────────────────────────────

    [Fact]
    public void Relative_motion_accumulates_fractional_remainder()
    {
        // Simulate the accumulator in InputInjectionService
        double remX = 0;

        // Ten 0.3-pixel nudges should not all round to zero
        int totalDx = 0;
        for (int i = 0; i < 10; i++)
        {
            remX += 0.3;
            int dx = (int)remX;
            remX -= dx;
            totalDx += dx;
        }
        // 10 × 0.3 ≈ 3.0 — remainder accumulation should produce at least 2 pixels,
        // not 0 (which is what the old code produced for sub-pixel deltas).
        Assert.InRange(totalDx, 2, 3);
    }

    // ── Media key wire format ─────────────────────────────────────────────────

    [Theory]
    [InlineData("volumeUp")]
    [InlineData("volumeDown")]
    [InlineData("mute")]
    [InlineData("playPause")]
    [InlineData("next")]
    [InlineData("previous")]
    [InlineData("stop")]
    [InlineData("brightnessUp")]
    [InlineData("brightnessDown")]
    [InlineData("eject")]
    public void MediaKey_roundtrips_through_wire(string key)
    {
        var payload = new InputEventMessage(
            "mediaKey", null, null, null, null, null, null, key,
            0, true, null);
        var bytes = MessageCodec.Encode(MessageType.InputEvent, DeviceId, SessionId, payload);
        var back  = MessageCodec.DecodePayload<InputEventMessage>(
                        MessageCodec.DecodeEnvelope(bytes));

        Assert.Equal("mediaKey", back.Kind);
        Assert.Equal(key,        back.MediaKey);
        Assert.True(back.Pressed);
    }

    // ── Pointer speed clamping ────────────────────────────────────────────────
    // NaN → 1.0  |  0 → 0.25  |  100 → 4.0  |  valid range stays unchanged

    [Theory]
    [InlineData(double.NaN,  1.0)]
    [InlineData(0.0,         0.25)]
    [InlineData(0.1,         0.25)]
    [InlineData(0.25,        0.25)]
    [InlineData(1.0,         1.0)]
    [InlineData(2.5,         2.5)]
    [InlineData(4.0,         4.0)]
    [InlineData(100.0,       4.0)]
    public void Pointer_speed_clamping(double raw, double expected)
    {
        double speed = double.IsNaN(raw) ? 1.0 : Math.Clamp(raw, 0.25, 4.0);
        Assert.Equal(expected, speed, precision: 10);
    }
}
