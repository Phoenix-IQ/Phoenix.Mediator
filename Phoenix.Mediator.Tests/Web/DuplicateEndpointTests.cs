using Asp.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Web;
using Phoenix.Mediator.Tests.Infrastructure;
using Xunit;

namespace Phoenix.Mediator.Tests;

/// <summary>
/// Mapping the same route twice is accepted by routing and only fails when a request arrives, as an
/// AmbiguousMatchException that surfaces as a 500. These cover reporting it while routes are mapped
/// instead, without flagging routes that the matcher can actually tell apart.
/// </summary>
public sealed class DuplicateEndpointTests
{
    [Fact]
    public void ValidateNoDuplicateEndpoints_ThrowsWhenTheSameRouteAndMethodIsMappedTwice()
    {
        var app = CreateBareApp();

        app.MapGroup("users").Get("{id}", (string id) => Results.Ok(id));
        app.MapGroup("users").Get("{id}", (string id) => Results.Ok(id));

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.Equal("GET /users/{id}", Assert.Single(exception.Routes));
    }

    [Fact]
    public void ValidateNoDuplicateEndpoints_ThrowsWhenRoutesDifferOnlyByParameterName()
    {
        var app = CreateBareApp();

        // Routing matches on shape, not on parameter names, so both of these match GET /users/7.
        app.MapGroup("users").Get("{id}", (string id) => Results.Ok(id));
        app.MapGroup("users").Get("{userId}", (string userId) => Results.Ok(userId));

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.StartsWith("GET /users/", Assert.Single(exception.Routes), StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateNoDuplicateEndpoints_ThrowsWhenRoutesDifferOnlyByCase()
    {
        var app = CreateBareApp();

        app.MapGroup("Users").Get("Active", () => Results.Ok());
        app.MapGroup("users").Get("active", () => Results.Ok());

        Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());
    }

    [Fact]
    public void ValidateNoDuplicateEndpoints_ThrowsWhenTwoEndpointsShareOneOfSeveralMethods()
    {
        var app = CreateBareApp();

        app.MapGroup("users").Get("{id}", (string id) => Results.Ok(id));
        app.MapGroup("users").MapMethods("{id}", ["GET", "POST"], (string id) => Results.Ok(id));

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.Equal("GET /users/{id}", Assert.Single(exception.Routes));
    }

    [Fact]
    public void ValidateNoDuplicateEndpoints_ThrowsWhenARouteIsMappedTwiceForEveryMethod()
    {
        var app = CreateBareApp();

        // ASP0022 catches this one at compile time; it cannot see through the group prefixes and
        // custom Map helpers the other cases here use, which is why the check exists.
#pragma warning disable ASP0022
        app.Map("status", () => Results.Ok());
        app.Map("status", () => Results.Ok());
#pragma warning restore ASP0022

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.Equal("(any method) /status", Assert.Single(exception.Routes));
    }

    [Fact]
    public void ValidateNoDuplicateEndpoints_AllowsAMappedMethodAlongsideAnEndpointForEveryMethod()
    {
        var app = CreateBareApp();

        // MapHealthChecks and friends map every method, but routing prefers the endpoint mapped for
        // the method being requested, so this is not ambiguous.
        app.MapGroup("status").Get("/", () => Results.Ok());
        app.Map("status", () => Results.Ok());

        app.ValidateNoDuplicateEndpoints();
    }

    [Fact]
    public void ValidateNoDuplicateEndpoints_ReportNamesTheTypeThatMappedEachRoute()
    {
        var app = CreateBareApp();

        DuplicateRouteMapper.MapPing(app);
        DuplicateRouteMapper.MapPing(app);

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.Contains(typeof(DuplicateRouteMapper).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains("AmbiguousMatchException", exception.Message, StringComparison.Ordinal);
    }

    // Content negotiation puts two handlers on one route and lets ConsumesMatcherPolicy pick by the
    // request's Content-Type. Routing resolves this correctly, so reporting it would fail the app at
    // startup on a route table that works — and under the default Throw, that is an adoption blocker
    // for any app doing content negotiation, [Consumes], or media-type API versioning.
    [Fact]
    public void ValidateNoDuplicateEndpoints_AllowsRoutesTheMatcherSeparatesByContentType()
    {
        var app = CreateBareApp();

        app.MapGroup("orders").Post("/", (DuplicateJsonBody body) => Results.Ok()).Accepts<DuplicateJsonBody>("application/json");
        app.MapGroup("orders").Post("/", (HttpRequest request) => Results.Ok()).Accepts<DuplicateXmlBody>("application/xml");

        app.ValidateNoDuplicateEndpoints();
    }

    // The asymmetric half of the same rule: an endpoint that constrains the content type beats one that
    // does not, exactly as a method-specific endpoint beats one mapped for every method, so the pair is
    // not ambiguous either.
    [Fact]
    public void ValidateNoDuplicateEndpoints_AllowsAContentTypeConstrainedRouteAlongsideAnUnconstrainedOne()
    {
        var app = CreateBareApp();

        app.MapGroup("orders").Post("/", (DuplicateJsonBody body) => Results.Ok()).Accepts<DuplicateJsonBody>("application/json");
        app.MapGroup("orders").Post("/", (HttpRequest request) => Results.Ok());

        app.ValidateNoDuplicateEndpoints();
    }

    // The guard against over-correcting: same route, same method, SAME content type really is ambiguous,
    // so declaring a content type must not become a way to hide a genuine duplicate.
    [Fact]
    public void ValidateNoDuplicateEndpoints_StillThrowsWhenTwoRoutesAcceptTheSameContentType()
    {
        var app = CreateBareApp();

        app.MapGroup("orders").Post("/", (DuplicateJsonBody body) => Results.Ok()).Accepts<DuplicateJsonBody>("application/json");
        app.MapGroup("orders").Post("/", (DuplicateJsonBody body) => Results.Ok()).Accepts<DuplicateJsonBody>("application/json");

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.Equal("POST /orders", Assert.Single(exception.Routes));
    }

    [Fact]
    public void ValidateNoDuplicateEndpoints_AllowsTheSameRouteOnDifferentMethods()
    {
        var app = CreateBareApp();

        app.MapGroup("users").Get("{id}", (string id) => Results.Ok(id));
        app.MapGroup("users").Delete("{id}", (string id) => Results.NoContent());

        app.ValidateNoDuplicateEndpoints();
    }

    [Fact]
    public void ValidateNoDuplicateEndpoints_AllowsRoutesTheMatcherSeparatesByConstraint()
    {
        var app = CreateBareApp();

        // A constraint raises route precedence, so these are scored differently and never ambiguous.
        app.MapGroup("users").Get("{id:int}", (int id) => Results.Ok(id));
        app.MapGroup("users").Get("{slug}", (string slug) => Results.Ok(slug));

        app.ValidateNoDuplicateEndpoints();
    }

    [Fact]
    public void ValidateNoDuplicateEndpoints_AllowsRoutesSeparatedByOrder()
    {
        var app = CreateBareApp();

        app.MapGroup("users").Get("{id}", (string id) => Results.Ok(id));
        app.MapGroup("users").Get("{id}", (string id) => Results.Ok(id)).WithOrder(1);

        app.ValidateNoDuplicateEndpoints();
    }

    [Fact]
    public void ValidateNoDuplicateEndpoints_WarnsInsteadOfThrowing()
    {
        var logs = new RecordingLoggerProvider();
        var app = CreateBareApp(logs);

        app.MapGroup("users").Get("{id}", (string id) => Results.Ok(id));
        app.MapGroup("users").Get("{id}", (string id) => Results.Ok(id));

        app.ValidateNoDuplicateEndpoints(DuplicateEndpointHandling.Warn);

        Assert.Contains(logs.Messages, message => message.Contains("users/{id}", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateNoDuplicateEndpoints_CanBeDisabled()
    {
        var app = CreateBareApp();

        app.MapGroup("users").Get("{id}", (string id) => Results.Ok(id));
        app.MapGroup("users").Get("{id}", (string id) => Results.Ok(id));

        app.ValidateNoDuplicateEndpoints(DuplicateEndpointHandling.None);
    }

    [Fact]
    public async Task MapEndpoints_ThrowsWhenADiscoveredGroupDuplicatesAnAlreadyMappedRoute()
    {
        await using var app = CreateDiscoveryApp();

        // DiscoveredEndpoints maps GET discovered/ping, so mapping it here collides with it.
        app.MapGet("discovered/ping", () => Results.Ok("pong"));

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.MapEndpoints());

        Assert.Equal("GET /discovered/ping", Assert.Single(exception.Routes));
    }

    [Fact]
    public async Task MapEndpoints_CanSkipTheDuplicateCheck()
    {
        await using var app = CreateDiscoveryApp();

        app.MapGet("discovered/ping", () => Results.Ok("pong"));

        app.MapEndpoints(new MapEndpointsOptions
        {
            DuplicateEndpointHandling = DuplicateEndpointHandling.None
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Matcher policies that choose between same-route endpoints per request.
    // ---------------------------------------------------------------------------------------------

    // Asp.Versioning maps one endpoint per version on the same route and method, and its ApiVersionMatcherPolicy picks
    // one by the requested version. Reporting that stopped every versioned API from starting under the default Throw.
    [Fact]
    public void ValidateNoDuplicateEndpoints_AllowsOneEndpointPerApiVersion_WithAVersionSet()
    {
        var app = CreateVersionedApp();
        var versions = app.NewApiVersionSet().HasApiVersion(new ApiVersion(1, 0)).HasApiVersion(new ApiVersion(2, 0)).Build();

        app.MapGroup("orders").Get("/", () => "orders v1").WithApiVersionSet(versions).MapToApiVersion(1.0);
        app.MapGroup("orders").Get("/", () => "orders v2").WithApiVersionSet(versions).MapToApiVersion(2.0);

        app.ValidateNoDuplicateEndpoints();
    }

    [Fact]
    public void ValidateNoDuplicateEndpoints_AllowsOneEndpointPerApiVersion_WithVersionedUrlSegmentGroups()
    {
        var app = CreateVersionedApp();
        var items = app.NewVersionedApi("Items");

        items.MapGroup("api/v{version:apiVersion}/items").HasApiVersion(1.0).Get("/", () => "items v1");
        items.MapGroup("api/v{version:apiVersion}/items").HasApiVersion(2.0).Get("/", () => "items v2");

        app.ValidateNoDuplicateEndpoints();
    }

    // End to end through MapEndpoints and its default Throw, with the versions mapped by an endpoint group: the app
    // starts, and each version still reaches its own endpoint.
    [Fact]
    public async Task MapEndpoints_AGroupWithVersionedRoutes_StartsAndServesEachVersion()
    {
        await using var app = CreateVersionedApp(useTestServer: true, isolated: true);
        app.MapEndpoints(new FakeAssembly("Duplicate.Versioned", typeof(DuplicateVersionedHost<object>.OrdersEndpoints)));
        await app.StartAsync();
        using var client = app.GetTestClient();

        // A minimal API endpoint returning a string writes it as text/plain, not JSON.
        Assert.Equal("orders v1", await client.GetStringAsync("orders?api-version=1.0"));
        Assert.Equal("orders v2", await client.GetStringAsync("orders?api-version=2.0"));
    }

    // Documents the limit of the rule rather than a virtue: a policy that applies only might tell the endpoints apart.
    // Two endpoints on one route AND one API version are genuinely ambiguous, and go unreported — erring this way costs
    // a 500 on that route, erring the other way failed every versioned app at startup.
    [Fact]
    public void ValidateNoDuplicateEndpoints_DoesNotReportTwoEndpointsForTheSameApiVersion()
    {
        var app = CreateVersionedApp();
        var versions = app.NewApiVersionSet().HasApiVersion(new ApiVersion(1, 0)).Build();

        app.MapGroup("orders").Get("/", () => "first").WithApiVersionSet(versions).MapToApiVersion(1.0);
        app.MapGroup("orders").Get("/", () => "second").WithApiVersionSet(versions).MapToApiVersion(1.0);

        app.ValidateNoDuplicateEndpoints();
    }

    // Nothing is special about API versioning: any policy the app registers that applies to the endpoints is trusted.
    [Fact]
    public void ValidateNoDuplicateEndpoints_AllowsEndpointsACustomMatcherPolicySeparates()
    {
        var app = CreateAppWith(static services => services.TryAddEnumerable(ServiceDescriptor.Singleton<MatcherPolicy, DuplicateTenantMatcherPolicy>()));

        app.MapGroup("reports").Get("/", () => Results.Ok("a")).WithMetadata(new DuplicateTenantMetadata("a"));
        app.MapGroup("reports").Get("/", () => Results.Ok("b")).WithMetadata(new DuplicateTenantMetadata("b"));

        app.ValidateNoDuplicateEndpoints();
    }

    // ...but only when it applies: a registered policy is not a blanket exemption for every duplicate.
    [Fact]
    public void ValidateNoDuplicateEndpoints_StillReportsDuplicatesARegisteredPolicyDoesNotApplyTo()
    {
        var app = CreateAppWith(static services => services.TryAddEnumerable(ServiceDescriptor.Singleton<MatcherPolicy, DuplicateTenantMatcherPolicy>()));

        app.MapGroup("reports").Get("/", () => Results.Ok("a"));
        app.MapGroup("reports").Get("/", () => Results.Ok("b"));

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.Equal("GET /reports", Assert.Single(exception.Routes));
    }

    // A policy that cannot answer for this subset of endpoints gets the benefit of the doubt: the check must never be
    // what stops an app from starting.
    [Fact]
    public void ValidateNoDuplicateEndpoints_GivesAPolicyThatThrowsTheBenefitOfTheDoubt()
    {
        var app = CreateAppWith(static services => services.TryAddEnumerable(ServiceDescriptor.Singleton<MatcherPolicy, DuplicateThrowingMatcherPolicy>()));

        app.MapGroup("reports").Get("/", () => Results.Ok("a"));
        app.MapGroup("reports").Get("/", () => Results.Ok("b"));

        app.ValidateNoDuplicateEndpoints();
    }

    private static WebApplication CreateBareApp(ILoggerProvider? loggerProvider = null)
    {
        var builder = TestApps.CreateBuilder(loggerProvider: loggerProvider);

        builder.Services.AddMediator();

        return builder.Build();
    }

    private static WebApplication CreateAppWith(Action<IServiceCollection> configure)
    {
        var builder = TestApps.CreateBuilder();

        builder.Services.AddMediator();
        configure(builder.Services);

        return builder.Build();
    }

    /// <param name="useTestServer">Serve requests in memory.</param>
    /// <param name="isolated">
    /// Use an application name that matches no assembly, so <c>MapEndpoints</c> discovers only the groups it is given and
    /// not every group in this test assembly.
    /// </param>
    private static WebApplication CreateVersionedApp(bool useTestServer = false, bool isolated = false)
    {
        var builder = isolated
            ? WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = "Phoenix.Mediator.Tests.DuplicateVersioned",
                ContentRootPath = AppContext.BaseDirectory,
                EnvironmentName = Environments.Production
            })
            : TestApps.CreateBuilder();

        if (useTestServer)
            builder.WebHost.UseTestServer();

        builder.Services.AddMediator();
        builder.Services.AddApiVersioning(static options =>
            options.ApiVersionReader = ApiVersionReader.Combine(new QueryStringApiVersionReader(), new UrlSegmentApiVersionReader()));

        return builder.Build();
    }

    /// <summary>
    /// An app that can run the full <c>MapEndpoints</c> discovery: every endpoint group in this
    /// assembly is constructed from DI, so their dependencies have to be registered.
    /// </summary>
    private static WebApplication CreateDiscoveryApp()
    {
        var builder = TestApps.CreateBuilder();

        builder.Services.AddScoped<ScopedDependencyMarker>();
        builder.Services.AddScoped<AsyncDisposableDependencyMarker>();
        builder.Services.AddSingleton<ExecutionLog>();
        builder.Services.AddMediator(typeof(DuplicateEndpointTests).Assembly);

        return builder.Build();
    }

    /// <summary>Maps from a named type so the report has a type name to point at.</summary>
    private static class DuplicateRouteMapper
    {
        public static void MapPing(WebApplication app) => app.Get("ping", () => Results.Ok("pong"));
    }

}

/// <summary>Bodies for the content-negotiation cases; distinct types so each endpoint declares its own schema.</summary>
public sealed record DuplicateJsonBody(string Name);

public sealed record DuplicateXmlBody(string Name);

public sealed record DuplicateTenantMetadata(string Tenant);

/// <summary>Chooses between same-route endpoints by an X-Tenant header, the way an app's own routing policy might.</summary>
public sealed class DuplicateTenantMatcherPolicy : MatcherPolicy, IEndpointSelectorPolicy
{
    public override int Order => 0;

    public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints)
        => endpoints.Any(static endpoint => endpoint.Metadata.GetMetadata<DuplicateTenantMetadata>() is not null);

    public Task ApplyAsync(HttpContext httpContext, CandidateSet candidates)
    {
        var tenant = httpContext.Request.Headers["X-Tenant"].ToString();
        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].Endpoint.Metadata.GetMetadata<DuplicateTenantMetadata>()?.Tenant != tenant)
                candidates.SetValidity(i, false);
        }

        return Task.CompletedTask;
    }
}

/// <summary>A third-party policy that cannot answer for an arbitrary subset of endpoints.</summary>
public sealed class DuplicateThrowingMatcherPolicy : MatcherPolicy, IEndpointSelectorPolicy
{
    public override int Order => 0;

    public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints)
        => throw new InvalidOperationException("This policy cannot be asked about a subset of endpoints.");

    public Task ApplyAsync(HttpContext httpContext, CandidateSet candidates) => Task.CompletedTask;
}

/// <summary>Kept out of default discovery; the versioned-routes test passes the closed group in.</summary>
public static class DuplicateVersionedHost<TMarker>
{
    public sealed class OrdersEndpoints : BaseEndpointGroup
    {
        public override void Map(IEndpointRouteBuilder app)
        {
            var versions = app.NewApiVersionSet().HasApiVersion(new ApiVersion(1, 0)).HasApiVersion(new ApiVersion(2, 0)).Build();
            app.MapGroup("orders").Get("/", () => "orders v1").WithApiVersionSet(versions).MapToApiVersion(1.0);
            app.MapGroup("orders").Get("/", () => "orders v2").WithApiVersionSet(versions).MapToApiVersion(2.0);
        }
    }
}
