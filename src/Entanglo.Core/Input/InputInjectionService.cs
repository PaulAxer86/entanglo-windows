using Entanglo.Core.Protocol.Payloads;
using Entanglo.PInvoke;

namespace Entanglo.Core.Input;

/// <summary>Translates incoming InputEventMessage from the Mac peer into Win32 SendInput calls.</summary>
public sealed class InputInjectionService
{
    // Screen dimensions for absolute coordinate mapping. Defaults to the
    // primary screen at construction; can be overridden if the sender's
    // coordinate space differs (e.g. multi-monitor virtual desktop).
    int _screenW = Math.Max(1, User32.GetSystemMetrics(User32.SM_CXSCREEN));
    int _screenH = Math.Max(1, User32.GetSystemMetrics(User32.SM_CYSCREEN));

    public void UpdateScreenSize(int w, int h) { _screenW = Math.Max(1, w); _screenH = Math.Max(1, h); }

    public void Inject(InputEventMessage evt)
    {
        switch (evt.Kind)
        {
            case "mouseMove":   InjectMouseMove(evt);  break;
            case "mouseDown":   InjectMouseButton(evt, down: true);  break;
            case "mouseUp":     InjectMouseButton(evt, down: false); break;
            case "scroll":      InjectScroll(evt);     break;
            case "keyDown":     InjectKey(evt, down: true);  break;
            case "keyUp":       InjectKey(evt, down: false); break;
        }
    }

    void InjectMouseMove(InputEventMessage evt)
    {
        if (evt.X is double x && evt.Y is double y)
        {
            // Convert logical coords to absolute (0–65535 range)
            int ax = (int)(x / _screenW * 65535);
            int ay = (int)(y / _screenH * 65535);
            Send(new User32.INPUT
            {
                type = User32.INPUT_MOUSE,
                u    = { mi = new User32.MOUSEINPUT
                {
                    dx = ax, dy = ay,
                    dwFlags = User32.MOUSEEVENTF_MOVE | User32.MOUSEEVENTF_ABSOLUTE | User32.MOUSEEVENTF_VIRTUALDESK,
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
