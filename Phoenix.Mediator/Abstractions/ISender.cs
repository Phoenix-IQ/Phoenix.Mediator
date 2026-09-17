namespace Phoenix.Mediator.Abstractions;

public interface ISender
{
    /// <summary>
    /// Sends a request whose type is resolved at runtime and returns the handler's response,
    /// or <c>null</c> for an <see cref="IRequest"/> (no response).
    /// Exceptions from handlers and pipeline behaviors propagate to the caller; they are not returned.
    /// Prefer the generic overloads when the request type is known at compile time: they avoid the runtime
    /// lookup and boxing. For <see cref="IRequest{TResponse}"/>, pass both type arguments explicitly;
    /// C# can't infer <c>TResponse</c>, so a call without them binds to this overload.
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
