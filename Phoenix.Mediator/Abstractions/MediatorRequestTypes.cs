namespace Phoenix.Mediator.Abstractions;

/// <summary>
/// The reflection probes that answer "is this a mediator request, and what does it respond with".
/// <para>
/// These were written out by hand in five places across the mediator, the endpoint helpers and the JSON
/// body resolver. Keeping one copy matters because the three uses have to agree: if the endpoint helpers
/// decide a parameter is a request but the mediator does not, an endpoint advertises a response it can
/// never produce, and if the JSON resolver disagrees with either, route members stay in the body schema.
/// </para>
/// </summary>
internal static class MediatorRequestTypes
{
    /// <summary>
    /// The <c>TResponse</c> of the <see cref="IRequest{TResponse}"/> <paramref name="requestType"/> implements,
    /// or <see langword="null"/> when it implements none.
    /// </summary>
    public static Type? GetResponseType(Type requestType)
    {
        foreach (var contract in requestType.GetInterfaces())
        {
            if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IRequest<>))
                return contract.GetGenericArguments()[0];
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="type"/> is a request type — it implements <see cref="IRequest"/> or
    /// <see cref="IRequest{TResponse}"/>.
    /// </summary>
    public static bool IsRequest(Type type)
    {
        return typeof(IRequest).IsAssignableFrom(type) || GetResponseType(type) is not null;
    }

    /// <summary>
    /// The response type of <paramref name="type"/> whether it IS <see cref="IRequest{TResponse}"/> or merely
    /// implements it, or <see langword="null"/> for a request with no response.
    /// <para>
    /// A minimal-API delegate may declare its parameter as <c>IRequest&lt;TResponse&gt;</c> rather than as the
    /// concrete request, and <see cref="GetResponseType"/> alone misses that: an interface has no interface
    /// list of its own to search, so <c>IRequest&lt;T&gt;</c> does not report itself.
    /// </para>
    /// </summary>
    public static Type? GetDeclaredResponseType(Type type)
    {
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IRequest<>)
            ? type.GetGenericArguments()[0]
            : GetResponseType(type);
    }

    /// <summary>
    /// Whether <paramref name="type"/> is a request type <b>or is one of the request interfaces itself</b>.
    /// <para>
    /// <see cref="IRequest"/> needs no extra case beyond <see cref="IsRequest"/> —
    /// <see cref="Type.IsAssignableFrom"/> already answers true for the same type.
    /// </para>
    /// </summary>
    public static bool IsRequestOrRequestInterface(Type type)
    {
        return GetDeclaredResponseType(type) is not null || typeof(IRequest).IsAssignableFrom(type);
    }
}
