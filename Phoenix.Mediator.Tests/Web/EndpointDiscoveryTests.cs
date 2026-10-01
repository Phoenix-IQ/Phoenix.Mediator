using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Tests.Infrastructure;
using Phoenix.Mediator.Web;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Phoenix.Mediator.Tests;

/// <summary>
/// <c>MapEndpoints</c> is the single call a host application makes: it wires the exception-handling
/// middleware, maps <c>/health</c>, discovers every <see cref="BaseEndpointGroup"/> and then refuses to
/// start on a duplicated route. These cover that orchestration, the discovery filters, and the
/// duplicate detector's edge cases that <c>DuplicateEndpointTests</c> does not reach.
/// </summary>
public sealed class DiscoveryEndpointTests
{
    /// <summary>The route <see cref="MapControlDuplicate"/> maps twice, as the report describes it.</summary>
    private const string ControlRoute = "GET /discovery-control/ping";

    // ---------------------------------------------------------------------
    // Argument guards
    // ---------------------------------------------------------------------

    // A null app here means the caller wrote app.MapEndpoints() before Build(); without the guard the
    // failure surfaces much later as a NullReferenceException from inside discovery.
    [Fact]
    public void MapEndpoints_ThrowsWhenTheApplicationIsNull()
    {
        Assert.Equal("app", Assert.Throws<ArgumentNullException>(
            () => EndpointsExtensions.MapEndpoints(null!, Array.Empty<Assembly>())).ParamName);

        Assert.Equal("app", Assert.Throws<ArgumentNullException>(
            () => EndpointsExtensions.MapEndpoints(null!, new MapEndpointsOptions(), Array.Empty<Assembly>())).ParamName);
    }

    [Fact]
    public async Task MapEndpoints_ThrowsWhenOptionsAreNull()
    {
        await using var app = CreateIsolatedApp();

        Assert.Equal("options", Assert.Throws<ArgumentNullException>(
            () => app.MapEndpoints((MapEndpointsOptions)null!, Array.Empty<Assembly>())).ParamName);
    }

    // params Assembly[] is only non-null by convention: an explicit null array (or a null variable
    // holding the assemblies) reaches the method and has to be rejected by name.
    [Fact]
    public async Task MapEndpoints_ThrowsWhenTheAssemblyArrayIsNull()
    {
        await using var app = CreateIsolatedApp();

        Assert.Equal("assemblies", Assert.Throws<ArgumentNullException>(
            () => app.MapEndpoints((Assembly[])null!)).ParamName);

        Assert.Equal("assemblies", Assert.Throws<ArgumentNullException>(
            () => app.MapEndpoints(new MapEndpointsOptions(), (Assembly[])null!)).ParamName);
    }

    [Fact]
    public void ValidateNoDuplicateEndpoints_ThrowsWhenTheApplicationIsNull()
    {
        Assert.Equal("app", Assert.Throws<ArgumentNullException>(
            () => EndpointsExtensions.ValidateNoDuplicateEndpoints(null!)).ParamName);
    }

    [Fact]
    public void UsePhoenixExceptionHandling_ThrowsWhenTheApplicationIsNull()
    {
        Assert.Equal("app", Assert.Throws<ArgumentNullException>(
            () => EndpointsExtensions.UsePhoenixExceptionHandling(null!)).ParamName);
    }

    [Fact]
    public void MapPhoenixHealthChecks_ThrowsWhenTheApplicationIsNull()
    {
        Assert.Equal("app", Assert.Throws<ArgumentNullException>(
            () => EndpointsExtensions.MapPhoenixHealthChecks(null!)).ParamName);
    }

    // ---------------------------------------------------------------------
    // MapEndpointsOptions defaults
    // ---------------------------------------------------------------------

    [Fact]
    public void MapEndpointsOptions_DefaultsToExceptionHandlingHealthChecksAndThrowingOnDuplicates()
    {
        var options = new MapEndpointsOptions();

        Assert.True(options.UseExceptionHandling);
        Assert.True(options.MapHealthChecks);
        Assert.Equal("/health", options.HealthCheckPattern);
        Assert.Equal(DuplicateEndpointHandling.Throw, options.DuplicateEndpointHandling);

        // The enum's zero value is None, so the property initializer is the only thing selecting Throw.
        // If it is ever dropped, duplicate detection turns itself off silently instead of loudly.
        Assert.Equal(DuplicateEndpointHandling.None, default(DuplicateEndpointHandling));
    }

    // ---------------------------------------------------------------------
    // Duplicate handling modes, and the fluent return values
    // ---------------------------------------------------------------------

