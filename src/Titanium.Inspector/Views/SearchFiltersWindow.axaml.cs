using Titanium.Inspector.Localization;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Views;

/// <summary>
/// Snapshot editor for the session search. Nothing is written until Apply.
/// Cancel (and Escape) discard the copy.
/// </summary>
public partial class SearchFiltersWindow : Window
{
    private readonly ObservableCollection<string> _hosts = new();
    private readonly ObservableCollection<string> _processes = new();
    private string? _applied;

    public SearchFiltersWindow() : this("")
    {
    }

    public SearchFiltersWindow(string query)
    {
        InitializeComponent();
        LanguageService.AttachWindow(this);
        HostList.ItemsSource = _hosts;
        ProcessList.ItemsSource = _processes;
        LoadSnapshot(query);
        ApplyButton.Click += OnApply;
        CancelButton.Click += (_, _) => Close();
        HostAddButton.Click += (_, _) => AddHost();
        ProcessAddButton.Click += (_, _) => AddProcess();
        HostAddBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                AddHost();
            }
        };
        ProcessAddBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                AddProcess();
            }
        };
    }

    /// <summary>The composed query when Apply was clicked; null when the dialog was cancelled.</summary>
    public string? AppliedQuery => _applied;

    /// <summary>Remainder field. Tests compose a snapshot without showing the window.</summary>
    internal TextBox RemainderBoxForTests => RemainderBox;

    internal Button ApplyButtonForTests => ApplyButton;

    internal Button CancelButtonForTests => CancelButton;

    internal TextBox HostAddBoxForTests => HostAddBox;

    /// <summary>
    /// Show the dialog over <paramref name="owner"/>. Returns the new query on Apply, or null on Cancel.
    /// </summary>
    public static async Task<string?> ShowAsync(Window owner, string query)
    {
        var w = new SearchFiltersWindow(query);
        await w.ShowDialog(owner);
        return w.AppliedQuery;
    }

    private void LoadSnapshot(string? query)
    {
        var remainder = SessionSearch.ReplaceHideTokens(query, [], []);
        RemainderBox.Text = remainder;
        foreach (var token in SessionSearch.GetTokens(query))
        {
            if (token.Key == "-host")
            {
                _hosts.Add(token.Value);
            }
            else if (token.Key == "-process")
            {
                _processes.Add(token.Value);
            }
        }
    }

    private void AddHost()
    {
        var value = FirstTokenWord(HostAddBox.Text);
        if (value.Length == 0 || ContainsToken(_hosts, value))
        {
            return;
        }

        _hosts.Add(value);
        HostAddBox.Text = "";
    }

    private void AddProcess()
    {
        var value = FirstTokenWord(ProcessAddBox.Text);
        if (value.Length == 0 || ContainsToken(_processes, value))
        {
            return;
        }

        _processes.Add(value);
        ProcessAddBox.Text = "";
    }

    private void OnRemoveHost(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string host })
        {
            _hosts.Remove(host);
        }
    }

    private void OnRemoveProcess(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string process })
        {
            _processes.Remove(process);
        }
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        // A value still sitting in an add box is part of what the user meant to apply.
        AddHost();
        AddProcess();
        _applied = SessionSearch.ReplaceHideTokens(RemainderBox.Text, _hosts, _processes);
        Close();
    }

    private static string FirstTokenWord(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return "";
        }

        return trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
    }

    private static bool ContainsToken(IEnumerable<string> values, string value) =>
        values.Any(v => v.Equals(value, StringComparison.OrdinalIgnoreCase));
}
