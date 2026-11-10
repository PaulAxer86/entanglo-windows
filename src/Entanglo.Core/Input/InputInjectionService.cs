using Entanglo.Core.Protocol.Payloads;
using Entanglo.PInvoke;

namespace Entanglo.Core.Input;

/// <summary>Translates incoming InputEventMessage from the wire into Win32 SendInput calls.</summary>
public sealed class InputInjectionService
{
    // Primary screen dimensions for absolute coordinate mapping (physical pixels,
    // per-monitor DPI aware — PerMonitorV2 is set in app.manifest).
    int _screenW = Math.Max(1, User32.GetSystemMetrics(User32.SM_CXSCREEN));
    int _screenH = Math.Max(1, User32.GetSystemMetrics(User32.SM_CYSCREEN));

    // Fractional remainders preserved across relative moves so sub-pixel motion
    // from a slow drag is not lost (accumulates until it rounds to ≥1 pixel).
    double _relRemX;
    double _relRemY;

    public void UpdateScreenSize(int w, int h)
    {
        _screenW = Math.Max(1, w);
        _screenH = Math.Max(1, h);
    }

    public void Inject(InputEventMessage evt)
    {
        switch (evt.Kind)
        {
            case "mouseMove":  InjectMouseMove(evt);  break;
            case "mouseDown":  InjectMouseButton(evt, down: true);  break;
            case "mouseUp":    InjectMouseButton(evt, down: false); break;
            case "scroll":     InjectScroll(evt);     break;
            case "keyDown":    InjectKey(evt, down: true);  break;
            case "keyUp":      InjectKey(evt, down: false); break;
            case "mediaKey":   InjectMediaKey(evt);   break;
        }
    }

    void InjectMouseMove(InputEventMessage evt)
    {
        if (evt.DeltaX.HasValue || evt.DeltaY.HasValue)
        {
            // Relative motion — accumulate fractional remainder so slow drags are not lost
            _relRemX += evt.DeltaX ?? 0;
            _relRemY += evt.DeltaY ?? 0;
            int dx = (int)_relRemX;
            int dy = (int)_relRemY;
            _relRemX -= dx;
            _relRemY -= dy;
            if (dx == 0 && dy == 0) return;
            Send(new User32.INPUT
            {
                type = User32.INPUT_MOUSE,
                u    = { mi = new User32.MOUSEINPUT
                {
                    dx = dx, dy = dy,
                    dwFlags = User32.MOUSEEVENTF_MOVE,
                    dwExtraInfo = User32.INJECTED_EXTRA_INFO,
                }}
            });
        }
        else if (evt.X is double x && evt.Y is double y)
        {
            // Absolute motion: map to [0,65535] over the primary screen, no VIRTUALDESK
            // (VIRTUALDESK would normalise over the whole virtual desktop, inconsistent
            //  with the primary-screen space we advertise in Hello).
            int ax = (int)Math.Clamp(x * 65535.0 / (_screenW - 1), 0, 65535);
            int ay = (int)Math.Clamp(y * 65535.0 / (_screenH - 1), 0, 65535);
            Send(new User32.INPUT
            {
                type = User32.INPUT_MOUSE,
                u    = { mi = new User32.MOUSEINPUT
                {
                    dx = ax, dy = ay,
                    dwFlags = User32.MOUSEEVENTF_MOVE | User32.MOUSEEVENTF_ABSOLUTE,
                    dwExtraInfo = User32.INJECTED_EXTRA_INFO,
                }}
            });
        }
    }

    void InjectMouseButton(InputEventMessage evt, bool down)
    {
        uint flag = evt.Button switch
        {
            0 => down ? User32.MOUSEEVENTF_LEFTDOWN   : User32.MOUSEEVENTF_LEFTUP,
            1 => down ? User32.MOUSEEVENTF_RIGHTDOWN  : User32.MOUSEEVENTF_RIGHTUP,
            2 => down ? User32.MOUSEEVENTF_MIDDLEDOWN : User32.MOUSEEVENTF_MIDDLEUP,
            _ => 0u,
        };
        if (flag == 0) return;

        Send(new User32.INPUT
        {
            type = User32.INPUT_MOUSE,
            u    = { mi = new User32.MOUSEINPUT
            {
                dwFlags = flag,
                dwExtraInfo = User32.INJECTED_EXTRA_INFO,
            }}
        });
    }

    void InjectScroll(InputEventMessage evt)
    {
        if (evt.DeltaY is double dy && dy != 0)
        {
            Send(new User32.INPUT
            {
                type = User32.INPUT_MOUSE,
                u    = { mi = new User32.MOUSEINPUT
                {
                    mouseData = (uint)(int)(dy * 10), // scale to WHEEL_DELTA units
                    dwFlags   = User32.MOUSEEVENTF_WHEEL,
                    dwExtraInfo = User32.INJECTED_EXTRA_INFO,
                }}
            });
        }
        if (evt.DeltaX is double dx && dx != 0)
        {
            Send(new User32.INPUT
            {
                type = User32.INPUT_MOUSE,
                u    = { mi = new User32.MOUSEINPUT
                {
                    mouseData = (uint)(int)(dx * 10),
                    dwFlags   = User32.MOUSEEVENTF_HWHEEL,
                    dwExtraInfo = User32.INJECTED_EXTRA_INFO,
                }}
            });
        }
    }

    void InjectMediaKey(InputEventMessage evt)
    {
        bool pressed = evt.Pressed ?? true;
        int? vk = evt.MediaKey switch
        {
            "volumeUp"    => User32.VK_VOLUME_UP,
            "volumeDown"  => User32.VK_VOLUME_DOWN,
            "mute"        => User32.VK_VOLUME_MUTE,
            "playPause"   => User32.VK_MEDIA_PLAY_PAUSE,
            "next"        => User32.VK_MEDIA_NEXT_TRACK,
            "previous"    => User32.VK_MEDIA_PREV_TRACK,
            "fastForward" => User32.VK_MEDIA_NEXT_TRACK,
            "rewind"      => User32.VK_MEDIA_PREV_TRACK,
            "stop"        => User32.VK_MEDIA_STOP,
            _             => null,
        };
        if (vk == null)
        {
            // brightnessUp/brightnessDown and eject have no Win32 VK — log once and ignore
            Logging.AppLog.Info($"[Input] mediaKey '{evt.MediaKey}' has no Win32 VK equivalent — ignored");
            return;
        }
        Send(new User32.INPUT
        {
            type = User32.INPUT_KEYBOARD,
            u    = { ki = new User32.KEYBDINPUT
            {
                wVk     = (ushort)vk.Value,
                dwFlags = (pressed ? 0u : User32.KEYEVENTF_KEYUP) | User32.KEYEVENTF_EXTENDEDKEY,
                dwExtraInfo = User32.INJECTED_EXTRA_INFO,
            }}
        });
    }

    void InjectKey(InputEventMessage evt, bool down)
    {
        if (evt.KeyCode is not int macKey) return;
        var winVk = Protocol.KeyMap.MacToWinVk(macKey);
        if (winVk == null) return;

        Send(new User32.INPUT
        {
            type = User32.INPUT_KEYBOARD,
            u    = { ki = new User32.KEYBDINPUT
            {
                wVk     = (ushort)winVk.Value,
                dwFlags = down ? 0u : User32.KEYEVENTF_KEYUP,
                dwExtraInfo = User32.INJECTED_EXTRA_INFO,
            }}
        });
    }

    static void Send(User32.INPUT input)
    {
        User32.SendInput(1, [input], System.Runtime.InteropServices.Marshal.SizeOf<User32.INPUT>());
    }
}