    // Three handling modes over an app with nothing duplicated: the check has to stay quiet in all of
    // them. A false positive here — two methods on one route, a parameter route beside a literal one —
    // would refuse to start every app that maps an ordinary REST resource.
    [Theory]
    [InlineData(DuplicateEndpointHandling.None)]
    [InlineData(DuplicateEndpointHandling.Warn)]
    [InlineData(DuplicateEndpointHandling.Throw)]
    public async Task ValidateNoDuplicateEndpoints_ReportsNothingWhenNoRouteIsDuplicated(DuplicateEndpointHandling handling)
    {
        var logs = new RecordingLoggerProvider();
        await using var app = CreateIsolatedApp(logs);

        app.MapGroup("discovery-clean").Get("ping", static () => Results.Ok());
        app.MapGroup("discovery-clean").Post("ping", static () => Results.Ok());
        app.MapGroup("discovery-clean").Get("{id}", static (string id) => Results.Ok(id));

        Assert.Same(app, app.ValidateNoDuplicateEndpoints(handling));
        Assert.DoesNotContain(logs.Warnings, entry => entry.Message.Contains("mapped more than once", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateNoDuplicateEndpoints_WarnsOncePerDuplicatedRouteAndReturnsTheSameApplication()
    {
        var logs = new RecordingLoggerProvider();
        await using var app = CreateIsolatedApp(logs);

        MapControlDuplicate(app);

        // Warn must stay usable in a chain: an app that opted out of throwing still wants the rest of
        // its startup expression to run.
        Assert.Same(app, app.ValidateNoDuplicateEndpoints(DuplicateEndpointHandling.Warn));

        // One record for the route, not one per colliding endpoint, and at Warning rather than Error:
        // an app that chose Warn asked to be told, not to be paged.
        var warning = Assert.Single(
            logs.Warnings,
            entry => entry.Message.Contains("discovery-control/ping", StringComparison.Ordinal));

        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.StartsWith("1 route is mapped more than once.", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapPhoenixHealthChecks_DefaultsToSlashHealthAndReturnsTheSameApplication()
    {
        await using var app = CreateIsolatedApp();

        Assert.Same(app, app.MapPhoenixHealthChecks());
        Assert.Equal(1, CountEndpoints(app, "/health"));
    }

    // ---------------------------------------------------------------------
    // Discovery sources
    // ---------------------------------------------------------------------

    // The documented way to map groups that live in a class library: hand MapEndpoints that assembly.
    [Fact]
    public async Task MapEndpoints_MapsGroupsFoundInAnAssemblyPassedExplicitly()
    {
        await using var app = CreateIsolatedApp();

        app.MapEndpoints(new FakeAssembly("Discovery.Explicit", typeof(DiscoveryGroupHost<object>.PingEndpoints)));

        Assert.Equal(1, CountEndpoints(app, "discovery-explicit/ping"));
    }

    // An assembly reaches discovery from several places at once (application name, entry assembly,
    // AddMediator registry, explicit argument). Without de-duplication every group in it would be
    // mapped twice and the duplicate check would then fail the app at startup.
    [Fact]
    public async Task MapEndpoints_MapsAGroupOnceWhenTheSameAssemblyIsPassedTwice()
    {
        await using var app = CreateIsolatedApp();
        var assembly = new FakeAssembly("Discovery.Repeated", typeof(DiscoveryGroupHost<object>.RepeatedEndpoints));

        app.MapEndpoints(assembly, assembly);

        Assert.Equal(1, CountEndpoints(app, "discovery-repeated/ping"));
    }

    // AddMediator(assembly) is how a host points the mediator at its handlers; discovery reads the same
    // registry, so those assemblies' endpoint groups are mapped by a bare MapEndpoints(). A host that
    // relies on this passes no assemblies to MapEndpoints at all.
    [Fact]
    public async Task MapEndpoints_MapsGroupsFromAssembliesRegisteredWithAddMediator()
    {
        await using var app = CreateAppWithRegisteredAssemblies(
            new FakeAssembly("Discovery.Registered", typeof(DiscoveryGroupHost<object>.RegistryEndpoints)));

        app.MapEndpoints();

        Assert.Equal(1, CountEndpoints(app, "discovery-registry/ping"));
    }

    // The overlap that actually happens in a real host: the same assembly is both registered with
    // AddMediator and named again at MapEndpoints. De-duplication has to span the two sources, or the
    // group maps twice and the duplicate check then refuses to start the app.
    [Fact]
    public async Task MapEndpoints_MapsAGroupOnceWhenTheSameAssemblyIsRegisteredAndPassedExplicitly()
    {
        var assembly = new FakeAssembly("Discovery.RegisteredAndPassed", typeof(DiscoveryGroupHost<object>.RegistryEndpoints));
        await using var app = CreateAppWithRegisteredAssemblies(assembly);

        app.MapEndpoints(assembly);

        Assert.Equal(1, CountEndpoints(app, "discovery-registry/ping"));
    }

    // Dynamic assemblies (a mocking proxy module, a Razor-compiled assembly) hold no endpoint groups a
    // host wrote and cannot be reasoned about the same way; discovery skips them rather than reflecting
    // over them. The second assembly is the control: it proves the skip is selective, not a no-op run.
    [Fact]
    public async Task MapEndpoints_SkipsDynamicAssemblies()
    {
        await using var app = CreateIsolatedApp();

        app.MapEndpoints(
            new DiscoveryDynamicAssembly(typeof(DiscoveryGroupHost<object>.DynamicEndpoints)),
            new FakeAssembly("Discovery.Static", typeof(DiscoveryGroupHost<object>.PingEndpoints)));

        Assert.Equal(0, CountEndpoints(app, "discovery-dynamic/ping"));
        Assert.Equal(1, CountEndpoints(app, "discovery-explicit/ping"));
    }

    // typeof(SomeType).Assembly on a type resolved at runtime, or an array built from configuration, can
    // hand MapEndpoints a null element. Startup must skip it instead of failing with a
    // NullReferenceException that names neither the caller nor the offending entry.
    [Fact]
    public async Task MapEndpoints_SkipsNullEntriesInTheAssemblyArray()
    {
        await using var app = CreateIsolatedApp();

        var assemblies = new Assembly[]
        {
            null!,
            new FakeAssembly("Discovery.AfterNull", typeof(DiscoveryGroupHost<object>.NullEntryEndpoints))
        };

        app.MapEndpoints(assemblies);

        // Mapped, so the null did not stop the scan before reaching the assembly behind it either.
        Assert.Equal(1, CountEndpoints(app, "discovery-nullentry/ping"));
    }

    // Discovery filters types it cannot construct on purpose (abstract, open generic). A group it *can*
    // see but whose dependency was never registered is a different thing: silently skipping it would
    // ship an app that answers 404 on routes the developer wrote, so the failure has to reach startup.
    [Fact]
    public async Task MapEndpoints_FailsWhenADiscoveredGroupsDependencyIsNotRegistered()
    {
        await using var app = CreateIsolatedApp();

        var exception = Record.Exception(() => app.MapEndpoints(
            new FakeAssembly("Discovery.MissingDependency", typeof(DiscoveryGroupHost<object>.NeedsDependencyEndpoints))));

        Assert.NotNull(exception);
        Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Contains("NeedsDependencyEndpoints", exception!.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, CountEndpoints(app, "discovery-needs-dependency/ping"));
    }

    // Each skipped type would fail the whole application at startup if discovery tried to construct it:
    // an abstract or open generic group cannot be instantiated, and an unrelated type cannot be cast to
    // BaseEndpointGroup. A library shipping a CrudEndpoints<TEntity> base class must stay startable.
    [Fact]
    public async Task MapEndpoints_SkipsAbstractOpenGenericAndUnrelatedTypes()
    {
        await using var app = CreateIsolatedApp();

        var assembly = new FakeAssembly(
            "Discovery.Mixed",
            typeof(DiscoveryGroupHost<object>.AbstractEndpoints),
            typeof(DiscoveryGroupHost<>.PingEndpoints),
            typeof(DiscoveryGroupHost<object>.NotAnEndpointGroup),
            typeof(DiscoveryGroupHost<object>.MixedEndpoints));

        app.MapEndpoints(assembly);

        Assert.Equal(1, CountEndpoints(app, "discovery-mixed/ping"));
        // The open generic form of PingEndpoints would have mapped this route had it been constructed.
        Assert.Equal(0, CountEndpoints(app, "discovery-explicit/ping"));
    }

    // A plugin assembly referencing something that was not deployed must not take the whole app down:
    // the groups that did load are still mapped, and the loss is reported rather than swallowed.
    [Fact]
    public async Task MapEndpoints_WarnsAndMapsWhatLoadedWhenAnAssemblyOnlyPartiallyLoads()
    {
        var logs = new RecordingLoggerProvider();
        await using var app = CreateIsolatedApp(logs);

        app.MapEndpoints(new DiscoveryPartiallyLoadableAssembly(typeof(DiscoveryGroupHost<object>.PartialEndpoints)));

        Assert.Equal(1, CountEndpoints(app, "discovery-partial/ping"));
        Assert.Contains(logs.Warnings, entry =>
            entry.Exception is ReflectionTypeLoadException
            && entry.Message.Contains(DiscoveryPartiallyLoadableAssembly.Name, StringComparison.Ordinal)
            && entry.Message.Contains("endpoint discovery", StringComparison.Ordinal)
            // One of the two types loaded. The counts are the only part of the warning that tells the
            // reader how much is missing, and they are easy to invert (counting the failures instead).
            && entry.Message.Contains("1 of 2", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------
    // BaseEndpointGroup.GroupName
    // ---------------------------------------------------------------------

    // GroupName is commonly used as the route prefix, so every one of these spellings decides a URL.
    // (The Turkish/Azerbaijani lower-casing case is covered in ReviewFixTests.)
    [Theory]
    [InlineData(typeof(DiscoveryGroupHost<object>.OrdersEndpoints), "orders")]
    [InlineData(typeof(DiscoveryGroupHost<object>.InvoiceArchiveEndpoints), "invoicearchive")]
    [InlineData(typeof(DiscoveryGroupHost<object>.Reports), "reports")]
    // "Endpoints" on its own has nothing before the suffix, so stripping it would leave an empty prefix.
    [InlineData(typeof(DiscoveryGroupHost<object>.Endpoints), "endpoints")]
    // The suffix is only stripped from the end; a name that merely starts with it keeps every letter.
    [InlineData(typeof(DiscoveryGroupHost<object>.EndpointsArchive), "endpointsarchive")]
    public void GroupName_DerivesThePrefixFromTheTypeName(Type groupType, string expected)
    {
        var group = (BaseEndpointGroup)Activator.CreateInstance(groupType)!;

        Assert.Equal(expected, group.GroupName);
    }

    [Fact]
    public void GroupName_CanBeOverriddenByTheGroup()
    {
        Assert.Equal("discovery-custom", new DiscoveryGroupHost<object>.OverriddenEndpoints().GroupName);
    }

    // ---------------------------------------------------------------------
    // Health checks
    // ---------------------------------------------------------------------

    [Fact]
    public async Task MapEndpoints_MapsTheHealthEndpointAtTheDefaultPattern()
    {
        await using var app = CreateIsolatedApp();

        Assert.Same(app, app.MapEndpoints());
        Assert.Equal(1, CountEndpoints(app, "/health"));
    }

    [Fact]
    public async Task MapEndpoints_UsesTheConfiguredHealthCheckPattern()
    {
        await using var app = CreateIsolatedApp();

        app.MapEndpoints(new MapEndpointsOptions { HealthCheckPattern = "/discovery-healthz" });

        Assert.Equal(1, CountEndpoints(app, "/discovery-healthz"));
        Assert.Equal(0, CountEndpoints(app, "/health"));
    }

    // An app that maps its own authenticated health endpoint turns this off; a custom pattern left
    // behind in the options must not resurrect the public one.
    [Fact]
    public async Task MapEndpoints_MapsNoHealthEndpointWhenHealthChecksAreDisabled()
    {
        await using var app = CreateIsolatedApp();

        app.MapEndpoints(new MapEndpointsOptions
        {
            MapHealthChecks = false,
            HealthCheckPattern = "/discovery-healthz"
        });

        Assert.Equal(0, CountEndpoints(app, "/discovery-healthz"));
        Assert.Equal(0, CountEndpoints(app, "/health"));
    }

    // The health endpoint is public and unauthenticated. Check names and descriptions name internal
    // infrastructure ("sql-primary", a connection string host), so the body deliberately carries the
    // overall status and nothing else.
    [Fact]
    public async Task MapPhoenixHealthChecks_ReportsOnlyTheOverallStatus()
    {
        await using var app = CreateTestServerApp(services => services
            .AddHealthChecks()
            .AddCheck("discovery-internal-database", static () => HealthCheckResult.Healthy("host=db-prod-01")));

        app.MapPhoenixHealthChecks();
        await app.StartAsync();

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(body);
        Assert.Single(document.RootElement.EnumerateObject());
        Assert.Equal("Healthy", document.RootElement.GetProperty("status").GetString());
        Assert.DoesNotContain("discovery-internal-database", body, StringComparison.Ordinal);
        Assert.DoesNotContain("db-prod-01", body, StringComparison.Ordinal);
    }

    // The failure path is the one that matters operationally and the one where detail usually leaks: a
    // failing check's description and exception are exactly what a diagnostic writer would print. The
    // status code has to change too — a load balancer reading 200 keeps routing traffic at a dead node.
    [Fact]
    public async Task MapPhoenixHealthChecks_ReportsUnhealthyWithoutNamingTheFailedCheck()
    {
        await using var app = CreateTestServerApp(services => services
            .AddHealthChecks()
            .AddCheck("discovery-internal-queue", static () => HealthCheckResult.Unhealthy(
                "mq-prod-07 refused the credentials",
                new InvalidOperationException("discovery-queue-credentials"))));

        app.MapPhoenixHealthChecks();
        await app.StartAsync();

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(body);
        Assert.Single(document.RootElement.EnumerateObject());
        Assert.Equal("Unhealthy", document.RootElement.GetProperty("status").GetString());
        Assert.DoesNotContain("discovery-internal-queue", body, StringComparison.Ordinal);
        Assert.DoesNotContain("mq-prod-07", body, StringComparison.Ordinal);
        Assert.DoesNotContain("discovery-queue-credentials", body, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // Exception-handling middleware
    // ---------------------------------------------------------------------

    // The whole point of wiring the middleware from MapEndpoints: a handler that throws answers with the
    // API's error envelope instead of an empty 500, and the exception text never reaches the caller.
    [Fact]
    public async Task MapEndpoints_TurnsAnUnhandledEndpointExceptionIntoAJsonErrorResponse()
    {
        await using var app = CreateTestServerApp();

        app.MapGet("discovery-pipeline/boom", static () => { throw new InvalidOperationException("discovery-boom"); });
        app.MapEndpoints(new MapEndpointsOptions { MapHealthChecks = false });

        await app.StartAsync();

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("discovery-pipeline/boom");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.TryGetProperty("errors", out var errors));
        Assert.Equal(JsonValueKind.Array, errors.ValueKind);
        Assert.True(document.RootElement.TryGetProperty("traceId", out _));
        Assert.DoesNotContain("discovery-boom", body, StringComparison.Ordinal);
    }

    // With UseExceptionHandling = false the caller promises to register the middleware themselves, so
    // MapEndpoints must leave the pipeline alone: nothing converts the failure into a response and the
    // exception escapes to the server (TestServer hands it straight back to the client).
    [Fact]
    public async Task MapEndpoints_LeavesTheExceptionMiddlewareOutWhenExceptionHandlingIsDisabled()
    {
        await using var app = CreateTestServerApp();

        app.MapGet("discovery-pipeline/unhandled", static () => { throw new InvalidOperationException("discovery-unhandled"); });
        app.MapEndpoints(new MapEndpointsOptions { UseExceptionHandling = false, MapHealthChecks = false });

        await app.StartAsync();

        using var client = app.GetTestClient();

        var exception = await Record.ExceptionAsync(() => client.GetAsync("discovery-pipeline/unhandled"));

        Assert.NotNull(exception);
        Assert.Contains("discovery-unhandled", exception!.ToString(), StringComparison.Ordinal);
    }

    // The other half of that opt-out, and the composition the README documents: registering the
    // middleware yourself has to give the same envelope MapEndpoints would have wired, at whatever
    // point in the pipeline the caller chose. Asserting only that the call returns the app would pass
    // even if it registered nothing.
    [Fact]
    public async Task UsePhoenixExceptionHandling_HandlesEndpointExceptionsWhenComposedManually()
    {
        await using var app = CreateTestServerApp();

        Assert.Same(app, app.UsePhoenixExceptionHandling());
        app.MapGet("discovery-manual/boom", static () => { throw new InvalidOperationException("discovery-manual-boom"); });

        await app.StartAsync();

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("discovery-manual/boom");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.TryGetProperty("errors", out var errors));
        Assert.Equal(JsonValueKind.Array, errors.ValueKind);
        Assert.DoesNotContain("discovery-manual-boom", body, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // Duplicate detection: how MapEndpoints drives it
    // ---------------------------------------------------------------------

    // MapEndpointsOptions.DuplicateEndpointHandling has to reach the check, not just the two states a
    // boolean would cover. Warn is what a team turns on to ship past a known clash, and getting it
    // wrong either way is expensive: hard-coded Throw blocks the deploy, hard-coded None loses the
    // report they turned it on for.
    [Fact]
    public async Task MapEndpoints_WarnsInsteadOfThrowingWhenTheOptionsSayWarn()
    {
        var logs = new RecordingLoggerProvider();
        await using var app = CreateIsolatedApp(logs);

        MapControlDuplicate(app);

        app.MapEndpoints(new MapEndpointsOptions
        {
            MapHealthChecks = false,
            DuplicateEndpointHandling = DuplicateEndpointHandling.Warn
        });

        Assert.Contains(logs.Warnings, entry => entry.Message.Contains("discovery-control/ping", StringComparison.Ordinal));
    }

    // The README's caveat, both halves of it: MapEndpoints only sees what is mapped by the time it
    // returns, and the follow-up call is what catches the rest. If MapEndpoints ever started deferring
    // the check to startup the first half would break silently, and a duplicate mapped afterwards is
    // exactly the case the documented follow-up call exists for.
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_CatchesDuplicatesMappedAfterMapEndpointsReturned()
    {
        await using var app = CreateIsolatedApp();

        app.MapEndpoints(new MapEndpointsOptions { MapHealthChecks = false });

        MapControlDuplicate(app);

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.Equal(ControlRoute, Assert.Single(exception.Routes));
    }

    // ---------------------------------------------------------------------
    // Duplicate detection: routes the matcher can tell apart
    // ---------------------------------------------------------------------

    // RequireHost puts the endpoints on separate branches of the matcher, so a multi-tenant app mapping
    // the same path per host is not ambiguous and must not be refused at startup.
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_AllowsTheSameRouteOnDifferentHosts()
    {
        await using var app = CreateIsolatedApp();
        MapControlDuplicate(app);

        app.MapGroup("discovery-hosts").Get("ping", static () => Results.Ok()).RequireHost("a.example.com");
        app.MapGroup("discovery-hosts").Get("ping", static () => Results.Ok()).RequireHost("b.example.com");

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.Equal(ControlRoute, Assert.Single(exception.Routes));
    }

    // Host matching is case-insensitive, so two endpoints pinned to the same host in different casing
    // really do collide and must still be reported.
    [Theory]
    [InlineData("a.example.com", "a.example.com")]
    [InlineData("a.example.com", "A.EXAMPLE.COM")]
    public async Task ValidateNoDuplicateEndpoints_ThrowsWhenTwoEndpointsRequireTheSameHost(string first, string second)
    {
        await using var app = CreateIsolatedApp();

        app.MapGroup("discovery-same-host").Get("ping", static () => Results.Ok()).RequireHost(first);
        app.MapGroup("discovery-same-host").Get("ping", static () => Results.Ok()).RequireHost(second);

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.Equal("GET /discovery-same-host/ping", Assert.Single(exception.Routes));
    }

    // RequireHost takes a list, and the order it is written in means nothing to the matcher: both of
    // these serve both hosts, so both really do collide. The two lists have to be compared as sets, or
    // a duplicate hides behind nothing more than the order someone typed two host names.
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_ThrowsWhenTwoEndpointsRequireTheSameHostsInADifferentOrder()
    {
        await using var app = CreateIsolatedApp();

        app.MapGroup("discovery-host-order").Get("ping", static () => Results.Ok())
            .RequireHost("a.example.com", "b.example.com");
        app.MapGroup("discovery-host-order").Get("ping", static () => Results.Ok())
            .RequireHost("b.example.com", "a.example.com");

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.Equal("GET /discovery-host-order/ping", Assert.Single(exception.Routes));
    }

    // Both halves of the constraint rule in one app. A constraint raises route precedence, so two
    // different ones are never ambiguous — but the constraint still has to be compared by what it says.
    // Treating "has a constraint" as a single fact would report the {id:int}/{id:guid} pair; ignoring
    // the constraint text entirely would report it too, while still passing the sibling test that only
    // puts a constrained route next to an unconstrained one.
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_SeparatesRoutesByConstraintButNotByTheFactOfHavingOne()
    {
        await using var app = CreateIsolatedApp();

        app.MapGroup("discovery-same-constraint").Get("{id:int}", static (int id) => Results.Ok(id));
        app.MapGroup("discovery-same-constraint").Get("{id:int}", static (int id) => Results.Ok(id));

        app.MapGroup("discovery-other-constraint").Get("{id:int}", static (int id) => Results.Ok(id));
        app.MapGroup("discovery-other-constraint").Get("{id:guid}", static (Guid id) => Results.Ok(id));

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.Equal("GET /discovery-same-constraint/{id:int}", Assert.Single(exception.Routes));
    }

    // Duplicates that span two endpoint data sources are the ones a developer cannot see by reading one
    // file: a library contributing its own EndpointDataSource next to the app's mapped routes. Looking
    // only at the app's own source would miss exactly the collisions that are hardest to find by hand.
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_ThrowsWhenTheCollidingEndpointsComeFromDifferentDataSources()
    {
        await using var app = CreateIsolatedApp();

        app.MapGroup("discovery-sources").Get("ping", static () => Results.Ok());
        AddEndpoints(app, CreateRouteEndpoint("discovery-sources/ping", "GET", isRouteHandler: true));

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.Equal("GET /discovery-sources/ping", Assert.Single(exception.Routes));
    }

    // A catch-all swallows the rest of the path and scores below a single-segment parameter, so
    // "{**path}" next to "{name}" is a normal pairing, not a mistake.
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_AllowsACatchAllAlongsideASingleSegmentParameter()
    {
        await using var app = CreateIsolatedApp();
        MapControlDuplicate(app);

        app.MapGroup("discovery-catchall").Get("{name}", static (string name) => Results.Ok(name));
        app.MapGroup("discovery-catchall").Get("{**path}", static (string path) => Results.Ok(path));

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.Equal(ControlRoute, Assert.Single(exception.Routes));
    }

    // "{id?}" also matches the route with the segment missing, so it is a different route shape than
    // "{id}" and the matcher never has to choose between them for one request.
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_AllowsAnOptionalParameterAlongsideARequiredOne()
    {
        await using var app = CreateIsolatedApp();
        MapControlDuplicate(app);

        app.MapGroup("discovery-optional").Get("{id}", static (string id) => Results.Ok(id));
        app.MapGroup("discovery-optional").Get("{id?}", static (string? id) => Results.Ok(id));

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.Equal(ControlRoute, Assert.Single(exception.Routes));
    }

    // A default value changes the route's precedence the same way a constraint does, so it has to stay
    // in the match key: "{page=1}" and "{page}" are not the same route.
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_AllowsAParameterWithADefaultAlongsideOneWithout()
    {
        await using var app = CreateIsolatedApp();
        MapControlDuplicate(app);

        AddEndpoints(
            app,
            CreateRouteEndpoint("discovery-default/{page}", "GET", isRouteHandler: true),
            CreateRouteEndpoint("discovery-default/{page=1}", "GET", isRouteHandler: true));

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.Equal(ControlRoute, Assert.Single(exception.Routes));
    }

    // Link-generation-only endpoints (MVC conventional routes, MapDynamic*) are never matched against a
    // request, so however many share a route they can never be ambiguous.
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_IgnoresEndpointsThatSuppressMatching()
    {
        await using var app = CreateIsolatedApp();
        MapControlDuplicate(app);

        app.MapGroup("discovery-suppressed").Get("ping", static () => Results.Ok())
            .WithMetadata(new DiscoverySuppressMatchingMetadata());
        app.MapGroup("discovery-suppressed").Get("ping", static () => Results.Ok())
            .WithMetadata(new DiscoverySuppressMatchingMetadata());

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.Equal(ControlRoute, Assert.Single(exception.Routes));
    }

    // Some endpoint sources put several endpoints on one route on purpose and pick between them with a
    // matcher policy of their own (static assets do this per Content-Encoding). Reporting those would
    // make the check fire on a perfectly normal app, so a route needs two real handlers to count.
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_IgnoresARouteWhereOnlyOneEndpointIsARouteHandler()
    {
        await using var app = CreateIsolatedApp();
        MapControlDuplicate(app);

        app.MapGroup("discovery-assets").Get("logo", static () => Results.Ok());
        AddEndpoints(app, CreateRouteEndpoint("discovery-assets/logo", "GET", isRouteHandler: false));

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.Equal(ControlRoute, Assert.Single(exception.Routes));
    }

    // ---------------------------------------------------------------------
    // Duplicate detection: the report
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ValidateNoDuplicateEndpoints_ReportIsSingularForOneDuplicatedRoute()
    {
        await using var app = CreateIsolatedApp();
        MapControlDuplicate(app);

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.StartsWith("1 route is mapped more than once.", exception.Message, StringComparison.Ordinal);
    }

    // Reporting only the first collision would send a developer round the loop once per duplicate.
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_ReportsEveryDuplicatedRouteWithPluralWording()
    {
        await using var app = CreateIsolatedApp();

        app.MapGroup("discovery-many-a").Get("ping", static () => Results.Ok());
        app.MapGroup("discovery-many-a").Get("ping", static () => Results.Ok());
        app.MapGroup("discovery-many-b").Post("ping", static () => Results.Ok());
        app.MapGroup("discovery-many-b").Post("ping", static () => Results.Ok());

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.StartsWith("2 routes are mapped more than once.", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, exception.Routes.Count);
        Assert.Contains("GET /discovery-many-a/ping", exception.Routes);
        Assert.Contains("POST /discovery-many-b/ping", exception.Routes);
    }

    // An endpoint that declares an empty HTTP method list accepts every method, exactly like one with no
    // method metadata at all. Without the fallback label the report would print a bare " /route".
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_LabelsEndpointsWithoutAnHttpMethodAsAnyMethod()
    {
        await using var app = CreateIsolatedApp();

        AddEndpoints(
            app,
            CreateRouteEndpoint("discovery-anymethod/ping", httpMethod: null, isRouteHandler: true),
            CreateRouteEndpoint("discovery-anymethod/ping", httpMethod: null, isRouteHandler: true));

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.Equal("(any method) /discovery-anymethod/ping", Assert.Single(exception.Routes));
    }

    // A group's root is mapped as "orders/"; printing that verbatim makes the reader look for a route
    // they never wrote, so the report normalizes it to "/orders".
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_DescribesAGroupRootWithLeadingSlashOnly()
    {
        await using var app = CreateIsolatedApp();

        app.MapGroup("discovery-root").Get("/", static () => Results.Ok());
        app.MapGroup("discovery-root").Get("/", static () => Results.Ok());

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.Equal("GET /discovery-root", Assert.Single(exception.Routes));
    }

    // Minimal APIs always set a display name, but an endpoint from a custom data source need not. The
    // report's value is the per-endpoint list underneath each route; leaving those lines blank would
    // tell a developer that something clashes without telling them what.
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_NamesEndpointsByRoutePatternWhenTheyHaveNoDisplayName()
    {
        await using var app = CreateIsolatedApp();

        AddEndpoints(
            app,
            CreateRouteEndpoint("discovery-nodisplay/ping", "GET", isRouteHandler: true, withDisplayName: false),
            CreateRouteEndpoint("discovery-nodisplay/ping", "GET", isRouteHandler: true, withDisplayName: false));

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.Equal("GET /discovery-nodisplay/ping", Assert.Single(exception.Routes));
        // The route heading reads "GET /discovery-nodisplay/ping"; only a listed endpoint reads
        // "- discovery-nodisplay/ping", so this fails if the fallback stops naming them.
        Assert.Contains("- discovery-nodisplay/ping", exception.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // DuplicateEndpointException
    // ---------------------------------------------------------------------

    [Fact]
    public void DuplicateEndpointException_DefaultsToAnEmptyRouteList()
    {
        var exception = new DuplicateEndpointException("report");

        Assert.Empty(exception.Routes);
        Assert.Equal("report", exception.Message);
        // Callers that already catch startup misconfiguration as InvalidOperationException keep working.
        Assert.IsAssignableFrom<InvalidOperationException>(exception);
    }

    [Fact]
    public void DuplicateEndpointException_KeepsTheRoutesItWasGiven()
    {
        var exception = new DuplicateEndpointException("report", new[] { "GET /a", "POST /b" });

        Assert.Equal(new[] { "GET /a", "POST /b" }, exception.Routes);
    }

    // Routes is copied, not aliased: the detector builds it from a lazy Select over a list it keeps
    // mutating, and an exception whose payload changes after it is thrown is untestable and unloggable.
    [Fact]
    public void DuplicateEndpointException_SnapshotsTheRoutesItWasGiven()
    {
        var routes = new List<string> { "GET /a" };

        var exception = new DuplicateEndpointException("report", routes);
        routes.Add("POST /b");

        Assert.Equal("GET /a", Assert.Single(exception.Routes));
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    /// <summary>
    /// An app whose application name points at the library assembly rather than at this test assembly.
    /// <c>MapEndpoints</c> always scans the assembly named by <c>IHostEnvironment.ApplicationName</c>,
    /// and this assembly is full of endpoint groups other test files own; naming Phoenix.Mediator
    /// (which has no concrete group) keeps discovery to exactly the assemblies a test hands it.
    /// </summary>
    private static WebApplicationBuilder CreateIsolatedBuilder(RecordingLoggerProvider? logs = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(BaseEndpointGroup).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });

        if (logs is not null)
            builder.Logging.AddProvider(logs);

        // AddMediator brings in the health-check services MapPhoenixHealthChecks needs.
        builder.Services.AddMediator();
        RegisterOtherEndpointGroupDependencies(builder.Services);

        return builder;
    }

    private static WebApplication CreateIsolatedApp(RecordingLoggerProvider? logs = null)
        => CreateIsolatedBuilder(logs).Build();

    /// <summary>
    /// An app that named <paramref name="assemblies"/> to <c>AddMediator</c>, the way a host already
    /// pointing the mediator at its handler assemblies does. Discovery reads the same registry, so
    /// <c>MapEndpoints()</c> finds their endpoint groups without being handed them again.
    /// </summary>
    private static WebApplication CreateAppWithRegisteredAssemblies(params Assembly[] assemblies)
    {
        var builder = CreateIsolatedBuilder();
        builder.Services.AddMediator(assemblies);

        return builder.Build();
    }

    private static WebApplication CreateTestServerApp(Action<IServiceCollection>? configureServices = null)
    {
        var builder = CreateIsolatedBuilder();
        builder.WebHost.UseTestServer();
        configureServices?.Invoke(builder.Services);

        return builder.Build();
    }

    /// <summary>
    /// Discovery also scans the entry assembly, which under some test runners is this one. The endpoint
    /// groups other test files declare take constructor dependencies, so register anything they need
    /// that can be created without arguments; otherwise those runs would fail here for an unrelated
    /// reason. Deliberately reflective: no test file may reference another's types by name.
    /// </summary>
    private static void RegisterOtherEndpointGroupDependencies(IServiceCollection services)
    {
        var dependencies = typeof(DiscoveryEndpointTests).Assembly
            .GetTypes()
            .Where(static type => type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters
                && typeof(BaseEndpointGroup).IsAssignableFrom(type))
            .SelectMany(static type => type.GetConstructors())
            .SelectMany(static constructor => constructor.GetParameters())
            .Select(static parameter => parameter.ParameterType)
            .Where(static type => type.IsClass && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) is not null)
            .Distinct();

        foreach (var dependency in dependencies)
            services.TryAddScoped(dependency);
    }

    /// <summary>
    /// Maps one route twice, so the check always has something to report. A test that expects another
    /// pair NOT to be reported asserts that this control route is the only entry in the report, which
    /// fails both if that pair is reported and if the check stopped running altogether.
    /// </summary>
    private static void MapControlDuplicate(WebApplication app)
    {
        app.MapGroup("discovery-control").Get("ping", static () => Results.Ok());
        app.MapGroup("discovery-control").Get("ping", static () => Results.Ok());
    }

    private static int CountEndpoints(WebApplication app, string routeSuffix)
    {
        return app.RouteEndpoints().Count(endpoint => endpoint.RoutePattern.RawText is not null
            && endpoint.RoutePattern.RawText.EndsWith(routeSuffix, StringComparison.OrdinalIgnoreCase));
    }

    private static void AddEndpoints(WebApplication app, params RouteEndpoint[] endpoints)
    {
        ((IEndpointRouteBuilder)app).DataSources.Add(new DiscoveryEndpointDataSource(endpoints));
    }

    /// <summary>
    /// Builds a <see cref="RouteEndpoint"/> by hand, so a test can present the detector with shapes no
    /// <c>Map*</c> helper produces: an endpoint that is not a route handler, one whose declared HTTP
    /// method list is empty, or one with no display name.
    /// </summary>
    private static RouteEndpoint CreateRouteEndpoint(string pattern, string? httpMethod, bool isRouteHandler, bool withDisplayName = true)
    {
        var builder = new RouteEndpointBuilder(
            static _ => Task.CompletedTask,
            RoutePatternFactory.Parse(pattern),
            order: 0)
        {
            DisplayName = withDisplayName ? pattern : null
        };

        builder.Metadata.Add(new HttpMethodMetadata(
            httpMethod is null ? Array.Empty<string>() : new[] { httpMethod }));

        // A MethodInfo in the metadata is how the detector recognizes a mapped handler.
        if (isRouteHandler)
            builder.Metadata.Add(typeof(DiscoveryManualEndpoint).GetMethod(nameof(DiscoveryManualEndpoint.Handle))!);

        return (RouteEndpoint)builder.Build();
    }
}

/// <summary>
/// Endpoint groups for these tests, nested in an open generic so the assembly scan every other test file
/// runs skips them (they carry <typeparamref name="TMarker"/>, so ContainsGenericParameters is true).
/// Tests hand the closed forms to a <c>FakeAssembly</c> when they want them discovered.
/// </summary>
public static class DiscoveryGroupHost<TMarker>
{
    public sealed class PingEndpoints : BaseEndpointGroup
    {
        public override void Map(IEndpointRouteBuilder app)
            => app.MapGroup("discovery-explicit").Get("ping", static () => Results.Ok("pong"));
    }

    public sealed class RepeatedEndpoints : BaseEndpointGroup
    {
        public override void Map(IEndpointRouteBuilder app)
            => app.MapGroup("discovery-repeated").Get("ping", static () => Results.Ok("pong"));
    }

    public sealed class MixedEndpoints : BaseEndpointGroup
    {
        public override void Map(IEndpointRouteBuilder app)
            => app.MapGroup("discovery-mixed").Get("ping", static () => Results.Ok("pong"));
    }

    public sealed class PartialEndpoints : BaseEndpointGroup
    {
        public override void Map(IEndpointRouteBuilder app)
            => app.MapGroup("discovery-partial").Get("ping", static () => Results.Ok("pong"));
    }

    public sealed class RegistryEndpoints : BaseEndpointGroup
    {
        public override void Map(IEndpointRouteBuilder app)
            => app.MapGroup("discovery-registry").Get("ping", static () => Results.Ok("pong"));
    }

    public sealed class DynamicEndpoints : BaseEndpointGroup
    {
        public override void Map(IEndpointRouteBuilder app)
            => app.MapGroup("discovery-dynamic").Get("ping", static () => Results.Ok("pong"));
    }

    public sealed class NullEntryEndpoints : BaseEndpointGroup
    {
        public override void Map(IEndpointRouteBuilder app)
            => app.MapGroup("discovery-nullentry").Get("ping", static () => Results.Ok("pong"));
    }

    /// <summary>Nothing registers this, so the group below cannot be constructed from DI.</summary>
    public interface IUnregisteredDependency
    {
    }

    /// <summary>
    /// Discoverable and constructible in principle, but its dependency is missing. Nested in the open
    /// generic like the rest, so the scan every other test file runs never tries to construct it.
    /// </summary>
    public sealed class NeedsDependencyEndpoints : BaseEndpointGroup
    {
        public NeedsDependencyEndpoints(IUnregisteredDependency dependency) => Dependency = dependency;

        public IUnregisteredDependency Dependency { get; }

        public override void Map(IEndpointRouteBuilder app)
            => app.MapGroup("discovery-needs-dependency").Get("ping", static () => Results.Ok("pong"));
    }

    /// <summary>Abstract: constructing it throws, so discovery has to filter it out before that.</summary>
    public abstract class AbstractEndpoints : BaseEndpointGroup
    {
    }

    /// <summary>Not a group at all; casting it to <see cref="BaseEndpointGroup"/> would throw.</summary>
    public sealed class NotAnEndpointGroup
    {
    }

    public sealed class OrdersEndpoints : BaseEndpointGroup
    {
        public override void Map(IEndpointRouteBuilder app)
        {
        }
    }

    public sealed class InvoiceArchiveEndpoints : BaseEndpointGroup
    {
        public override void Map(IEndpointRouteBuilder app)
        {
        }
    }

    public sealed class Reports : BaseEndpointGroup
    {
        public override void Map(IEndpointRouteBuilder app)
        {
        }
    }

    public sealed class Endpoints : BaseEndpointGroup
    {
        public override void Map(IEndpointRouteBuilder app)
        {
        }
    }

    public sealed class EndpointsArchive : BaseEndpointGroup
    {
        public override void Map(IEndpointRouteBuilder app)
        {
        }
    }

    public sealed class OverriddenEndpoints : BaseEndpointGroup
    {
        public override string GroupName => "discovery-custom";

        public override void Map(IEndpointRouteBuilder app)
        {
        }
    }
}

/// <summary>
/// An assembly whose type list fails to load, like one referencing a dependency that was not deployed.
/// Supplies <see cref="Assembly.FullName"/> because endpoint discovery reads it when it reports the
/// partial load, and the base implementation throws.
/// </summary>
public sealed class DiscoveryPartiallyLoadableAssembly(params Type[] loadableTypes) : Assembly
{
    public const string Name = "Discovery.PartiallyLoadable";

    public override string FullName => Name;

    public override AssemblyName GetName() => new(Name);

    public override Type[] GetTypes()
        => throw new ReflectionTypeLoadException(
            [.. loadableTypes, null],
            [new TypeLoadException("Simulated missing dependency.")]);
}

/// <summary>
/// An assembly that reports itself as dynamic, like a runtime-emitted proxy module. It still hands out
/// a perfectly good endpoint group, so a test can tell "discovery skipped it" apart from "discovery
/// found nothing in it".
/// </summary>
public sealed class DiscoveryDynamicAssembly(params Type[] types) : Assembly
{
    public const string Name = "Discovery.Dynamic";

    public override bool IsDynamic => true;

    public override string FullName => Name;

    public override AssemblyName GetName() => new(Name);

    public override Type[] GetTypes() => types;
}

/// <summary>Lets a test put endpoints in front of the detector that no <c>Map*</c> helper produces.</summary>
public sealed class DiscoveryEndpointDataSource(IReadOnlyList<Endpoint> endpoints) : EndpointDataSource
{
    public override IReadOnlyList<Endpoint> Endpoints { get; } = endpoints;

    public override IChangeToken GetChangeToken() => new CancellationChangeToken(CancellationToken.None);
}

/// <summary>Marks an endpoint as link-generation only, the way MVC's conventional routes are.</summary>
public sealed class DiscoverySuppressMatchingMetadata : ISuppressMatchingMetadata
{
    public bool SuppressMatching => true;
}

/// <summary>Supplies the <see cref="System.Reflection.MethodInfo"/> a hand-built route handler needs.</summary>
public static class DiscoveryManualEndpoint
{
    public static Task Handle(HttpContext context) => Task.CompletedTask;
}
