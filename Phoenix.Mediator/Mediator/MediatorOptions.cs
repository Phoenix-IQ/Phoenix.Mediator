namespace Phoenix.Mediator.Mediator;

public sealed class MediatorOptions
{
    public EmptyResponseStatusCode EmptyResponseStatusCode { get; set; } = EmptyResponseStatusCode.NoContent;
}

public enum EmptyResponseStatusCode
{
    Ok = 200,
    NoContent = 204
}

internal static class MediatorMessages
{
    public const string InvalidEmptyResponseStatusCode = "Empty response status code must be 200 OK or 204 No Content.";
}

/// <summary>
/// Exposes the resolved <see cref="MediatorOptions"/> from an <c>ISender</c> implementation.
/// The built-in mediator implements this so the Web helpers (e.g. <c>SendAsApiResult</c>) can
/// honor the configured <see cref="MediatorOptions.EmptyResponseStatusCode"/>.
/// <para>
/// If you wrap/decorate <c>ISender</c>, implement this interface and forward to the inner sender;
/// otherwise empty-response mapping falls back to <see cref="EmptyResponseStatusCode.NoContent"/>.
/// </para>
/// </summary>
public interface IMediatorOptionsAccessor
{
    MediatorOptions Options { get; }
}
