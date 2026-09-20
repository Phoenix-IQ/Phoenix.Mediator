using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using System.Reflection;
using System.Runtime.Serialization;

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
    /// <para>
    /// Multiple roles passed to one call are OR-combined: the caller must be in at least one of them,
    /// matching ASP.NET Core's <see cref="AuthorizeAttribute.Roles"/> semantics. Calling this more than once
    /// (for example on the group and again on the endpoint) is AND: each call adds its own requirement.
    /// </para>
    /// <para>
    /// The enum member names are used as the role-claim values, so they must match the roles your identity
    /// provider issues, <b>including their casing</b> — the check is case-sensitive. For role values that are
    /// not valid C# identifiers, or that use a different casing, annotate the member with
    /// <see cref="EnumMemberAttribute"/>: <c>[EnumMember(Value = "super-admin")] SuperAdmin</c>.
    /// </para>
    /// <para>
    /// The roles are matched against claims of the identity's role claim type (<see cref="System.Security.Claims.ClaimsIdentity.RoleClaimType"/>,
    /// by default <see cref="System.Security.Claims.ClaimTypes.Role"/>). With JWT bearer tokens that carry a
    /// <c>"role"</c>/<c>"roles"</c> claim and <c>MapInboundClaims = false</c>, set
    /// <c>TokenValidationParameters.RoleClaimType</c> accordingly, or every check fails with 403.
    /// </para>
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

        var rolesString = string.Join(",", roles.Select(GetRoleClaimValue));
        return builder.RequireAuthorization(new AuthorizeAttribute { Roles = rolesString });
    }

    /// <summary>
    /// The role-claim value for an enum member: <see cref="EnumMemberAttribute.Value"/> when present,
    /// otherwise the member name.
    /// </summary>
    private static string GetRoleClaimValue<TRole>(TRole role) where TRole : struct, Enum
    {
        // Rejects undefined values ((AppRole)42 would require the role "42") and [Flags] combinations
        // (Admin | Manager is not a defined member, and ToString() would produce "Admin, Manager" —
        // silently meaning "either role" where the caller most likely meant "both").
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentException(
                $"'{role}' is not a defined {typeof(TRole).Name} member. Pass single, defined enum members; " +
                $"to allow any one of several roles pass them all to one call, e.g. RequireRole(role1, role2).",
                nameof(role));
        }

        var name = role.ToString();
        var value = typeof(TRole)
            .GetField(name, BindingFlags.Public | BindingFlags.Static)
            ?.GetCustomAttribute<EnumMemberAttribute>()
            ?.Value;

        var claimValue = string.IsNullOrWhiteSpace(value) ? name : value;

        // Roles are carried in a single comma-separated string, so a comma inside one would split it.
        if (claimValue.Contains(','))
        {
            throw new ArgumentException(
                $"Role value '{claimValue}' contains a comma, which ASP.NET Core uses to separate roles.",
                nameof(role));
        }

        return claimValue;
    }
}
