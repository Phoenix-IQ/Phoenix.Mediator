using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using System.Reflection;

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
    /// Maps this group's endpoints: <c>app.MapGroup(GroupName).Get(...)</c>. Override this overload.
    /// <para>
    /// <c>MapEndpoints</c> passes a route group rather than the application: one that already carries
    /// <see cref="MapEndpointsOptions.RoutePrefix"/>, <see cref="MapEndpointsOptions.ConfigureEndpoints"/> and this
    /// group's OpenAPI tag, so everything mapped on it gets them. Services a group needs while mapping (configuration,
    /// the environment) come in through its constructor.
    /// </para>
    /// </summary>
    /// <param name="app">Where to map the endpoints. Usually a route group, not the application itself.</param>
    public virtual void Map(IEndpointRouteBuilder app)
    {
        // A group written before this overload existed overrides the WebApplication one instead, and can still be
        // mapped on the application itself.
        if (OverridesLegacyMap && app is WebApplication application)
        {
            Map(application);
            return;
        }

        throw new InvalidOperationException(OverridesLegacyMap
            ? $"Endpoint group '{GetType().FullName}' overrides Map(WebApplication), so it can only be mapped on the application " +
              "itself. Override Map(IEndpointRouteBuilder) instead to map it under a route group."
            : $"Endpoint group '{GetType().FullName}' must override Map(IEndpointRouteBuilder).");
    }

    /// <summary>
    /// The entry point of earlier versions, kept so existing groups compile and behave as before. A group overriding
    /// it is mapped directly on the application, so <see cref="MapEndpointsOptions.RoutePrefix"/> and
    /// <see cref="MapEndpointsOptions.ConfigureEndpoints"/> cannot reach its endpoints, and <c>MapEndpoints</c> refuses
    /// the combination. Override <see cref="Map(IEndpointRouteBuilder)"/> instead; usually only the parameter type changes.
    /// </summary>
    public virtual void Map(WebApplication app) => Map((IEndpointRouteBuilder)app);

    /// <summary>Whether this group overrides <see cref="Map(WebApplication)"/> but not <see cref="Map(IEndpointRouteBuilder)"/>.</summary>
    internal bool UsesLegacyMap => OverridesLegacyMap && !Overrides(typeof(IEndpointRouteBuilder));

    private bool OverridesLegacyMap => Overrides(typeof(WebApplication));

    private bool Overrides(Type parameterType)
    {
        // Only a real override counts. A same-signature method that hides the virtual instead — `new`, or a group's own
        // public Map(IEndpointRouteBuilder) helper written before the base class had one — is not in the virtual slot, so
        // MapEndpoints would never reach it; its GetBaseDefinition() is itself rather than the base declaration.
        for (var type = GetType(); type is not null && type != typeof(BaseEndpointGroup); type = type.BaseType)
        {
            var method = type.GetMethod(nameof(Map), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, [parameterType]);
            if (method is not null && method.GetBaseDefinition().DeclaringType == typeof(BaseEndpointGroup))
                return true;
        }

        return false;
    }
}
