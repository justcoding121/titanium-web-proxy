using System.ComponentModel;
using Titanium.Inspector.Localization;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _statusWrite;
    private string? _statusFreeDraft;
    private string? _statusErrorKey;

    public bool StatusClass2xx
    {
        get => StatusFilter.HasClass(StatusFilter.FindValue(SearchQuery), '2');
        set
        {
            if (_statusWrite || value == StatusClass2xx)
            {
                return;
            }

            WriteStatus(value, StatusClass3xx, StatusClass4xx, StatusClass5xx, StatusFreeText);
        }
    }

    public bool StatusClass3xx
    {
        get => StatusFilter.HasClass(StatusFilter.FindValue(SearchQuery), '3');
        set
        {
            if (_statusWrite || value == StatusClass3xx)
            {
                return;
            }

            WriteStatus(StatusClass2xx, value, StatusClass4xx, StatusClass5xx, StatusFreeText);
        }
    }

    public bool StatusClass4xx
    {
        get => StatusFilter.HasClass(StatusFilter.FindValue(SearchQuery), '4');
        set
        {
            if (_statusWrite || value == StatusClass4xx)
            {
                return;
            }

            WriteStatus(StatusClass2xx, StatusClass3xx, value, StatusClass5xx, StatusFreeText);
        }
    }

    public bool StatusClass5xx
    {
        get => StatusFilter.HasClass(StatusFilter.FindValue(SearchQuery), '5');
        set
        {
            if (_statusWrite || value == StatusClass5xx)
            {
                return;
            }

            WriteStatus(StatusClass2xx, StatusClass3xx, StatusClass4xx, value, StatusFreeText);
        }
    }

    /// <summary>Free codes and ranges for the Status flyout. The search box stays the source of truth.</summary>
    public string StatusFreeText
    {
        get => _statusFreeDraft ?? StatusFilter.FreeText(StatusFilter.FindValue(SearchQuery));
        set
        {
            if (_statusWrite || string.Equals(value, StatusFreeText, StringComparison.Ordinal))
            {
                return;
            }

            _statusFreeDraft = value ?? "";
            WriteStatus(StatusClass2xx, StatusClass3xx, StatusClass4xx, StatusClass5xx, _statusFreeDraft);
        }
    }

    public string StatusFilterError =>
        _statusErrorKey is null ? "" : LanguageService.Get(_statusErrorKey);

    public bool HasStatusFilterError => _statusErrorKey is not null;

    private void WriteStatus(bool c2, bool c3, bool c4, bool c5, string? free)
    {
        if (_statusWrite)
        {
            return;
        }

        if (!StatusFilter.TryCompose(c2, c3, c4, c5, free, out var value, out var errorKey))
        {
            _statusErrorKey = errorKey;
            _statusWrite = true;
            NotifyStatusChrome(resetFreeText: false);
            _statusWrite = false;
            return;
        }

        _statusErrorKey = errorKey;
        _statusFreeDraft = null;
        _statusWrite = true;
        SearchQuery = string.IsNullOrEmpty(value)
            ? SessionSearch.RemoveKeyedTokens(SearchQuery, "status")
            : SessionSearch.SetKeyedToken(SearchQuery, "status", value);
        NotifyStatusChrome(resetFreeText: true);
        _statusWrite = false;
    }

    private void NotifyStatusChrome(bool resetFreeText)
    {
        if (resetFreeText)
        {
            _statusFreeDraft = null;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusClass2xx)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusClass3xx)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusClass4xx)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusClass5xx)));
        if (resetFreeText)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusFreeText)));
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusFilterError)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasStatusFilterError)));
    }
}
