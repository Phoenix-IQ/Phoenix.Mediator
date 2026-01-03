namespace Phoenix.Mediator.Abstractions;

public interface ISender
{
    /// <summary>
    /// Send that returns one of:
    /// - SingleResponse{T}
    /// - MultiResponse{T}
    /// - ErrorsResponse (validation/global exception)
    /// - null (no-content requests)
    /// </summary>
    Task<object?> Send(object request, CancellationToken cancellationToken = default);
}