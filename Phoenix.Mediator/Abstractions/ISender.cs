using Microsoft.AspNetCore.Http;

namespace Phoenix.Mediator.Abstractions;

public interface ISender
{
    /// <summary>
    /// Sends a request and returns either:
    /// - the handler response value
    /// - an <see cref="IResult"/> for no-content or error flows
    /// Uses reflection to dispatch. Prefer the generic overloads when types are known at compile time.
    /// </summary>
    Task<object?> Send(object request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a typed request and returns the response directly — no reflection, no boxing.
    /// </summary>
    Task<TResponse> Send<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest<TResponse>;

    /// <summary>
    /// Sends a void request — no reflection, no boxing.
    /// </summary>
    Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest;
}
