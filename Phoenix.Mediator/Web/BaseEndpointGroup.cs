using Microsoft.AspNetCore.Routing;

namespace Phoenix.Mediator.Web;
/// <summary>
/// Base class for grouping endpoints using minimal APIs.
/// <para>
/// Constructor injection is supported but is <b>map-time only</b>: each group is instantiated
/// inside a temporary DI scope that is disposed as soon as <see cref="Map(IEndpointRouteBuilder)"/> returns. Any scoped
/// service captured in the constructor and stored on the instance will therefore be referencing a
/// <b>disposed</b> object by the time a request runs. Use constructor dependencies only to read
/// configuration/metadata while building routes — never stash them for use inside the route
/// delegates.
/// </para>
/// <para>
/// For request-scoped services used at execution time (current user, DbContext, etc.), inject them
/// as handler/delegate parameters so the framework resolves them per request.
/// </para>
/// </summary>
public abstract class BaseEndpointGroup
{
    /// <summary>
    /// The group's name: the OpenAPI tag <c>MapEndpoints</c> gives its endpoints (see
    /// <see cref="MapEndpointsOptions.TagEndpointsWithGroupName"/>), and commonly the route prefix
    /// (<c>app.MapGroup(GroupName)</c>). Lower-casing is culture-invariant on purpose: with
    /// <c>ToLower()</c> a Turkish/Azerbaijani server turns <c>InvoiceEndpoints</c> into
    /// <c>ınvoice</c> (dotless i) and serves different URLs than every other machine.
    /// </summary>
    public virtual string GroupName
    {
        get
        {
            const string suffix = "Endpoints";
            var name = GetType().Name;

            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
                name = name[..^suffix.Length];

            return name.ToLowerInvariant();
        }
    }

    /// <summary>
    /// Maps this group's endpoints: <c>app.MapGroup(GroupName).Get(...)</c>.
    /// <para>
    /// <c>MapEndpoints</c> passes a route group rather than the application: one that already carries
    /// <see cref="MapEndpointsOptions.RoutePrefix"/>, <see cref="MapEndpointsOptions.ConfigureEndpoints"/> and this
    /// group's OpenAPI tag, so everything mapped on it gets them. Services a group needs while mapping (configuration,
    /// the environment) come in through its constructor.
    /// </para>
    /// </summary>
    /// <param name="app">Where to map the endpoints. Usually a route group, not the application itself.</param>
    public abstract void Map(IEndpointRouteBuilder app);
}
