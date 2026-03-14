using Microsoft.AspNetCore.Builder;

namespace Phoenix.Mediator.Web;
/// <summary>
/// Base class for grouping endpoints using minimal APIs.
/// Supports constructor injection for services needed while endpoints are being mapped.
/// Prefer handler-parameter injection for request-scoped services used at execution time.
/// </summary>
public abstract class BaseEndpointGroup
{
    /// <summary>
    /// Name used for grouping Swagger documentation.
    /// </summary>
    public virtual string GroupName => GetType().Name.Replace("Endpoints", "").ToLower();

    /// <summary>
    /// Override to map all endpoints for this group.
    /// </summary>
    public abstract void Map(WebApplication app);
}
