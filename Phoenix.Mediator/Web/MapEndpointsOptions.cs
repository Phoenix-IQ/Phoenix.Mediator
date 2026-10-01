using Microsoft.AspNetCore.Routing;

namespace Phoenix.Mediator.Web;

/// <summary>
/// Controls what <see cref="EndpointsExtensions.MapEndpoints(Microsoft.AspNetCore.Builder.WebApplication, MapEndpointsOptions, System.Reflection.Assembly[])"/>
/// sets up alongside endpoint-group discovery. Defaults preserve the original one-call behavior
/// (exception handling + <c>/health</c> both enabled).
/// </summary>
public sealed class MapEndpointsOptions
{
    /// <summary>Register the Phoenix exception-handling middleware. Default <see langword="true"/>.</summary>
    public bool UseExceptionHandling { get; set; } = true;

    /// <summary>Map the health endpoint. Default <see langword="true"/>.</summary>
    public bool MapHealthChecks { get; set; } = true;

    /// <summary>Route pattern for the health endpoint. Default <c>/health</c>.</summary>
    public string HealthCheckPattern { get; set; } = "/health";

    /// <summary>
    /// What to do when the same route and HTTP method is mapped more than once. Default
    /// <see cref="DuplicateEndpointHandling.Throw"/>: routing itself accepts such a mapping and only
    /// fails once a request matches both endpoints, so the mistake would otherwise show up as a 500
    /// the first time someone calls the route. Only endpoints mapped before <c>MapEndpoints</c>
    /// returns are checked; call <see cref="EndpointsExtensions.ValidateNoDuplicateEndpoints"/> after
    /// mapping the rest if you map endpoints outside endpoint groups.
    /// </summary>
    public DuplicateEndpointHandling DuplicateEndpointHandling { get; set; } = DuplicateEndpointHandling.Throw;

    /// <summary>
    /// A route prefix in front of every endpoint group, e.g. <c>"api"</c> or <c>"api/v1"</c>. Default none. The health
    /// endpoint is not prefixed.
    /// <para>
    /// Only groups that override <see cref="BaseEndpointGroup.Map(IEndpointRouteBuilder)"/> can be prefixed: one that
    /// overrides the <c>WebApplication</c> overload maps on the application itself, and is refused while this is set.
    /// </para>
    /// </summary>
    public string? RoutePrefix { get; set; }

    /// <summary>
    /// Conventions for every endpoint the groups map, applied once to the route group they are all mapped under:
    /// <c>endpoints =&gt; endpoints.RequireAuthorization().RequireRateLimiting("default")</c>. An endpoint can still
    /// relax one for itself, e.g. <c>.AllowAnonymous()</c>. Default none.
    /// <para>
    /// Like <see cref="RoutePrefix"/>, it only reaches groups that override
    /// <see cref="BaseEndpointGroup.Map(IEndpointRouteBuilder)"/>. A group still on the <c>WebApplication</c> overload
    /// makes <c>MapEndpoints</c> throw while this is set: a shared <c>RequireAuthorization()</c> that silently skipped
    /// one group would leave it open.
    /// </para>
    /// </summary>
    public Action<RouteGroupBuilder>? ConfigureEndpoints { get; set; }

    /// <summary>
    /// Tag each group's endpoints with its <see cref="BaseEndpointGroup.GroupName"/>, which is how OpenAPI tools group
    /// them, unless the endpoint (or <see cref="ConfigureEndpoints"/>) already sets tags. Default <see langword="true"/>.
    /// Applies to groups that override <see cref="BaseEndpointGroup.Map(IEndpointRouteBuilder)"/>.
    /// </summary>
    public bool TagEndpointsWithGroupName { get; set; } = true;

    /// <summary>Whether a setting is present that a group mapped on the application itself would silently miss.</summary>
    internal bool HasSharedEndpointSettings => !string.IsNullOrEmpty(NormalizedRoutePrefix) || ConfigureEndpoints is not null;

    internal string NormalizedRoutePrefix => RoutePrefix?.Trim().Trim('/') ?? string.Empty;
}
