#if NET6_0_OR_GREATER
namespace Titanium.Web.Proxy.Http2;

/// <summary>
///     SETTINGS frame parameter identifiers (RFC 7540 §6.5.2 / RFC 9113 §6.5.2).
/// </summary>
internal enum Http2SettingsId
{
    HeaderTableSize = 0x1,
    EnablePush = 0x2,
    MaxConcurrentStreams = 0x3,
    InitialWindowSize = 0x4,
    MaxFrameSize = 0x5,
    MaxHeaderListSize = 0x6
}
#endif
