using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.AspNetCore.Routing.Patterns;
using System.Reflection;
using System.Text;

namespace Phoenix.Mediator.Web;

/// <summary>
/// Finds endpoints that routing would score identically for the same request. ASP.NET Core does not
/// reject these while routes are being built: it throws <c>AmbiguousMatchException</c> (surfacing as a
/// 500) the first time a request matches more than one of them, so a route mapped twice by mistake can
/// sit unnoticed until someone calls it.
/// </summary>
internal static class DuplicateEndpointDetector
{
    /// <summary>Stands in for an endpoint mapped without an HTTP method constraint.</summary>
    internal const string AnyHttpMethod = "*";

    /// <summary>
    /// Matcher policies whose job <see cref="GetMatchKey"/> and the per-method grouping already do. Asking them
    /// would be wrong, not just redundant: every endpoint in a group shares the method, host and content type
    /// these policies look at, yet they still report that they apply, which would hide every duplicate.
    /// Two of them are internal, hence the names.
    /// </summary>
    private static readonly HashSet<string> PoliciesModeledByTheMatchKey = new(StringComparer.Ordinal)
    {
        typeof(HttpMethodMatcherPolicy).FullName!,
        typeof(HostMatcherPolicy).FullName!,
        "Microsoft.AspNetCore.Routing.Matching.AcceptsMatcherPolicy",
        "Microsoft.AspNetCore.Mvc.Routing.ConsumesMatcherPolicy",
    };

