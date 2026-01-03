namespace Phoenix.CustomMediator.Abstractions;

public interface IRequestValidator<in TRequest>
{
    /// <summary>
    /// HTTP-like error code used when validation fails (defaults to 400).
    /// </summary>
    int ErrorCode => 400;

    /// <summary>
    /// Return validation error messages (empty means valid).
    /// </summary>
    Task<IReadOnlyList<string>> ValidateAsync(TRequest request, CancellationToken cancellationToken);
}


