using Microsoft.Extensions.Options;

namespace Phoenix.Mediator.Mediator;

public sealed class MediatorOptions
{
    public EmptyResponseStatusCode EmptyResponseStatusCode { get; set; } = EmptyResponseStatusCode.NoContent;

    /// <summary>
    /// What to do at host startup about request types, in the assemblies passed to <c>AddMediator(...)</c>, that no
    /// handler is registered for. Default <see cref="MissingHandlerHandling.None"/>: such a request only fails when it
    /// is first sent. <see cref="MissingHandlerHandling.Throw"/> in Development is a good way to catch a forgotten
    /// handler, or a handler assembly that was never passed to <c>AddMediator(...)</c>, before anyone calls the endpoint.
    /// </summary>
    public MissingHandlerHandling MissingHandlerHandling { get; set; } = MissingHandlerHandling.None;
}

public enum EmptyResponseStatusCode
{
    Ok = 200,
    NoContent = 204
}

/// <summary>
/// What to do at startup when a request type in the scanned assemblies has no handler. Without the check, sending
/// it throws <see cref="InvalidOperationException"/> on first use, usually from an endpoint, as a 500.
/// </summary>
public enum MissingHandlerHandling
{
    /// <summary>Do not check. Default.</summary>
    None = 0,

    /// <summary>Log a warning naming each request type without a handler.</summary>
    Warn = 1,

    /// <summary>Fail host startup with an exception naming each request type without a handler.</summary>
    Throw = 2
}

internal static class MediatorMessages
{
    public const string InvalidEmptyResponseStatusCode = "Empty response status code must be 200 OK or 204 No Content.";
    public const string InvalidMissingHandlerHandling = "MissingHandlerHandling must be None, Warn or Throw.";
}

internal sealed class MediatorOptionsValidator : IValidateOptions<MediatorOptions>
{
    public ValidateOptionsResult Validate(string? name, MediatorOptions options)
    {
        if (options.EmptyResponseStatusCode is not (EmptyResponseStatusCode.Ok or EmptyResponseStatusCode.NoContent))
            return ValidateOptionsResult.Fail(MediatorMessages.InvalidEmptyResponseStatusCode);

        return Enum.IsDefined(options.MissingHandlerHandling)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(MediatorMessages.InvalidMissingHandlerHandling);
    }
}
