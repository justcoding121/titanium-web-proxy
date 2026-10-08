namespace Titanium.Inspector.ViewModels;

/// <summary>
/// Inspect pane tabs. Values match <see cref="MainWindowViewModel.SelectedInspectTabIndex"/>.
/// Request and Response each show headers and body. Contextual tabs (Diff, WS, SSE, Protobuf)
/// stay in this order and are hidden when they do not apply.
/// </summary>
public enum InspectTab
{
    Request = 0,
    Response = 1,
    Diff = 2,
    WsFrames = 3,
    Sse = 4,
    Protobuf = 5,
}
