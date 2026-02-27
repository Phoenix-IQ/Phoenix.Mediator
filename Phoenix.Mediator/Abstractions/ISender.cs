namespace Phoenix.Mediator.Abstractions;

public interface ISender
{
    /// <summary>
    /// Send that returns one of:
    /// - SingleResponse{T}
    /// - MultiResponse{T}
    /// - ErrorsResponse (validation/global exception)
    /// - IResult (e.g. 204 NoContent for no-content requests)
    /// </summary>
    Task<object?> Send(object request, CancellationToken cancellationToken = default);
}
