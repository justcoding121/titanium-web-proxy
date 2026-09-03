using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;
using Titanium.Inspector.Views;

namespace Titanium.Inspector;

/// <summary>Shared wiring for desktop App and headless / E2E fixtures.</summary>
public static class InspectorAppFactory
{
    public static MainWindowViewModel CreateViewModel(
        SettingsService settings,
        SessionStreamBuffer buffer,
        SessionRegistry registry,
        UpdateService updates,
        InterceptionService? interception = null,
        IInspectorDialogs? dialogs = null,
        IInspectorPathPicker? pathPicker = null,
        IStatusNotifier? statusNotifier = null) =>
        new(buffer, registry, updates, settings, interception, dialogs, pathPicker, statusNotifier);

    public static (MainWindowViewModel ViewModel, MainWindow Window) CreateMainWindow(
        SettingsService settings,
        SessionStreamBuffer buffer,
        SessionRegistry registry,
        UpdateService updates,
        InterceptionService? interception = null,
        IInspectorDialogs? dialogs = null,
        IInspectorPathPicker? pathPicker = null,
        IStatusNotifier? statusNotifier = null)
    {
        var vm = CreateViewModel(settings, buffer, registry, updates, interception, dialogs, pathPicker, statusNotifier);
        var window = new MainWindow { DataContext = vm };
        return (vm, window);
    }
}
