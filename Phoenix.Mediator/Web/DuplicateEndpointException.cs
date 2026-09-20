namespace Phoenix.Mediator.Web;

/// <summary>
/// Thrown while endpoints are being mapped when the same route and HTTP method is mapped more than
/// once. Without this, routing reports the clash only when a request first matches both endpoints,
/// as an <c>AmbiguousMatchException</c> that surfaces as a 500.
/// </summary>
public sealed class DuplicateEndpointException : InvalidOperationException
{
    /// <param name="message">The report naming each duplicated route and its endpoints.</param>
    /// <param name="routes">The duplicated routes, as <c>"GET /users/{id}"</c>.</param>
    public DuplicateEndpointException(string message, IEnumerable<string>? routes = null)
        : base(message)
    {
        Routes = routes?.ToArray() ?? [];
    }

    /// <summary>The duplicated routes, each as <c>"GET /users/{id}"</c>.</summary>
    public IReadOnlyList<string> Routes { get; }
}
