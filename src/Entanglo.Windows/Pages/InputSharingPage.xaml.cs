using Entanglo.Core;
using Entanglo.Core.Net;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Entanglo.Windows.Pages;

public sealed partial class InputSharingPage : Page
{
    bool _suppressEvents;

    public InputSharingPage()
    {
        InitializeComponent();
        Load();

        App.Services.Coordinator.PeersChanged += OnPeersChanged;
        Unloaded += (_, _) =>
        {
            App.Services.Coordinator.PeersChanged -= OnPeersChanged;
        };
    }

    void OnPeersChanged() => DispatcherQueue.TryEnqueue(Load);

    void Load()
    {
        _suppressEvents = true;
        var s = App.Services.Settings;

        ShareToggle.IsOn       = s.AutoDialTrusted;   // reuse: share = auto-dial peers
        ReceiveToggle.IsOn     = s.ReceiveRemoteInput;
        EdgeEnabledToggle.IsOn = s.EdgeConfig.Enabled;

        var devices = App.Services.TrustStore.Devices
            .Select(d => new EdgeOption(d.DeviceId, d.DeviceName))
            .ToList();

        PopulateBox(LeftPeerBox,   devices, s.EdgeConfig.LeftPeer);
        PopulateBox(RightPeerBox,  devices, s.EdgeConfig.RightPeer);
        PopulateBox(TopPeerBox,    devices, s.EdgeConfig.TopPeer);
        PopulateBox(BottomPeerBox, devices, s.EdgeConfig.BottomPeer);

        EdgeHint.Text = devices.Count == 0
            ? "Pair a device first under Devices to start mapping edges."
            : "Drag the cursor into an edge of this screen to hand control to the mapped peer.";

        var peers = App.Services.Coordinator.ActivePeers;
        PeerText.Text = peers.Count switch
        {
            0 => "—",
            1 => peers.First().DeviceName,
            _ => $"{peers.Count} connected"
        };
        RoleText.Text = peers.Count == 0 ? "Idle" : "Linked";

        _suppressEvents = false;
    }

    static void PopulateBox(ComboBox box, IReadOnlyList<EdgeOption> devices, string? selectedId)
    {
        box.Items.Clear();
        box.Items.Add(new EdgeOption(null, "(no device)"));
        foreach (var d in devices) box.Items.Add(d);

        int idx = 0;
        if (selectedId != null)
        {
            for (int i = 0; i < box.Items.Count; i++)
            {
                if (box.Items[i] is EdgeOption opt && opt.DeviceId == selectedId)
                {
                    idx = i; break;
                }
            }
        }
        box.SelectedIndex = idx;
    }

    void ShareToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        App.Services.Settings.AutoDialTrusted = ShareToggle.IsOn;
        App.Services.Settings.Save();
    }

    void ReceiveToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        App.Services.Settings.ReceiveRemoteInput = ReceiveToggle.IsOn;
        App.Services.Settings.Save();
    }

    void EdgeEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        App.Services.Settings.EdgeConfig.Enabled = EdgeEnabledToggle.IsOn;
        App.Services.Settings.Save();
    }

    void LeftPeerBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyEdgeSelection(LeftPeerBox, id => App.Services.Settings.EdgeConfig.LeftPeer = id);

    void RightPeerBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyEdgeSelection(RightPeerBox, id => App.Services.Settings.EdgeConfig.RightPeer = id);

    void TopPeerBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyEdgeSelection(TopPeerBox, id => App.Services.Settings.EdgeConfig.TopPeer = id);

    void BottomPeerBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyEdgeSelection(BottomPeerBox, id => App.Services.Settings.EdgeConfig.BottomPeer = id);

    void ApplyEdgeSelection(ComboBox box, Action<string?> assign)
    {
        if (_suppressEvents) return;
        if (box.SelectedItem is not EdgeOption opt) return;
        assign(opt.DeviceId);
        App.Services.Settings.Save();
    }

    void StopButton_Click(object sender, RoutedEventArgs e)
    {
        RoleText.Text = "Idle";
        PeerText.Text = "—";
    }

    sealed class EdgeOption
    {
        public string? DeviceId { get; }
        public string  DeviceName { get; }
        public EdgeOption(string? id, string name) { DeviceId = id; DeviceName = name; }
        public override string ToString() => DeviceName;
    }
}
