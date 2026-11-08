using System.Runtime.InteropServices;
using Entanglo.Core.Logging;
using Entanglo.Core.Net;
using Entanglo.PInvoke;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace Entanglo.Windows;

/// <summary>
/// Two-way clipboard sync. Reads use Win32 GetClipboardSequenceNumber +
/// OpenClipboard/GetClipboardData polling so they keep working when the
/// main window is hidden / minimized (UWP Clipboard.ContentChanged only
/// fires while the app is foreground). Writes still go through the UWP
/// API since SetContent is safe from background.
/// </summary>
public sealed class ClipboardBridge : IDisposable
{
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    readonly ConnectionCoordinator _coord;
    readonly DispatcherQueue _ui;
    readonly Timer _pollTimer;
    uint _lastSeq;
    string? _lastBroadcast;
    string? _lastReceived;
    bool _disposed;

    public ClipboardBridge(ConnectionCoordinator coord, DispatcherQueue ui)
    {
        _coord = coord;
        _ui    = ui;
        _lastSeq = User32.GetClipboardSequenceNumber();
        _coord.ClipboardReceived += OnRemoteClipboard;
        _pollTimer = new Timer(PollTick, null, PollInterval, PollInterval);
    }

    void PollTick(object? _)
    {
        if (_disposed) return;
        try
        {
            var seq = User32.GetClipboardSequenceNumber();
            if (seq == _lastSeq) return;
            _lastSeq = seq;

            var text = TryReadClipboardText();
            if (string.IsNullOrEmpty(text)) return;
            if (text == _lastReceived) return;   // came from a peer — don't echo
            if (text == _lastBroadcast) return;  // unchanged
            _lastBroadcast = text;
            _ = _coord.BroadcastClipboardAsync(text);
        }
        catch (Exception ex)
        {
            AppLog.Warning($"[Clip] Poll failed: {ex.Message}");
        }
    }

    static string? TryReadClipboardText()
    {
        // Small retry loop — OpenClipboard fails if another process is
        // holding it (racy Explorer paste, etc.).
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (User32.OpenClipboard(nint.Zero)) break;
            Thread.Sleep(10);
            if (attempt == 4) return null;
        }
        try
        {
            if (!User32.IsClipboardFormatAvailable(User32.CF_UNICODETEXT)) return null;
            var handle = User32.GetClipboardData(User32.CF_UNICODETEXT);
            if (handle == nint.Zero) return null;
            var ptr = Kernel32.GlobalLock(handle);
            if (ptr == nint.Zero) return null;
            try { return Marshal.PtrToStringUni(ptr); }
            finally { Kernel32.GlobalUnlock(handle); }
        }
        finally { User32.CloseClipboard(); }
    }

    void OnRemoteClipboard(string fromDeviceId, string text)
    {
        if (_disposed || string.IsNullOrEmpty(text)) return;
        _ui.TryEnqueue(() =>
        {
            try
            {
                _lastReceived  = text;
                _lastBroadcast = text;
                var pkg = new DataPackage();
                pkg.SetText(text);
                Clipboard.SetContent(pkg);
                // Bump our baseline so the immediate SetContent-driven
                // change doesn't ping-pong back out on the next poll.
                _lastSeq = User32.GetClipboardSequenceNumber();
            }
            catch (Exception ex)
            {
                AppLog.Warning($"[Clip] Peer→local write failed: {ex.Message}");
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Dispose();
        _coord.ClipboardReceived -= OnRemoteClipboard;
    }
}
