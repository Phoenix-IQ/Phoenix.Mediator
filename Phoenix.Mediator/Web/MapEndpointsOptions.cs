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
}
