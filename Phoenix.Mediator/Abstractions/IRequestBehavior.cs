namespace Phoenix.Mediator.Abstractions;

/// <summary>
/// A pipeline behavior that runs for every request, with or without a response, so a cross-cutting concern
/// (logging, timing, transactions, caching) is written once. Without it the same logic has to be written twice:
/// as an <see cref="IPipelineBehavior{TRequest, TResponse}"/> for requests with a response and as an
/// <see cref="IPipelineBehavior{TRequest}"/> for requests without one.
/// <para>
/// Register the open generic type once, e.g. <c>services.AddMediatorBehavior(typeof(LoggingBehavior&lt;&gt;))</c>.
/// Constrain <typeparamref name="TRequest"/> on the implementation (<c>where TRequest : ICommand</c>) to limit it
/// to some requests; requests that don't satisfy the constraint skip it. A closed behavior over a supertype
/// (<c>IRequestBehavior&lt;object&gt;</c>, <c>IRequestBehavior&lt;ICommand&gt;</c>) never runs: the container does not
/// apply variance, so it is never asked for in place of <c>IRequestBehavior&lt;CreateOrder&gt;</c>.
/// </para>
/// <para>
/// Behaviors of both shapes run together in registration order, first registered outermost.
/// </para>
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
public interface IRequestBehavior<in TRequest>
{
    /// <summary>
    /// Handles the request: call <paramref name="next"/> to continue down the pipeline, or return without calling
    /// it to short-circuit.
    /// </summary>
    /// <typeparam name="TResponse">
    /// The request's response type, or <see cref="NoResponse"/> for a request without one. A behavior that
    /// short-circuits has to produce one, typically from a cache: <c>return (TResponse)cached;</c>.
    /// </typeparam>
    Task<TResponse> Handle<TResponse>(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}
