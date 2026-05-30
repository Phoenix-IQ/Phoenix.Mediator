using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;

namespace Phoenix.Mediator.Web;

/// <summary>
/// Authorization helpers for Minimal-API endpoint builders.
/// </summary>
public static class AuthorizationExtensions
{
    /// <summary>
    /// Requires authentication and (optionally) restricts the endpoint to one of the supplied
    /// enum-typed roles. When no roles are passed this is equivalent to
    /// <see cref="AuthorizationEndpointConventionBuilderExtensions.RequireAuthorization{TBuilder}(TBuilder)"/>
    /// — any authenticated user is allowed.
    /// </summary>
    /// <remarks>
    /// Multiple roles are OR-combined: the caller must be in at least one of them, matching
    /// ASP.NET Core's <see cref="AuthorizeAttribute.Roles"/> semantics. The enum member names
    /// are used as the role-claim values (via <c>ToString()</c>), so the names must match the
    /// role values your identity provider issues.
    /// </remarks>
    /// <typeparam name="TBuilder">The endpoint builder type — preserved so the call chains fluently.</typeparam>
    /// <typeparam name="TRole">An enum whose member names are the role-claim values.</typeparam>
    public static TBuilder RequireRole<TBuilder, TRole>(this TBuilder builder, params TRole[] roles)
        where TBuilder : IEndpointConventionBuilder
        where TRole : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(roles);

        if (roles.Length == 0)
            return builder.RequireAuthorization();

        var rolesString = string.Join(",", roles.Select(r => r.ToString()));
        return builder.RequireAuthorization(new AuthorizeAttribute { Roles = rolesString });
    }
}
