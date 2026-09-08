using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;
using Titanium.Inspector.Views;

namespace Titanium.Inspector;

/// <summary>Shared wiring for desktop App and headless / E2E fixtures.</summary>
public static class InspectorAppFactory
{
    public static MainWindowViewModel CreateViewModel(InspectorViewModelServices services) =>
        new(services);

    public static (MainWindowViewModel ViewModel, MainWindow Window) CreateMainWindow(
        InspectorViewModelServices services)
    {
        ThemeService.ApplyThemeMode(services.Settings.Current.ThemeMode);
        var vm = CreateViewModel(services);
        var window = new MainWindow { DataContext = vm };
        return (vm, window);
    }

    public static (MainWindowViewModel ViewModel, MainWindow Window) CreateMainWindow(
        SettingsService settings,
        SessionStreamBuffer buffer,
        SessionRegistry registry,
        UpdateService updates,
        InterceptionService? interception = null,
        IInspectorDialogs? dialogs = null,
        IInspectorPathPicker? pathPicker = null) =>
        CreateMainWindow(new InspectorViewModelServices(
            buffer, registry, updates, settings, interception, dialogs, pathPicker));
}
