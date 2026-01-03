using Microsoft.AspNetCore.Builder;

namespace Phoenix.CustomMediator.Web;
/// <summary>
/// Base class for grouping endpoints using minimal APIs.
/// Supports DI through constructor injection in derived groups.
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
