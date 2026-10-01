namespace Phoenix.Mediator.Abstractions;

public interface ISender
{
    /// <summary>
    /// Sends a request whose type is resolved at runtime and returns the handler's response,
    /// or <c>null</c> for an <see cref="IRequest"/> (no response).
    /// Exceptions from handlers and pipeline behaviors propagate to the caller; they are not returned.
    /// Prefer the typed overloads when the request type is known at compile time: they avoid boxing the response.
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

    /// <summary>
    /// Sends a request with a response and returns that response, with <typeparamref name="TResponse"/> inferred
    /// from the request: <c>SingleResponse&lt;string&gt; greeting = await sender.Send(query);</c>.
    /// <para>
    /// C# cannot infer <c>TResponse</c> for <see cref="Send{TRequest, TResponse}"/>, because it only appears in a
    /// constraint there; here it is part of the parameter type, so no type arguments are needed. The request is
    /// dispatched by its runtime type.
    /// </para>
    /// <para>
    /// This member has a default implementation, so an existing <see cref="ISender"/> implementation (a decorator
    /// or a test double) keeps compiling; the default forwards to <see cref="Send(object, CancellationToken)"/>.
    /// </para>
    /// </summary>
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        => SenderDefaults.SendThroughObjectOverload(this, request, cancellationToken);
}

/// <summary>The default implementation behind <see cref="ISender.Send{TResponse}(IRequest{TResponse}, CancellationToken)"/>.</summary>
internal static class SenderDefaults
{
    public static async Task<TResponse> SendThroughObjectOverload<TResponse>(ISender sender, IRequest<TResponse> request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return (TResponse)(await sender.Send((object)request, cancellationToken).ConfigureAwait(false))!;
    }
}
