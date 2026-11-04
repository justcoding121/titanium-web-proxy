namespace Titanium.Xds.Translation;

public sealed class TranslationError
{
    public required string ResourceName { get; init; }

    public required string FieldPath { get; init; }

    public required string Message { get; init; }

    public override string ToString() => $"{ResourceName}: {FieldPath}: {Message}";
}

public readonly struct TranslationResult<T>
{
    private TranslationResult(T? value, TranslationError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }

    public TranslationError? Error { get; }

    public bool IsSuccess => Error is null;

    public static TranslationResult<T> Ok(T value) => new(value, null);

    public static TranslationResult<T> Fail(TranslationError error) => new(default, error);
}
