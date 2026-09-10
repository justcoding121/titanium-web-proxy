using Titanium.Inspector.Services;

namespace Titanium.Inspector.ViewModels;

/// <summary>Constructor bag for <see cref="MainWindowViewModel"/> (keeps the VM under 7 parameters).</summary>
public readonly record struct InspectorViewModelServices(
    SessionStreamBuffer Buffer,
    SessionRegistry Registry,
    UpdateService Updates,
    SettingsService Settings,
    InterceptionService? Interception = null,
    IInspectorDialogs? Dialogs = null,
    IInspectorPathPicker? PathPicker = null,
    IStatusNotifier? StatusNotifier = null);
