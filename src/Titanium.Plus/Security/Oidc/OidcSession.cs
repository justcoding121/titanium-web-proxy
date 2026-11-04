using System.Text.Json;
using System.Text.Json.Serialization;

namespace Titanium.Plus.Security.Oidc;

/// <summary>What the session cookie holds. Only what a later request needs: no ID token, no access token.</summary>
internal sealed class OidcSession
{
    [JsonPropertyName("sub")]
    public string? Subject { get; set; }

    [JsonPropertyName("em")]
    public string? Email { get; set; }

    /// <summary>The member groups that matter: those named by <c>allowedGroups</c>. Empty when no rule uses groups.</summary>
    [JsonPropertyName("gr")]
    public List<string> Groups { get; set; } = [];

    /// <summary>Claim name to value, for the claims <c>passClaims</c> maps to headers.</summary>
    [JsonPropertyName("cl")]
    public Dictionary<string, string> Claims { get; set; } = new(StringComparer.Ordinal);

    /// <summary>When the session ends, whatever the provider says (Unix seconds).</summary>
    [JsonPropertyName("exp")]
    public long ExpiresAt { get; set; }

    /// <summary>When the ID token (or access token) runs out and a refresh is due (Unix seconds).</summary>
    [JsonPropertyName("rx")]
    public long RefreshAt { get; set; }

    [JsonPropertyName("rt")]
    public string? RefreshToken { get; set; }

    public static OidcSession? Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            var session = JsonSerializer.Deserialize<OidcSession>(json);
            session?.Claims ??= new Dictionary<string, string>(StringComparer.Ordinal);
            session?.Groups ??= [];
            return session;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public byte[] ToUtf8() => JsonSerializer.SerializeToUtf8Bytes(this);
}