    /// <param name="dataSources">Where the endpoints come from.</param>
    /// <param name="matcherPolicies">
    /// The app's registered <see cref="MatcherPolicy"/> services. A group of same-route endpoints that one of
    /// them says it applies to is left alone: that policy chooses between them per request, the way
    /// Asp.Versioning's <c>ApiVersionMatcherPolicy</c> picks the endpoint for the requested API version.
    /// </param>
    public static IReadOnlyList<DuplicateEndpointGroup> Find(IEnumerable<EndpointDataSource> dataSources, IEnumerable<MatcherPolicy>? matcherPolicies = null)
    {
        var separatingPolicies = (matcherPolicies ?? [])
            .Where(static policy => !PoliciesModeledByTheMatchKey.Contains(policy.GetType().FullName ?? string.Empty))
            .ToArray();

        var candidates = dataSources
            .SelectMany(static dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            // Link-generation-only endpoints (MVC conventional routes, MapDynamic*) never match a request.
            .Where(static endpoint => endpoint.Metadata.GetMetadata<ISuppressMatchingMetadata>() is null)
            .ToArray();

        var duplicates = new List<DuplicateEndpointGroup>();

        // Two endpoints only compete when routing gives them the same score: same route shape, same
        // order and same host constraint. Everything else is resolved by a matcher policy instead.
        foreach (var sameShape in candidates.GroupBy(GetMatchKey, StringComparer.Ordinal))
        {
            // Routing gives each endpoint one branch per HTTP method it declares, and endpoints that
            // declare none a branch of their own. An endpoint mapped for every method therefore
            // loses to one mapped for the method being requested and never competes with it.
            var perMethod = sameShape
                .SelectMany(static endpoint => GetHttpMethods(endpoint)
                    .DefaultIfEmpty(AnyHttpMethod)
                    .Select(httpMethod => (httpMethod, endpoint)))
                .GroupBy(static pair => pair.httpMethod, static pair => pair.endpoint, StringComparer.OrdinalIgnoreCase);

            foreach (var method in perMethod)
            {
                var conflicting = method.ToArray();
                if (conflicting.Length < 2)
                    continue;

                // Other endpoint sources legitimately put several endpoints on one route and choose
                // between them with a matcher policy of their own (static assets do this per
                // Content-Encoding). Only report a route where at least two route handlers collide.
                if (conflicting.Count(IsRouteHandler) < 2)
                    continue;

                // Route handlers do this too. API versioning maps the same route and method once per
                // version and lets its matcher policy pick one by the requested version, which is not
                // ambiguous at all. Under the default Throw, reporting it stopped every versioned API
                // from starting.
                if (IsSeparatedByAMatcherPolicy(conflicting, separatingPolicies))
                    continue;

                duplicates.Add(new DuplicateEndpointGroup(
                    method.Key,
                    conflicting[0].RoutePattern.RawText ?? string.Empty,
                    conflicting));
            }
        }

        return duplicates;
    }

    public static string BuildMessage(IReadOnlyList<DuplicateEndpointGroup> duplicates)
    {
        var message = new StringBuilder()
            .Append(duplicates.Count == 1 ? "1 route is" : $"{duplicates.Count} routes are")
            .AppendLine(" mapped more than once. ASP.NET Core does not fail on this while routes are built; it throws AmbiguousMatchException (HTTP 500) the first time a request matches more than one endpoint:")
            .AppendLine();

        foreach (var duplicate in duplicates)
        {
            message.Append("  ").Append(duplicate.Describe()).AppendLine(":");

            foreach (var endpoint in duplicate.Endpoints)
                message.Append("    - ").AppendLine(Describe(endpoint));

            message.AppendLine();
        }

        return message
            .Append("Remove or rename the duplicates, or set MapEndpointsOptions.DuplicateEndpointHandling to Warn or None to allow them.")
            .ToString();
    }

    /// <summary>
    /// Whether a matcher policy other than the ones the match key already models says it applies to these
    /// endpoints, and so may choose between them per request. This only proves the endpoints might be told
    /// apart, not that they always are; it errs towards not reporting, because a false report fails startup.
    /// </summary>
    private static bool IsSeparatedByAMatcherPolicy(RouteEndpoint[] endpoints, MatcherPolicy[] policies)
    {
        foreach (var policy in policies)
        {
            try
            {
                if (policy is INodeBuilderPolicy nodeBuilder && nodeBuilder.AppliesToEndpoints(endpoints))
                    return true;

                if (policy is IEndpointSelectorPolicy selector && selector.AppliesToEndpoints(endpoints))
                    return true;
            }
            catch (Exception)
            {
                // A third-party policy that cannot cope with being asked about this subset gets the benefit
                // of the doubt: the check must not be what stops the app from starting.
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> GetHttpMethods(RouteEndpoint endpoint)
    {
        // An empty list means the same as no metadata at all: the endpoint accepts every method.
        return endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
    }

    /// <summary>
    /// Whether the endpoint came from mapping a handler (minimal API, MVC action, ...) rather than
    /// from an endpoint source that manages several endpoints per route itself.
    /// </summary>
    private static bool IsRouteHandler(RouteEndpoint endpoint)
    {
        return endpoint.Metadata.GetMetadata<MethodInfo>() is not null;
    }

    private static string GetMatchKey(RouteEndpoint endpoint)
    {
        var key = new StringBuilder();

        // Order breaks ties before ambiguity is ever considered, so a lower-priority endpoint
        // (a fallback, for example) on the same route is not a duplicate.
        key.Append(endpoint.Order).Append('|');
        AppendCanonicalPattern(key, endpoint.RoutePattern);
        key.Append('|');

        // RequireHost sends endpoints down separate branches of the matcher.
        var hosts = endpoint.Metadata.GetMetadata<IHostMetadata>()?.Hosts ?? [];
        foreach (var host in hosts.Order(StringComparer.OrdinalIgnoreCase))
            key.Append(host.ToLowerInvariant()).Append(',');

        key.Append('|');

        // So does the accepted content type: ConsumesMatcherPolicy picks between same-route endpoints by the
        // request's Content-Type, and an endpoint that constrains it beats one that does not — the same way a
        // method-specific endpoint beats one mapped for every method. Two endpoints that accept different
        // content types are therefore never ambiguous, which is the documented shape for content negotiation
        // (Accepts<T>(...), [Consumes]) and for API versioning libraries that version by media type. Without
        // this they were reported as duplicates and, under the default Throw, failed the app at startup on a
        // route table routing resolves perfectly well.
        var contentTypes = endpoint.Metadata.GetMetadata<IAcceptsMetadata>()?.ContentTypes ?? [];
        foreach (var contentType in contentTypes.Order(StringComparer.OrdinalIgnoreCase))
            key.Append(contentType.ToLowerInvariant()).Append(',');

        return key.ToString();
    }

    /// <summary>
    /// Renders the route the way the matcher sees it, so routes that differ only in ways matching
    /// ignores (<c>/Users/{id}</c> vs <c>/users/{userId}</c>) produce the same text.
    /// </summary>
    private static void AppendCanonicalPattern(StringBuilder key, RoutePattern pattern)
    {
        foreach (var segment in pattern.PathSegments)
        {
            key.Append('/');

            foreach (var part in segment.Parts)
            {
                switch (part)
                {
                    // Route matching is case-insensitive.
                    case RoutePatternLiteralPart literal:
                        key.Append(literal.Content.ToLowerInvariant());
                        break;

                    case RoutePatternSeparatorPart separator:
                        key.Append(separator.Content);
                        break;

                    // Parameter names are irrelevant to matching. Constraints, optionality and
                    // defaults are not: they change route precedence, so they stay in the key and
                    // keep "{id:int}" from being reported as a duplicate of "{id}".
                    case RoutePatternParameterPart parameter:
                        key.Append('{').Append(parameter.IsCatchAll ? "**" : "*");

                        if (parameter.IsOptional)
                            key.Append('?');

                        foreach (var policy in parameter.ParameterPolicies)
                            key.Append(':').Append(DescribePolicy(policy));

                        if (parameter.Default is not null)
                            key.Append('=').Append(parameter.Default);

                        key.Append('}');
                        break;
                }
            }
        }
    }

    private static string DescribePolicy(RoutePatternParameterPolicyReference policy)
    {
        // Content is null when the constraint was supplied as an object rather than as route text.
        return policy.Content ?? policy.ParameterPolicy?.GetType().FullName ?? string.Empty;
    }

    private static string Describe(RouteEndpoint endpoint)
    {
        var description = endpoint.DisplayName ?? endpoint.RoutePattern.RawText ?? "(unnamed endpoint)";
        var declaringType = GetDeclaringType(endpoint.Metadata.GetMetadata<MethodInfo>());

        return declaringType is null
            ? description
            : $"{description} (mapped in {declaringType.FullName})";
    }

    private static Type? GetDeclaringType(MethodInfo? handler)
    {
        var type = handler?.DeclaringType;

        // A lambda compiles into a closure class nested inside the type that mapped the route, and
        // that outer type is the one the developer has to go and look at.
        while (type is not null && type.Name.StartsWith('<'))
            type = type.DeclaringType;

        return type;
    }
}

/// <summary>One route and method that more than one endpoint matches.</summary>
/// <param name="HttpMethod">The method they collide on, or <c>*</c> when none of them constrains it.</param>
/// <param name="RoutePattern">The route pattern as written.</param>
/// <param name="Endpoints">Every endpoint that matches it.</param>
internal sealed record DuplicateEndpointGroup(string HttpMethod, string RoutePattern, IReadOnlyList<RouteEndpoint> Endpoints)
{
    public string Describe()
    {
        // A group's root is mapped as "orders/", which reads better as "/orders".
        var route = "/" + RoutePattern.Trim('/');

        return HttpMethod == DuplicateEndpointDetector.AnyHttpMethod
            ? $"(any method) {route}"
            : $"{HttpMethod} {route}";
    }
}
