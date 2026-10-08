namespace Titanium.Inspector.ViewModels;

/// <summary>
/// Inspect pane tabs. Values match <see cref="MainWindowViewModel.SelectedInspectTabIndex"/>.
/// Contextual tabs (Diff, WS, SSE, Protobuf) stay in this order and are hidden when they do not apply.
/// </summary>
public enum InspectTab
{
    RequestHeaders = 0,
    RequestBody = 1,
    ResponseHeaders = 2,
    ResponseBody = 3,
    Diff = 4,
    WsFrames = 5,
    Sse = 6,
    Protobuf = 7,
}
