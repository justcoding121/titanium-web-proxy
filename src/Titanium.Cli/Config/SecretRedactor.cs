namespace Titanium.Cli.Config;

/// <summary>
///     Masks resolved secret values in CLI console output. A value is registered when it is resolved, and
///     <see cref="AsyncConsole" /> masks every line it writes, so an exception message that carries a secret
///     cannot reach a terminal or a container log. With nothing registered the check is one volatile read.
///     <para>Values shorter than <see cref="MinLength" /> are not masked: they would garble unrelated text.</para>
/// </summary>
internal static class SecretRedactor
{
    public const string Mask = "***";

    public const int MinLength = 4;

    private static readonly object Gate = new();
    private static string[] _secrets = [];

    public static void Register(string? secret)
    {
        if (secret is null || secret.Length < MinLength)
        {
            return;
        }

        lock (Gate)
        {
            if (Array.IndexOf(_secrets, secret) >= 0)
            {
                return;
            }

            // Longest first, so a secret that contains another is masked whole.
            var next = new string[_secrets.Length + 1];
            Array.Copy(_secrets, next, _secrets.Length);
            next[^1] = secret;
            Array.Sort(next, static (a, b) => b.Length.CompareTo(a.Length));
            Volatile.Write(ref _secrets, next);
        }
    }

    public static string Redact(string text)
    {
        var secrets = Volatile.Read(ref _secrets);
        if (secrets.Length == 0 || text.Length == 0)
        {
            return text;
        }

        foreach (var secret in secrets.Where(s => text.Contains(s, StringComparison.Ordinal)))
        {
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>Forgets every registered secret. For tests.</summary>
    internal static void Clear()
    {
        lock (Gate)
        {
            Volatile.Write(ref _secrets, []);
        }
    }
}
