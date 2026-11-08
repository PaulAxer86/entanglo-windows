namespace Entanglo.Core.Input;

/// <summary>
/// Detects when the local user touches their own mouse/keyboard while we are the receiver.
/// Fires ReleaseRequested so ConnectionCoordinator can send releaseControl to the peer.
/// Implementation: piggybacks on InputCaptureService events that weren't injected by us.
/// </summary>
public sealed class LocalInputWatcher
{
    readonly InputCaptureService _capture;
    volatile bool _watching;

    public event Action? ReleaseRequested;

    public LocalInputWatcher(InputCaptureService capture)
    {
        _capture = capture;
        _capture.EventCaptured += OnEvent;
    }

    public void StartWatching() => _watching = true;
    public void StopWatching()  => _watching = false;

    void OnEvent(Protocol.Payloads.InputEventMessage _)
    {
        if (_watching)
        {
            _watching = false; // one-shot until re-armed
            ReleaseRequested?.Invoke();
        }
    }
}
