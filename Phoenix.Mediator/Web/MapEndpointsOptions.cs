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
}
