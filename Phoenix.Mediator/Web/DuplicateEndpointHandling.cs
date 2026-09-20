namespace Phoenix.Mediator.Web;

/// <summary>
/// What to do when more than one endpoint is mapped to the same route and HTTP method. Left alone,
/// ASP.NET Core accepts the mapping and only fails once a request matches both, with an
/// <c>AmbiguousMatchException</c> that surfaces as a 500.
/// </summary>
public enum DuplicateEndpointHandling
{
    /// <summary>Do not look for duplicates.</summary>
    None = 0,

    /// <summary>Log a warning naming each duplicated route and the endpoints that map it.</summary>
    Warn = 1,

    /// <summary>Throw <see cref="DuplicateEndpointException"/> while routes are being mapped. Default.</summary>
    Throw = 2
}
