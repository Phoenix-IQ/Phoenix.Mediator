using Microsoft.AspNetCore.Builder;

namespace Phoenix.Mediator.Web;
/// <summary>
/// Base class for grouping endpoints using minimal APIs.
/// <para>
/// Constructor injection is supported but is <b>map-time only</b>: each group is instantiated
/// inside a temporary DI scope that is disposed as soon as <see cref="Map"/> returns. Any scoped
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
    /// Name used for grouping Swagger documentation, and commonly as the route prefix
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
    /// Override to map all endpoints for this group.
    /// </summary>
    public abstract void Map(WebApplication app);
}
