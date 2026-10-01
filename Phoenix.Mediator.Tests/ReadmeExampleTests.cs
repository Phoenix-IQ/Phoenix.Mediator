using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Exceptions;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Sentry;
using Phoenix.Mediator.Serilog;
using Phoenix.Mediator.Tests.Infrastructure;
using Phoenix.Mediator.Validation;
using Phoenix.Mediator.Web;
using Phoenix.Mediator.Web.Middlewares;
using Phoenix.Mediator.Wrappers;
using Serilog;
using Serilog.Events;
using Xunit;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Phoenix.Mediator.Tests;

/// <summary>
/// Executable versions of every C# example in README.md, in README order. A README example that no longer
/// compiles, or that quietly stopped doing what the surrounding prose promises, is a documentation bug that
/// nothing else in the suite catches: the snippets are what a consumer copies into their own Program.cs.
/// <para>
/// Types the examples need are declared at the bottom of this file with the <c>Readme</c> prefix. The ones the
/// other test files' assembly scans must not see (endpoint groups, validators, a second handler for one
/// request) live nested inside <see cref="ReadmeHost{TMarker}"/>, whose type parameter they inherit.
/// </para>
/// </summary>
public sealed class ReadmeExampleTests
{
    // ---------------------------------------------------------------------------------------------
    // README "Quick start / 1. Register mediator"
    // ---------------------------------------------------------------------------------------------

    // The README's very first snippet. If this chain stops compiling or stops producing a usable ISender,
    // every consumer's Program.cs is wrong on line one.
    [Fact]
    public async Task AddMediator_QuickStartChain_ProducesAWorkingSender()
    {
        var assembly = ReadmeGreetingAssembly();
        var services = new ServiceCollection();

        services
            .AddMediator(assembly)            // core: ISender + request handlers
            .AddMediatorSentry()              // optional: Phoenix.Mediator.Sentry
            .AddMediatorValidation();         // optional: Phoenix.Mediator.Validation (validators from the same assemblies)

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var greeting = await sender.Send<ReadmeGetGreetingQuery, SingleResponse<string>>(
            new ReadmeGetGreetingQuery { Name = "Ada" },
            CancellationToken.None);

        Assert.Equal("Hello Ada", greeting.Result);
    }

    // "Pipeline behaviors ... run in registration order (first registered = outermost). AddMediatorSentry()
    // before AddMediatorValidation() makes the Sentry span wrap validation."
    [Fact]
    public void AddMediatorSentryBeforeValidation_PutsTheSentryBehaviorOutsideValidation()
    {
        var assembly = ReadmeGreetingAssembly();
        var services = new ServiceCollection();

        services.AddMediator(assembly).AddMediatorSentry().AddMediatorValidation();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.Collection(
            scope.ServiceProvider.GetServices<IPipelineBehavior<ReadmeGetGreetingQuery, SingleResponse<string>>>(),
            behavior => Assert.IsType<SentryBehavior<ReadmeGetGreetingQuery, SingleResponse<string>>>(behavior),
            behavior => Assert.IsType<ValidationBehavior<ReadmeGetGreetingQuery, SingleResponse<string>>>(behavior));
    }

    // Reversing the two calls has to reverse the pipeline, otherwise the README's ordering advice is noise.
    [Fact]
    public void AddMediatorValidationBeforeSentry_PutsValidationOutsideTheSentrySpan()
    {
        var assembly = ReadmeGreetingAssembly();
        var services = new ServiceCollection();

        services.AddMediator(assembly).AddMediatorValidation().AddMediatorSentry();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.Collection(
            scope.ServiceProvider.GetServices<IPipelineBehavior<ReadmeGetGreetingQuery, SingleResponse<string>>>(),
            behavior => Assert.IsType<ValidationBehavior<ReadmeGetGreetingQuery, SingleResponse<string>>>(behavior),
            behavior => Assert.IsType<SentryBehavior<ReadmeGetGreetingQuery, SingleResponse<string>>>(behavior));
    }

    // "Empty IRequest responses default to 204 No Content."
    [Fact]
    public void AddMediator_WithoutOptions_DefaultsEmptyResponsesToNoContent()
    {
        var services = new ServiceCollection();

        services.AddMediator(ReadmeGreetingAssembly());

        using var provider = services.BuildServiceProvider(validateScopes: true);

        Assert.Equal(
            EmptyResponseStatusCode.NoContent,
            provider.GetRequiredService<IOptions<MediatorOptions>>().Value.EmptyResponseStatusCode);
    }

    // The README's options overload, verbatim: AddMediator(options => { ... }, assembly).
    [Fact]
    public void AddMediator_OptionsOverload_ConfiguresOkForEmptyResponses()
    {
        var services = new ServiceCollection();

        services.AddMediator(options =>
        {
            options.EmptyResponseStatusCode = EmptyResponseStatusCode.Ok;
        }, ReadmeGreetingAssembly());

        using var provider = services.BuildServiceProvider(validateScopes: true);

        Assert.Equal(
            EmptyResponseStatusCode.Ok,
            provider.GetRequiredService<IOptions<MediatorOptions>>().Value.EmptyResponseStatusCode);
    }

    // "AddMediator(assemblies...) registers: ISender (scoped)". Scoped is load-bearing: handlers depend on
    // scoped services (DbContext, current user), which a singleton sender would capture across requests.
    [Fact]
    public void AddMediator_RegistersSenderAsScoped()
    {
        var services = new ServiceCollection();

        services.AddMediator(ReadmeGreetingAssembly());

        Assert.Equal(
            ServiceLifetime.Scoped,
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ISender)).Lifetime);

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<ISender>(),
            first.ServiceProvider.GetRequiredService<ISender>());
        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<ISender>(),
            second.ServiceProvider.GetRequiredService<ISender>());
    }

    // "AddMediator(assemblies...) registers: ... request handlers from the provided assemblies".
    [Fact]
    public async Task AddMediator_RegistersRequestHandlersFromTheProvidedAssemblies()
    {
        var services = new ServiceCollection();

        services.AddMediator(ReadmeGreetingAssembly());

        // Single rather than Contains: a second registration for the same request would make which handler
        // runs depend on registration order, which is exactly what the duplicate-handler guard rules out.
        var registration = Assert.Single(services, descriptor =>
            descriptor.ServiceType == typeof(IRequestHandler<ReadmeGetGreetingQuery, SingleResponse<string>>));
        Assert.Equal(typeof(ReadmeGetGreetingQueryHandler), registration.ImplementationType);

        // ...and the registration is usable, not merely present: a handler registered with a lifetime the
        // container rejects (or against the wrong service type) would still satisfy the descriptor check.
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.IsType<ReadmeGetGreetingQueryHandler>(
            scope.ServiceProvider.GetRequiredService<IRequestHandler<ReadmeGetGreetingQuery, SingleResponse<string>>>());
    }

    // "Pipeline behaviors are opt-in." AddMediator on its own must register none: a behavior that showed up
    // without its companion package would run on every request in an app that never asked for it, and the
    // README's registration-order advice would be describing something the consumer cannot see.
    [Fact]
    public void AddMediator_WithoutTheCompanionPackages_RegistersNoPipelineBehaviors()
    {
        var services = new ServiceCollection();

        services.AddMediator(ReadmeGreetingAssembly());

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.Empty(scope.ServiceProvider.GetServices<IPipelineBehavior<ReadmeGetGreetingQuery, SingleResponse<string>>>());
        Assert.Empty(scope.ServiceProvider.GetServices<IPipelineBehavior<ReadmeCompleteCommand>>());
    }

    // "The core package has no third-party runtime dependencies. Validation, Sentry, and Serilog support live
    // in opt-in companion packages." One `using` added inside Phoenix.Mediator would push FluentValidation or
    // Sentry onto every consumer, and nothing but this would notice until a restore log was read.
    [Fact]
    public void CorePackage_ReferencesNoThirdPartyRuntimeDependencies()
    {
        var thirdPartyReferences = typeof(ISender).Assembly
            .GetReferencedAssemblies()
            .Select(static reference => reference.Name ?? string.Empty)
            .Where(static name =>
                name.StartsWith("FluentValidation", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Sentry", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Serilog", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(thirdPartyReferences);

        // ...and the opt-in pieces really do ship somewhere else, so "companion package" is not just a heading.
        Assert.NotSame(typeof(ISender).Assembly, typeof(ValidationBehavior<,>).Assembly);
        Assert.NotSame(typeof(ISender).Assembly, typeof(SentryBehavior<,>).Assembly);
    }

    // A handler in an assembly that was never handed to AddMediator must NOT be registered, otherwise the
    // "from the provided assemblies" half of the claim means nothing.
    [Fact]
    public void AddMediator_DoesNotRegisterHandlersFromAssembliesItWasNotGiven()
    {
        var services = new ServiceCollection();

        services.AddMediator(new FakeAssembly("Readme.Empty"));

        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(IRequestHandler<ReadmeGetGreetingQuery, SingleResponse<string>>));
    }

    // "AddMediator(assemblies...) registers: ... the /health endpoint support". MapPhoenixHealthChecks resolves
    // HealthCheckService, so without this registration the health endpoint throws on the first request.
    [Fact]
    public void AddMediator_RegistersHealthCheckSupport()
    {
        var services = new ServiceCollection();
        // HealthCheckService takes an ILogger; every real host has logging, a bare ServiceCollection does not.
        services.AddLogging();

        services.AddMediator(ReadmeGreetingAssembly());

        using var provider = services.BuildServiceProvider(validateScopes: true);

        Assert.NotNull(provider.GetService<HealthCheckService>());
    }

    // "AddMediatorValidation() also registers the FluentValidation validators in those assemblies ..."
    [Fact]
    public void AddMediatorValidation_RegistersTheValidatorsInTheAssembliesGivenToAddMediator()
    {
        var services = new ServiceCollection();

        services
            .AddMediator(new FakeAssembly("Readme.App", typeof(ReadmeHost<object>.ReadmeCreateStudentCommandValidator)))
            .AddMediatorValidation();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.IsType<ReadmeHost<object>.ReadmeCreateStudentCommandValidator>(
            Assert.Single(scope.ServiceProvider.GetServices<IValidator<ReadmeCreateStudentCommand>>()));
    }

    // "... pass it more assemblies only for validators that live elsewhere."
    [Fact]
    public void AddMediatorValidation_RegistersValidatorsFromTheAssembliesPassedToIt()
    {
        var services = new ServiceCollection();

        services
            .AddMediator(ReadmeGreetingAssembly())
            .AddMediatorValidation(new FakeAssembly("Readme.Validators", typeof(ReadmeHost<object>.ReadmeCreateStudentCommandValidator)));

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.IsType<ReadmeHost<object>.ReadmeCreateStudentCommandValidator>(
            Assert.Single(scope.ServiceProvider.GetServices<IValidator<ReadmeCreateStudentCommand>>()));
    }

    // ---------------------------------------------------------------------------------------------
    // README "Quick start / 2. Create a request + handler"
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Ada", "Hello Ada")]
    [InlineData("Grace Hopper", "Hello Grace Hopper")]
    [InlineData("", "Hello ")]
    public async Task GetGreetingQueryHandler_ReturnsHelloFollowedByTheName(string name, string expected)
    {
        var services = new ServiceCollection();
        services.AddMediator(ReadmeGreetingAssembly());

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var greeting = await sender.Send<ReadmeGetGreetingQuery, SingleResponse<string>>(new ReadmeGetGreetingQuery { Name = name });

        Assert.Equal(expected, greeting.Result);
    }

    // ---------------------------------------------------------------------------------------------
    // README "Quick start / 3. Map endpoints via endpoint groups"
    // ---------------------------------------------------------------------------------------------

    // The README's GreetingEndpoints group, end to end over a real HTTP request: [AsParameters] binds the
    // query string into the mediator request and the endpoint helper turns the response into a 200 JSON body.
    [Fact]
    public async Task GreetingEndpoints_ReturnsTheGreetingOverHttp()
    {
        var builder = CreateReadmeBuilder(useTestServer: true);
        await using var app = builder.Build();

        new ReadmeHost<object>.ReadmeGreetingEndpoints().Map(app);

        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync("readmegreeting/hello?name=Ada");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The documented body is the handler's SingleResponse, not merely a response mentioning the name.
        Assert.Equal("Hello Ada", JsonBody(await response.Content.ReadAsStringAsync()).GetProperty("result").GetString());
    }

    // BaseEndpointGroup.GroupName drives the route prefix in the README's snippet (app.MapGroup(GroupName)),
    // so the "Endpoints" suffix stripping and lower-casing are part of the documented URL.
    [Fact]
    public async Task GreetingEndpoints_UsesTheGroupNameAsTheRoutePrefix()
    {
        await using var app = CreateReadmeApp();

        new ReadmeHost<object>.ReadmeGreetingEndpoints().Map(app);

        Assert.Equal("readmegreeting/hello", app.Endpoint("readmegreeting/hello").RoutePattern.RawText);
    }

    // "app.MapEndpoints(); // also maps /health"
    [Fact]
    public async Task MapEndpoints_AlsoMapsTheHealthEndpoint()
    {
        await using var app = CreateReadmeApp(registerDiscoveredGroupDependencies: true);

        app.MapEndpoints();

        Assert.Contains(app.RouteEndpoints(), endpoint =>
            string.Equals(endpoint.RoutePattern.RawText, "/health", StringComparison.Ordinal));
    }

    // "app.MapEndpoints(typeof(GreetingEndpoints).Assembly);" — groups living in a separate class library.
    [Fact]
    public async Task MapEndpoints_MapsGroupsFromTheAssembliesPassedExplicitly()
    {
        var builder = CreateReadmeBuilder(useTestServer: true);
        RegisterDiscoveredGroupDependencies(builder.Services);
        await using var app = builder.Build();

        app.MapEndpoints(new FakeAssembly("Readme.Endpoints", typeof(ReadmeHost<object>.ReadmeGreetingEndpoints)));

        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync("readmegreeting/hello?name=Ada");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Hello Ada", JsonBody(await response.Content.ReadAsStringAsync()).GetProperty("result").GetString());
    }

    // "By default MapEndpoints also registers the exception-handling middleware" — the thing that turns a
    // NotFoundException thrown inside a handler into the documented 404 error body rather than a 500.
    [Fact]
    public async Task MapEndpoints_RegistersTheExceptionHandlingMiddlewareByDefault()
    {
        var builder = CreateReadmeBuilder(useTestServer: true);
        RegisterDiscoveredGroupDependencies(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("readme-defaultpipeline").Get("missing", (ISender sender, CancellationToken ct) =>
            sender.Send(new ReadmeMissingStudentQuery(), ct));
        app.MapEndpoints();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync("readme-defaultpipeline/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // The README's MapEndpointsOptions snippet, both flags at once: no /health route, and no middleware, so
    // the exception reaches the host instead of being turned into an error body.
    [Fact]
    public async Task MapEndpoints_WithOptions_SkipsHealthChecksAndExceptionHandling()
    {
        var builder = CreateReadmeBuilder(useTestServer: true);
        RegisterDiscoveredGroupDependencies(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("readme-nopipeline").Get("missing", (ISender sender, CancellationToken ct) =>
            sender.Send(new ReadmeMissingStudentQuery(), ct));

        app.MapEndpoints(new MapEndpointsOptions
        {
            UseExceptionHandling = false, // you call app.UsePhoenixExceptionHandling() yourself
            MapHealthChecks = false       // or app.MapPhoenixHealthChecks("/healthz")
        });

        Assert.DoesNotContain(app.RouteEndpoints(), endpoint =>
            endpoint.RoutePattern.RawText is not null
            && endpoint.RoutePattern.RawText.EndsWith("health", StringComparison.OrdinalIgnoreCase));

        await app.StartAsync();
        using var client = app.GetTestClient();

        var exception = await Record.ExceptionAsync(() => client.GetAsync("readme-nopipeline/missing"));

        Assert.True(
            ContainsException<NotFoundException>(exception),
            $"Expected the NotFoundException to escape an app without the middleware, got: {exception?.ToString() ?? "no exception"}");
    }

    // "Or compose the pieces directly: app.UsePhoenixExceptionHandling(); app.MapPhoenixHealthChecks();"
    [Fact]
    public async Task UsePhoenixExceptionHandling_CanBeComposedManually()
    {
        var builder = CreateReadmeBuilder(useTestServer: true);
        await using var app = builder.Build();

        app.UsePhoenixExceptionHandling();
        app.MapGroup("readme-manualpipeline").Get("missing", (ISender sender, CancellationToken ct) =>
            sender.Send(new ReadmeMissingStudentQuery(), ct));

        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync("readme-manualpipeline/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // The health endpoint deliberately reports only the overall status; per-check names can leak
    // infrastructure detail and the endpoint is public and unauthenticated.
    [Fact]
    public async Task MapPhoenixHealthChecks_ReturnsTheOverallStatusOnly()
    {
        var builder = CreateReadmeBuilder(useTestServer: true);
        await using var app = builder.Build();

        app.MapPhoenixHealthChecks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonBody(await response.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        Assert.Equal(new[] { "status" }, body.EnumerateObject().Select(static property => property.Name).ToArray());
    }

    // "or app.MapPhoenixHealthChecks("/healthz")"
    [Fact]
    public async Task MapPhoenixHealthChecks_UsesTheGivenPattern()
    {
        await using var app = CreateReadmeApp();

        app.MapPhoenixHealthChecks("/healthz");

        Assert.Contains(app.RouteEndpoints(), endpoint =>
            string.Equals(endpoint.RoutePattern.RawText, "/healthz", StringComparison.Ordinal));
        // The pattern replaces the default rather than adding to it: a second, undocumented /health would be
        // a public unauthenticated URL nobody knew was there.
        Assert.DoesNotContain(app.RouteEndpoints(), endpoint =>
            string.Equals(endpoint.RoutePattern.RawText, "/health", StringComparison.Ordinal));
    }

    // "Or compose the pieces directly: app.UsePhoenixExceptionHandling();, app.MapPhoenixHealthChecks();, then
    // app.MapEndpoints(...)". Each piece is asserted on its own above; this is the recipe as a whole, which is
    // what a consumer who needs precise middleware ordering actually copies: opting out of both defaults must
    // still discover endpoint groups, and the hand-registered middleware must still produce the 404 body.
    [Fact]
    public async Task ComposedPipeline_HandWiredMiddlewareAndHealthChecks_StillMapsGroupsAndHandlesErrors()
    {
        var builder = CreateReadmeBuilder(useTestServer: true);
        RegisterDiscoveredGroupDependencies(builder.Services);
        await using var app = builder.Build();

        app.UsePhoenixExceptionHandling();
        app.MapPhoenixHealthChecks("/readme-composed-health");
        app.MapGroup("readme-composed").Get("missing", (ISender sender, CancellationToken ct) =>
            sender.Send(new ReadmeMissingStudentQuery(), ct));

        app.MapEndpoints(
            new MapEndpointsOptions { UseExceptionHandling = false, MapHealthChecks = false },
            new FakeAssembly("Readme.Composed", typeof(ReadmeHost<object>.ReadmeGreetingEndpoints)));

        Assert.DoesNotContain(app.RouteEndpoints(), endpoint =>
            string.Equals(endpoint.RoutePattern.RawText, "/health", StringComparison.Ordinal));

        await app.StartAsync();
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/readme-composed-health")).StatusCode);

        var greeting = await client.GetAsync("readmegreeting/hello?name=Ada");
        Assert.Equal("Hello Ada", JsonBody(await greeting.Content.ReadAsStringAsync()).GetProperty("result").GetString());

        var error = await client.GetAsync("readme-composed/missing");
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Equal("Student 5 was not found.", JsonBody(await error.Content.ReadAsStringAsync()).GetProperty("errors")[0].GetString());
    }

    // ---------------------------------------------------------------------------------------------
    // README "Shared route prefix and conventions"
    // ---------------------------------------------------------------------------------------------
    // EndpointGroupSettingsTests.cs covers every rule; these run the README's snippet on the quick start group.

    // The snippet, verbatim: every group under /api, and RequireAuthorization() applied once for all of them.
    [Fact]
    public async Task MapEndpoints_SharedPrefixAndConventions_ReachTheQuickStartGroup()
    {
        await using var app = await StartSharedSettingsAppAsync(new MapEndpointsOptions
        {
            RoutePrefix = "api",                                                // GET /api/greeting/hello
            ConfigureEndpoints = endpoints => endpoints.RequireAuthorization()  // every group, no group can forget it
        });
        using var client = app.GetTestClient();

        var anonymous = await client.GetAsync("api/readmegreeting/hello?name=Ada");
        using var signedInRequest = new HttpRequestMessage(HttpMethod.Get, "api/readmegreeting/hello?name=Ada");
        signedInRequest.Headers.TryAddWithoutValidation(RoleHeaderAuthenticationHandler.RolesHeader, "User");
        var signedIn = await client.SendAsync(signedInRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.Equal("Hello Ada", JsonBody(await signedIn.Content.ReadAsStringAsync()).GetProperty("result").GetString());
    }

    // "The health endpoint is not prefixed."
    [Fact]
    public async Task MapEndpoints_WithARoutePrefix_KeepsTheHealthEndpointAtItsOwnPath()
    {
        await using var app = await StartSharedSettingsAppAsync(new MapEndpointsOptions { RoutePrefix = "api" });
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("health")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("api/health")).StatusCode);
    }

    // "Each group's endpoints are tagged with its GroupName, which is how OpenAPI tools group them."
    [Fact]
    public async Task MapEndpoints_TagsTheQuickStartGroupsEndpointsWithItsGroupName()
    {
        await using var app = await StartSharedSettingsAppAsync(new MapEndpointsOptions());

        Assert.Equal(
            new[] { "readmegreeting" },
            app.Endpoint("readmegreeting/hello").Metadata.OfType<ITagsMetadata>().SelectMany(static metadata => metadata.Tags));
    }

    // ---------------------------------------------------------------------------------------------
    // README "Duplicate routes"
    // ---------------------------------------------------------------------------------------------
    // DuplicateEndpointTests.cs covers the detector's edge cases in depth; these pin the specific claims the
    // README makes about it, including the wording of the report it prints.

    // The README's own example: two endpoint groups mapping GET users/{id} and GET users/{userId}.
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_ThrowsWhenTwoEndpointGroupsMapTheSameRoute()
    {
        await using var app = CreateReadmeApp();

        new ReadmeHost<object>.ReadmeDuplicateUserEndpoints().Map(app);
        new ReadmeHost<object>.ReadmeDuplicateAdminEndpoints().Map(app);

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        // Which parameter name lands in the report depends on the order the detector happened to group the
        // endpoints in, which is not a documented guarantee; the route and method are.
        Assert.StartsWith("GET /readme-dupes/", Assert.Single(exception.Routes), StringComparison.Ordinal);
    }

    // The report is quoted verbatim in the README. Consumers meet it as a startup crash, so the sentence that
    // explains why ASP.NET Core did not catch the clash itself has to survive refactoring.
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_ReportReadsTheWayTheReadmeShowsIt()
    {
        await using var app = CreateReadmeApp();

        new ReadmeHost<object>.ReadmeDuplicateUserEndpoints().Map(app);
        new ReadmeHost<object>.ReadmeDuplicateAdminEndpoints().Map(app);

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.StartsWith("1 route is mapped more than once.", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            "AmbiguousMatchException (HTTP 500) the first time a request matches more than one endpoint",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("  GET /readme-dupes/", exception.Message, StringComparison.Ordinal);
        Assert.Contains("(mapped in ", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ReadmeDuplicateUserEndpoints", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ReadmeDuplicateAdminEndpoints", exception.Message, StringComparison.Ordinal);
    }

    // The report's endpoint lines are the ones a developer follows back to the code, and the README shows them
    // as "HTTP: GET users/{id}", which is the minimal-API display name.
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_ReportListsEachEndpointByItsDisplayName()
    {
        await using var app = CreateReadmeApp();

        new ReadmeHost<object>.ReadmeDuplicateUserEndpoints().Map(app);
        new ReadmeHost<object>.ReadmeDuplicateAdminEndpoints().Map(app);

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.Contains("HTTP: GET readme-dupes/{id}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("HTTP: GET readme-dupes/{userId}", exception.Message, StringComparison.Ordinal);
    }

    // "2 routes are mapped more than once" is the plural branch of the same report.
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_ReportCountsEveryDuplicatedRoute()
    {
        await using var app = CreateReadmeApp();

        app.MapGroup("readme-plural").Get("a", () => Results.Ok());
        app.MapGroup("readme-plural").Get("a", () => Results.Ok());
        app.MapGroup("readme-plural").Get("b", () => Results.Ok());
        app.MapGroup("readme-plural").Get("b", () => Results.Ok());

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.StartsWith("2 routes are mapped more than once.", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, exception.Routes.Count);
    }

    // "To log instead of throwing, or to turn the check off" - the README's DuplicateEndpointHandling snippet.
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_WarnLogsTheReportInsteadOfThrowing()
    {
        var logs = new RecordingLoggerProvider();
        await using var app = CreateReadmeApp(loggerProvider: logs);

        new ReadmeHost<object>.ReadmeDuplicateUserEndpoints().Map(app);
        new ReadmeHost<object>.ReadmeDuplicateAdminEndpoints().Map(app);

        app.ValidateNoDuplicateEndpoints(DuplicateEndpointHandling.Warn);

        Assert.Contains(logs.Warnings, entry => entry.Message.Contains("readme-dupes/{id}", StringComparison.Ordinal));
    }

    // None turns the check off completely. Not throwing is the documented contract here, so the assertions are
    // that the duplicates really are still mapped and that nothing was reported about them.
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_NoneSkipsTheCheckEntirely()
    {
        var logs = new RecordingLoggerProvider();
        await using var app = CreateReadmeApp(loggerProvider: logs);

        new ReadmeHost<object>.ReadmeDuplicateUserEndpoints().Map(app);
        new ReadmeHost<object>.ReadmeDuplicateAdminEndpoints().Map(app);

        app.ValidateNoDuplicateEndpoints(DuplicateEndpointHandling.None);

        Assert.Equal(2, app.RouteEndpoints().Count(endpoint =>
            endpoint.RoutePattern.RawText is not null
            && endpoint.RoutePattern.RawText.StartsWith("readme-dupes/", StringComparison.Ordinal)));
        Assert.DoesNotContain(logs.Warnings, entry => entry.Message.Contains("readme-dupes", StringComparison.Ordinal));
    }

    // "Routes the matcher can tell apart are not reported: ... a different RequireHost". The other separators
    // in that sentence (method, constraint, WithOrder, any-method) are covered in DuplicateEndpointTests.cs.
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_AllowsRoutesSeparatedByRequireHost()
    {
        await using var separated = CreateReadmeApp();
        separated.MapGroup("readme-hosts").Get("{id}", (string id) => Results.Ok(id)).RequireHost("a.example.com");
        separated.MapGroup("readme-hosts").Get("{id}", (string id) => Results.Ok(id)).RequireHost("b.example.com");

        separated.ValidateNoDuplicateEndpoints();

        // The same pair without the host constraint must still be reported, otherwise the case above would
        // pass against a detector that simply never fires on this route shape.
        await using var collided = CreateReadmeApp();
        collided.MapGroup("readme-hosts").Get("{id}", (string id) => Results.Ok(id));
        collided.MapGroup("readme-hosts").Get("{id}", (string id) => Results.Ok(id));

        Assert.Throws<DuplicateEndpointException>(() => collided.ValidateNoDuplicateEndpoints());
    }

    // "Only endpoints mapped by the time MapEndpoints returns are checked. If you map more afterwards, call
    // app.ValidateNoDuplicateEndpoints(); once you are done."
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_CatchesRoutesMappedAfterMapEndpointsReturned()
    {
        await using var app = CreateReadmeApp(registerDiscoveredGroupDependencies: true);

        app.MapEndpoints();

        app.MapGroup("readme-late").Get("{id}", (string id) => Results.Ok(id));
        app.MapGroup("readme-late").Get("{id}", (string id) => Results.Ok(id));

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.Equal("GET /readme-late/{id}", Assert.Single(exception.Routes));
    }

    // ---------------------------------------------------------------------------------------------
    // README "Sending requests"
    // ---------------------------------------------------------------------------------------------

    // "SingleResponse<string> greeting = await sender.Send(query, cancellationToken);" — the response type is
    // inferred from the request. The declared type of the local is the assertion: Task<SingleResponse<string>>
    // does not convert from Task<object?>, so if the call ever bound to Send(object) again this stops compiling.
    [Fact]
    public async Task Send_WithoutTypeArguments_InfersTheResponseType()
    {
        await using var provider = CreateReadmeProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        Task<SingleResponse<string>> pending = sender.Send(new ReadmeGetGreetingQuery { Name = "Ada" }, CancellationToken.None);

        Assert.Equal("Hello Ada", (await pending).Result);
    }

    // "The two-type-argument form Send<GetGreetingQuery, SingleResponse<string>>(query) still works" — code written
    // before the response type could be inferred keeps compiling and returning the response itself.
    [Fact]
    public async Task Send_WithBothTypeArguments_ReturnsTheHandlerResponse()
    {
        await using var provider = CreateReadmeProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        SingleResponse<string> greeting = await sender.Send<ReadmeGetGreetingQuery, SingleResponse<string>>(
            new ReadmeGetGreetingQuery { Name = "Ada" },
            CancellationToken.None);

        Assert.Equal("Hello Ada", greeting.Result);
    }

    // "IRequest (no response): returns a plain Task." An endpoint returning it gets the configured empty status from
    // the endpoint helpers, so the return type is part of the contract.
    // The void overload is the one-type-argument Send whose parameter IS its type argument; the other one-type-argument
    // Send takes an IRequest<TResponse>.
    [Fact]
    public void Send_VoidOverload_IsDeclaredToReturnAPlainTask()
    {
        var voidSend = Assert.Single(
            typeof(ISender).GetMethods(),
            method => method.Name == nameof(ISender.Send)
                && method.GetGenericArguments().Length == 1
                && method.GetParameters()[0].ParameterType.IsGenericParameter);

        Assert.Equal(typeof(Task), voidSend.ReturnType);
    }

    [Fact]
    public async Task Send_VoidOverload_RunsTheHandler()
    {
        await using var provider = CreateReadmeProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var command = new ReadmeNoteCommand();

        // Declaring the local as a plain Task is the README's claim, checked here by the compiler.
        Task send = sender.Send(command, CancellationToken.None);
        await send;

        Assert.Equal(new[] { "handled" }, command.Handled.ToArray());
    }

    // ISender.Send(object) is documented to return null for an IRequest, which is what lets the endpoint helpers
    // map it to an empty response at all.
    [Fact]
    public async Task Send_Object_ReturnsNullForRequestsWithoutAResponse()
    {
        await using var provider = CreateReadmeProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        Assert.Null(await sender.Send((object)new ReadmeCompleteCommand(), CancellationToken.None));
    }

    // "IRequest (no response): returns configured empty response status on success (204 No Content by default, or
    // 200 OK)". The OpenAPI document and the response come from the same setting, so they can't disagree.
    [Fact]
    public async Task EndpointHelpers_AnswerTheEmptyStatusTheyAdvertise()
    {
        var builder = CreateReadmeBuilder(static options => options.EmptyResponseStatusCode = EmptyResponseStatusCode.Ok, useTestServer: true);
        await using var app = builder.Build();
        app.MapGroup("readme-empty-status").Post("complete", static (ISender sender, ReadmeCompleteCommand command, CancellationToken ct) =>
            sender.Send(command, ct));

        var advertised = app.Endpoint("readme-empty-status/complete").Metadata
            .OfType<IProducesResponseTypeMetadata>()
            .Select(static metadata => metadata.StatusCode)
            .ToArray();

        await app.StartAsync();
        using var client = app.GetTestClient();
        var response = await client.PostAsync("readme-empty-status/complete", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Contains(StatusCodes.Status200OK, advertised);
        Assert.DoesNotContain(StatusCodes.Status204NoContent, advertised);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // "In endpoints, return what sender.Send(...) returns. The endpoint helpers map it to an IResult."
    [Fact]
    public async Task EndpointHelpers_MapWhatSendReturnsToAJsonResult()
    {
        var builder = CreateReadmeBuilder(useTestServer: true);
        await using var app = builder.Build();
        app.MapGroup("readme-send").Get("greeting", static (ISender sender, CancellationToken ct) =>
            sender.Send(new ReadmeGetGreetingQuery { Name = "Ada" }, ct));
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync("readme-send/greeting");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The mapped result is the handler's response serialized as JSON, not a body that happens to mention
        // the name: a client reads "result", so a rewrapped or flattened body is a breaking change.
        Assert.Equal("Hello Ada", JsonBody(await response.Content.ReadAsStringAsync()).GetProperty("result").GetString());
    }

    // ---------------------------------------------------------------------------------------------
    // README "Response and error behavior"
    // ---------------------------------------------------------------------------------------------

    // "IRequest<TResponse>: returns JSON body (200 OK) on success".
    [Fact]
    public async Task RequestWithAResponse_ReturnsJsonWithTwoHundredOverHttp()
    {
        var builder = CreateReadmeBuilder(useTestServer: true);
        await using var app = builder.Build();

        app.UsePhoenixExceptionHandling();
        app.MapGroup("readme-responses").Get("greeting", (ISender sender, [AsParameters] ReadmeGetGreetingQuery query, CancellationToken ct) =>
            sender.Send(query, ct));

        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync("readme-responses/greeting?name=Ada");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Hello Ada", JsonBody(await response.Content.ReadAsStringAsync()).GetProperty("result").GetString());
    }

    // "IRequest (no response): returns configured empty response status on success (204 No Content by
    // default, or 200 OK)."
    [Theory]
    [InlineData(EmptyResponseStatusCode.NoContent, HttpStatusCode.NoContent)]
    [InlineData(EmptyResponseStatusCode.Ok, HttpStatusCode.OK)]
    public async Task RequestWithoutAResponse_ReturnsTheConfiguredEmptyStatusOverHttp(EmptyResponseStatusCode configured, HttpStatusCode expected)
    {
        var builder = CreateReadmeBuilder(options => options.EmptyResponseStatusCode = configured, useTestServer: true);
        await using var app = builder.Build();

        app.UsePhoenixExceptionHandling();
        app.MapGroup("readme-empty").Post("complete", (ISender sender, ReadmeCompleteCommand command, CancellationToken ct) =>
            sender.Send(command, ct));

        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.PostAsync("readme-empty/complete", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(expected, response.StatusCode);
        // "No response" has to mean no body as well: a serialized "null" here would break every client that
        // reads the body of a 200, and would make the 200 and 204 branches differ in more than the status.
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    // "Built-in exception types: BadRequestException, NotFoundException" mapped by the middleware, not caught
    // by the endpoint helpers.
    [Fact]
    public async Task NotFoundException_MapsToNotFoundWithTheDocumentedErrorBody()
    {
        var builder = CreateReadmeBuilder(useTestServer: true);
        await using var app = builder.Build();

        app.UsePhoenixExceptionHandling();
        app.MapGroup("readme-errors").Get("missing", (ISender sender, CancellationToken ct) =>
            sender.Send(new ReadmeMissingStudentQuery(), ct));

        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync("readme-errors/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = JsonBody(await response.Content.ReadAsStringAsync());
        Assert.Equal("Student 5 was not found.", body.GetProperty("errors")[0].GetString());
    }

    [Fact]
    public async Task BadRequestException_MapsToBadRequestWithTheDocumentedErrorBody()
    {
        var builder = CreateReadmeBuilder(useTestServer: true);
        await using var app = builder.Build();

        app.UsePhoenixExceptionHandling();
        app.MapGroup("readme-errors").Get("invalid", (ISender sender, CancellationToken ct) =>
            sender.Send(new ReadmeInvalidStudentQuery(), ct));

        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync("readme-errors/invalid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonBody(await response.Content.ReadAsStringAsync());
        Assert.Equal("Student id must be positive.", body.GetProperty("errors")[0].GetString());
    }

    // The README documents the error body as exactly {"errors": [...], "traceId": "..."}. Clients parse this
    // shape, so an extra or renamed property is a breaking change.
    [Fact]
    public async Task ErrorBody_ContainsOnlyErrorsAndTraceId()
    {
        var builder = CreateReadmeBuilder(useTestServer: true);
        await using var app = builder.Build();

        app.UsePhoenixExceptionHandling();
        app.MapGroup("readme-errors").Get("shape", (ISender sender, CancellationToken ct) =>
            sender.Send(new ReadmeMissingStudentQuery(), ct));

        await app.StartAsync();
        using var client = app.GetTestClient();

        var body = JsonBody(await (await client.GetAsync("readme-errors/shape")).Content.ReadAsStringAsync());

        Assert.Equal(new[] { "errors", "traceId" }, body.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.Equal(JsonValueKind.Array, body.GetProperty("errors").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
    }

    // "HttpResponseException (or derived exceptions): returns {"errors":[...]} with mapped status code" - the
    // base type carries the status, so a project's own exception type works without any extra wiring.
    [Fact]
    public async Task DerivedHttpResponseException_UsesItsOwnStatusCodeAndErrors()
    {
        var response = await RunExceptionMiddlewareAsync(
            new ReadmeConflictException("Student already enrolled."));

        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        Assert.Equal("Student already enrolled.", response.Json.GetProperty("errors")[0].GetString());
    }

    // "Unhandled exceptions: returns 500 with the configured unknown-error message", and the ErrorMessages
    // block is copied verbatim from the README (keys "Default", "Ar", "En").
    [Theory]
    [InlineData(null, "En")]
    [InlineData("en", "En")]
    [InlineData("En", "En")]
    [InlineData("English", "En")]
    [InlineData("en-GB", "En")]
    [InlineData("ar", "Ar")]
    [InlineData("Ar", "Ar")]
    [InlineData("Arabic", "Ar")]
    [InlineData("ar-IQ", "Ar")]
    public async Task UnhandledException_ReturnsFiveHundredWithTheConfiguredUnknownErrorMessage(string? acceptLanguage, string expectedKey)
    {
        var messages = ReadmeErrorMessages();

        var response = await RunExceptionMiddlewareAsync(
            new InvalidOperationException("boom"),
            ReadmeErrorMessagesConfiguration(),
            acceptLanguage);

        Assert.Equal(StatusCodes.Status500InternalServerError, response.StatusCode);
        Assert.Equal(messages[expectedKey], response.Json.GetProperty("errors")[0].GetString());
    }

    // "if the header is missing, it uses Default/DefaultLanguage, then English." Both spellings are documented,
    // and an app that wrote the one the middleware forgot would silently serve English to Arabic clients while
    // its configuration says otherwise.
    [Theory]
    [InlineData("Default")]
    [InlineData("DefaultLanguage")]
    public async Task UnhandledException_WithoutAnAcceptLanguageHeader_UsesTheConfiguredDefaultLanguage(string defaultKey)
    {
        var messages = ReadmeErrorMessages();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ErrorMessages:{defaultKey}"] = "Ar",
                ["ErrorMessages:Ar"] = messages["Ar"],
                ["ErrorMessages:En"] = messages["En"]
            })
            .Build();

        var response = await RunExceptionMiddlewareAsync(new InvalidOperationException("boom"), configuration);

        Assert.Equal(StatusCodes.Status500InternalServerError, response.StatusCode);
        Assert.Equal(messages["Ar"], response.Json.GetProperty("errors")[0].GetString());
    }

    // "if the header is missing, it uses Default/DefaultLanguage, then English." With no Default configured at
    // all the built-in English message is what a consumer sees.
    [Fact]
    public async Task UnhandledException_FallsBackToTheBuiltInEnglishMessageWithoutConfiguration()
    {
        var response = await RunExceptionMiddlewareAsync(new InvalidOperationException("boom"));

        Assert.Equal(StatusCodes.Status500InternalServerError, response.StatusCode);
        Assert.Equal("Unknown error occurred", response.Json.GetProperty("errors")[0].GetString());
    }

    // "The helpers don't catch exceptions, they let them propagate" - the whole reason the middleware has to be
    // registered. Without this, an app that skipped the middleware would still look fine in tests.
    [Fact]
    public async Task EndpointHelpers_LetHandlerExceptionsPropagate()
    {
        var builder = CreateReadmeBuilder(useTestServer: true);
        await using var app = builder.Build();
        app.MapGroup("readme-propagate").Get("missing", static (ISender sender, CancellationToken ct) =>
            sender.Send(new ReadmeMissingStudentQuery(), ct));
        await app.StartAsync();
        using var client = app.GetTestClient();

        await Assert.ThrowsAsync<NotFoundException>(() => client.GetAsync("readme-propagate/missing"));
    }

    // "Built-in exception types": each one's status, as the list states it.
    [Fact]
    public void BuiltInExceptionTypes_CarryTheStatusTheReadmeListsForThem()
    {
        Assert.Equal(HttpStatusCode.BadRequest, new BadRequestException("readme").HttpStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, new UnauthorizedException("readme").HttpStatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, new ForbiddenException("readme").HttpStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, new NotFoundException("readme").HttpStatusCode);
        Assert.Equal(HttpStatusCode.Conflict, new ConflictException("readme").HttpStatusCode);
    }

    // README "Validation": a handler validating on its own with ValidateAndThrowAsync gets the same 400, in the error
    // body shape "Response and error behavior" shows, nothing added.
    [Fact]
    public async Task ErrorBody_ForAValidationFailure_HasTheReadmeShape()
    {
        await using var app = await StartErrorReadmeAppAsync(static services =>
            services.AddTransient<IRequestHandler<ReadmeSubscribeCommand, SingleResponse<string>>, ReadmeHost<object>.ReadmeSubscribeCommandHandler>());
        using var client = app.GetTestClient();

        var response = await client.PostAsync("readme-errors/subscribe", new StringContent("""{"email":"not-an-email"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonBody(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "errors", "traceId" }, body.EnumerateObject().Select(static member => member.Name).Order(StringComparer.Ordinal));
        Assert.Equal("'Email' is not a valid email address.", body.GetProperty("errors")[0].GetString());
    }

    // "### Mapping your own exceptions", the snippet verbatim apart from its two exception types, which come from EF Core
    // and Polly and have stand-ins here.
    [Fact]
    public async Task ExceptionHandlingOptions_TheReadmeMappings_AnswerWithTheirStatusAndMessage()
    {
        await using var app = await StartErrorReadmeAppAsync(static services =>
            services.Configure<ExceptionHandlingOptions>(options => options
                .Map<ReadmeStandIns.DbUpdateConcurrencyException>(HttpStatusCode.Conflict, _ => "Someone else changed this record. Reload and try again.")
                .Map<ReadmeStandIns.BrokenCircuitException>(HttpStatusCode.ServiceUnavailable)));
        using var client = app.GetTestClient();

        var concurrency = await client.GetAsync("readme-errors/concurrency");
        var circuit = await client.GetAsync("readme-errors/circuit");

        Assert.Equal(HttpStatusCode.Conflict, concurrency.StatusCode);
        Assert.Equal("Someone else changed this record. Reload and try again.", JsonBody(await concurrency.Content.ReadAsStringAsync()).GetProperty("errors")[0].GetString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, circuit.StatusCode);
        // "Map<T>(status) answers with the generic unknown-error message": never the exception's own.
        Assert.Equal("Unknown error occurred", JsonBody(await circuit.Content.ReadAsStringAsync()).GetProperty("errors")[0].GetString());
    }

    // "ArgumentException, KeyNotFoundException and UnauthorizedAccessException are 500s, logged at Error."
    [Fact]
    public async Task ExceptionHandling_TurnsAnArgumentExceptionIntoA500_ByDefault()
    {
        await using var app = await StartErrorReadmeAppAsync(configure: null);
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.InternalServerError, (await client.GetAsync("readme-errors/argument")).StatusCode);
    }

    // "Versions before 3.0.0 mapped them to 400, 404 and 401; to keep that:" and the snippet.
    [Fact]
    public async Task ExceptionHandlingOptions_MapCommonExceptionsOn_TurnsAnArgumentExceptionIntoA400()
    {
        await using var app = await StartErrorReadmeAppAsync(static services =>
            services.Configure<ExceptionHandlingOptions>(options => options.MapCommonExceptions = true));
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("readme-errors/argument")).StatusCode);
    }

    // "Client errors (4xx) are logged at Information, without the stack trace ... Server errors (5xx) are logged at
    // Error, with the exception."
    [Fact]
    public async Task Logging_ClientErrorsAtInformationWithoutTheStackTrace_ServerErrorsAtErrorWithIt()
    {
        var logs = new RecordingLoggerProvider();
        await using var app = await StartErrorReadmeAppAsync(configure: null, logs);
        using var client = app.GetTestClient();

        await client.GetAsync("readme-errors/missing");
        await client.GetAsync("readme-errors/boom");

        var middlewareLogs = logs.Entries.Where(static entry => entry.Category == typeof(ExceptionHandlingMiddleware).FullName).ToArray();
        Assert.Contains(middlewareLogs, static entry => entry.Level == LogLevel.Information && entry.Exception is null && entry.Message.Contains("404"));
        Assert.Contains(middlewareLogs, static entry => entry.Level == LogLevel.Error && entry.Exception is InvalidOperationException);
    }

    // ---------------------------------------------------------------------------------------------
    // README "Endpoint helpers"
    // ---------------------------------------------------------------------------------------------

    // "These helpers: Add default OpenAPI responses (401, 403, 400, 500)" - for every verb the README lists.
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task EndpointHelpers_AddTheDocumentedDefaultResponses(string verb)
    {
        await using var app = CreateReadmeApp();

        MapByVerb(app.MapGroup("readme-defaults"), verb, "x", () => Results.Ok());

        var statusCodes = SuccessAndErrorStatusCodes(app, "readme-defaults/x");

        Assert.Contains(StatusCodes.Status400BadRequest, statusCodes);
        Assert.Contains(StatusCodes.Status401Unauthorized, statusCodes);
        Assert.Contains(StatusCodes.Status403Forbidden, statusCodes);
        Assert.Contains(StatusCodes.Status500InternalServerError, statusCodes);
    }

    // The 400 and 500 entries carry the ErrorsResponse schema, which is the {"errors":[...],"traceId":"..."}
    // body the README documents. Producing them without a schema would publish an empty error contract.
    [Fact]
    public async Task EndpointHelpers_DescribeTheErrorResponsesWithTheErrorsResponseSchema()
    {
        await using var app = CreateReadmeApp();

        app.MapGroup("readme-errorschema").Get("x", () => Results.Ok());

        var metadata = app.Endpoint("readme-errorschema/x").Metadata.OfType<IProducesResponseTypeMetadata>().ToArray();

        Assert.Equal(typeof(ErrorsResponse), Assert.Single(metadata, m => m.StatusCode == StatusCodes.Status400BadRequest).Type);
        Assert.Equal(typeof(ErrorsResponse), Assert.Single(metadata, m => m.StatusCode == StatusCodes.Status500InternalServerError).Type);
    }

    // "Infer success response metadata from request type (IRequest<T> => 200 ...)", with T as the schema so
    // the published OpenAPI document describes the body clients actually receive.
    [Fact]
    public async Task EndpointHelpers_InferTwoHundredAndTheResponseSchemaFromTheRequestType()
    {
        await using var app = CreateReadmeApp();

        app.MapGroup("readme-infer").Get("greeting", (ISender sender, [AsParameters] ReadmeGetGreetingQuery query, CancellationToken ct) =>
            sender.Send(query, ct));

        var responses = app.Endpoint("readme-infer/greeting").Metadata.OfType<IProducesResponseTypeMetadata>().ToArray();

        // Single, not Contains: two 200 entries carrying different schemas would publish two different bodies
        // for one status, and a client generator picks whichever it meets first.
        Assert.Single(responses, metadata => metadata.StatusCode == StatusCodes.Status200OK
            && metadata.Type == typeof(SingleResponse<string>));

        // The framework infers a 200 of its own from the delegate's Task<SingleResponse<string>> (and, on .NET 9, a
        // schema-less one from a Task<IResult>). AddResponses drops it next to the declared one, on every framework.
        Assert.Equal(1, responses.Count(metadata => metadata.StatusCode == StatusCodes.Status200OK));
    }

    // "IRequest => configured empty response status". The negative half matters just as much: advertising both
    // 200 and 204 would document a response the endpoint never returns.
    [Theory]
    [InlineData(EmptyResponseStatusCode.NoContent, StatusCodes.Status204NoContent, StatusCodes.Status200OK)]
    [InlineData(EmptyResponseStatusCode.Ok, StatusCodes.Status200OK, StatusCodes.Status204NoContent)]
    public async Task EndpointHelpers_InferTheConfiguredEmptyStatusForRequestsWithoutAResponse(
        EmptyResponseStatusCode configured,
        int advertised,
        int notAdvertised)
    {
        await using var app = CreateReadmeApp(options => options.EmptyResponseStatusCode = configured);

        app.MapGroup("readme-inferempty").Post("complete", (ISender sender, ReadmeCompleteCommand command, CancellationToken ct) =>
            sender.Send(command, ct));

        var statusCodes = SuccessAndErrorStatusCodes(app, "readme-inferempty/complete");

        Assert.Contains(advertised, statusCodes);
        Assert.DoesNotContain(notAdvertised, statusCodes);
    }

    // A delegate with no mediator request parameter has nothing to infer from, so only the four error
    // responses are documented. Inventing a 200 here would describe a body the delegate may never write.
    [Fact]
    public async Task EndpointHelpers_InferNoSuccessResponseWhenTheDelegateTakesNoMediatorRequest()
    {
        await using var app = CreateReadmeApp();

        app.MapGroup("readme-noinfer").Delete("{id}", (string id) => Results.NoContent());

        var statusCodes = SuccessAndErrorStatusCodes(app, "readme-noinfer/{id}");

        Assert.Equal(
            new[]
            {
                StatusCodes.Status400BadRequest,
                StatusCodes.Status401Unauthorized,
                StatusCodes.Status403Forbidden,
                StatusCodes.Status500InternalServerError
            },
            statusCodes.Order().ToArray());
    }

    // Each helper must map its own HTTP method; a Patch helper that mapped POST would silently 405 every call.
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task EndpointHelpers_MapTheirOwnHttpMethod(string verb)
    {
        await using var app = CreateReadmeApp();

        MapByVerb(app.MapGroup("readme-verbs"), verb, "x", () => Results.Ok());

        var methods = app.Endpoint("readme-verbs/x").Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;

        Assert.Equal(new[] { verb }, methods?.ToArray());
    }

    // ---------------------------------------------------------------------------------------------
    // README "File uploads (multipart)"
    // ---------------------------------------------------------------------------------------------

    // "a body size limit (5 MB by default ...), a request timeout (120 s by default), and the default OpenAPI
    // responses", plus the multipart content type the helper advertises.
    [Fact]
    public async Task PostMultiPart_AddsTheDocumentedLimitTimeoutAndContentType()
    {
        await using var app = CreateReadmeApp();

        app.MapGroup("readme-uploads").PostMultiPart("documents",
            async (ISender sender, [FromForm] ReadmeUploadDocumentCommand command, CancellationToken ct) =>
                await sender.Send(command, ct), disableAntiforgery: true);

        var endpoint = app.Endpoint("readme-uploads/documents");

        // The documented default is a number, not just "a limit", and it is documented as applying to both
        // ceilings: with only the server limit raised, form parsing rejects the upload on its own limit.
        // Read the same way the server reads them - the last entry of each kind wins.
        Assert.Equal(5_000_000L, (long?)endpoint.Metadata.OfType<IRequestSizeLimitMetadata>().Last().MaxRequestBodySize);
        Assert.Equal(5_000_000L, (long?)endpoint.Metadata.OfType<IFormOptionsMetadata>().Last().MultipartBodyLengthLimit);
        Assert.Equal(TimeSpan.FromSeconds(120), Assert.Single(endpoint.Metadata.OfType<RequestTimeoutPolicy>()).Timeout);
        Assert.Contains(endpoint.Metadata.OfType<IAcceptsMetadata>(), metadata =>
            metadata.ContentTypes.Contains("multipart/form-data", StringComparer.OrdinalIgnoreCase));
        Assert.Contains(StatusCodes.Status500InternalServerError, SuccessAndErrorStatusCodes(app, "readme-uploads/documents"));
    }

    // Both limits are parameters, not constants: an app that raises them must get the raised values, on both
    // the request ceiling and the multipart form ceiling.
    [Fact]
    public async Task PostMultiPart_UsesTheLimitsTheCallerPasses()
    {
        await using var app = CreateReadmeApp();

        app.MapGroup("readme-uploads").PostMultiPart("large",
            async (ISender sender, [FromForm] ReadmeUploadDocumentCommand command, CancellationToken ct) =>
                await sender.Send(command, ct),
            maxRequestBodySize: 50_000_000,
            timeoutSeconds: 300,
            disableAntiforgery: true);

        var endpoint = app.Endpoint("readme-uploads/large");

        Assert.Equal(TimeSpan.FromSeconds(300), Assert.Single(endpoint.Metadata.OfType<RequestTimeoutPolicy>()).Timeout);
        Assert.Equal(50_000_000L, (long?)endpoint.Metadata.OfType<IRequestSizeLimitMetadata>().Last().MaxRequestBodySize);
        Assert.Equal(50_000_000L, (long?)endpoint.Metadata.OfType<IFormOptionsMetadata>().Last().MultipartBodyLengthLimit);
    }

    // Every other multipart assertion in this file (and in MultipartEndpointTests.cs) reads metadata. This is
    // the only place an upload actually travels: multipart parsing under the helper's limits, the antiforgery
    // opt-out the README documents for bearer-token APIs, and the mediator response mapped back to JSON. The
    // delegate binds the form as IFormCollection rather than as a [FromForm] command, because binding an
    // IFormFile *inside* a complex type is where the three target frameworks differ.
    [Fact]
    public async Task PostMultiPart_ReceivesAMultipartUploadAndReturnsTheHandlerResponse()
    {
        var builder = CreateReadmeBuilder(useTestServer: true);
        await using var app = builder.Build();

        app.MapGroup("readme-upload-http").PostMultiPart("documents",
            async (ISender sender, IFormCollection form, CancellationToken ct) =>
                await sender.Send(
                    new ReadmeUploadDocumentCommand { File = form.Files["file"], Title = form["title"].ToString() },
                    ct),
            disableAntiforgery: true);

        await app.StartAsync();
        using var client = app.GetTestClient();

        using var upload = new MultipartFormDataContent
        {
            { new StringContent("Spec"), "title" },
            { new ByteArrayContent("hello"u8.ToArray()), "file", "notes.txt" }
        };

        var response = await client.PostAsync("readme-upload-http/documents", upload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Title, file name and byte count: the handler sees the uploaded file, not just a bound title.
        Assert.Equal("Spec:notes.txt:5", JsonBody(await response.Content.ReadAsStringAsync()).GetProperty("result").GetString());
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task MultiPartHelpers_MapTheirOwnHttpMethod(string verb)
    {
        await using var app = CreateReadmeApp();

        var group = app.MapGroup("readme-multipartverbs");
        Delegate handler = async (ISender sender, [FromForm] ReadmeUploadDocumentCommand command, CancellationToken ct) =>
            await sender.Send(command, ct);

        switch (verb)
        {
            case "POST":
                group.PostMultiPart("x", handler, disableAntiforgery: true);
                break;
            case "PUT":
                group.PutMultiPart("x", handler, disableAntiforgery: true);
                break;
            default:
                group.PatchMultiPart("x", handler, disableAntiforgery: true);
                break;
        }

        Assert.Equal(
            new[] { verb },
            app.Endpoint("readme-multipartverbs/x").Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.ToArray());
    }

    // "The helpers log a warning at startup when the services are missing entirely." Without it the only signal
    // is a 500 at runtime whose cause is buried in the framework log.
    [Fact]
    public async Task PostMultiPart_WarnsWhenTheEndpointBindsFormDataAndAntiforgeryIsNotRegistered()
    {
        var logs = new RecordingLoggerProvider();
        var builder = CreateReadmeBuilder(loggerProvider: logs);
        RemoveAntiforgeryServices(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("readme-uploads").PostMultiPart("unprotected",
            async (ISender sender, [FromForm] ReadmeUploadDocumentCommand command, CancellationToken ct) =>
                await sender.Send(command, ct));

        Assert.Contains(logs.Warnings, entry => entry.Message.Contains("antiforgery", StringComparison.OrdinalIgnoreCase));
    }

    // The README's own snippet passes disableAntiforgery: true for bearer-token APIs. That takes the opt-out
    // branch instead of the warning branch, which is the observable difference the flag makes at map time.
    [Fact]
    public async Task PostMultiPart_DoesNotWarnWhenAntiforgeryIsExplicitlyDisabled()
    {
        var logs = new RecordingLoggerProvider();
        var builder = CreateReadmeBuilder(loggerProvider: logs);
        RemoveAntiforgeryServices(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("readme-uploads").PostMultiPart("documents",
            async (ISender sender, [FromForm] ReadmeUploadDocumentCommand command, CancellationToken ct) =>
                await sender.Send(command, ct), disableAntiforgery: true);

        Assert.DoesNotContain(logs.Warnings, entry => entry.Message.Contains("antiforgery", StringComparison.OrdinalIgnoreCase));
    }

    // "A delegate that reads HttpRequest.Form itself is never validated, whatever disableAntiforgery says" -
    // such a delegate binds no form parameter, so there is nothing for the helper to warn about either.
    [Fact]
    public async Task PostMultiPart_DoesNotWarnForADelegateThatDoesNotBindFormParameters()
    {
        var logs = new RecordingLoggerProvider();
        var builder = CreateReadmeBuilder(loggerProvider: logs);
        RemoveAntiforgeryServices(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("readme-uploads").PostMultiPart("manual", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            return Results.Ok(form.Count);
        });

        Assert.DoesNotContain(logs.Warnings, entry => entry.Message.Contains("antiforgery", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------------------------------------
    // README "Route, query, and header members in body requests"
    // ---------------------------------------------------------------------------------------------
    // MediatorRegressionTests.cs covers the JSON contract these rely on at the serializer level. These drive
    // real HTTP requests instead, because binding is the thing the README's snippets actually promise.

    // The README's UpdateStudentCommand snippet: a record with [FromRoute] Id, assigned in the endpoint with
    // "command with { Id = id }". The body deliberately carries a different id - a caller must not be able to
    // patch student 999 by posting its id to /readme-students/5.
    [Fact]
    public async Task Patch_RecordCommand_UsesTheRouteValueAndNotTheBodyValue()
    {
        var builder = CreateReadmeBuilder(useTestServer: true);
        await using var app = builder.Build();

        app.MapGroup("readme-students").Patch("{id}", (ISender sender, int id, ReadmeUpdateStudentCommand command, CancellationToken ct) =>
            sender.Send(command with { Id = id }, ct));

        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.PatchAsync("readme-students/5", JsonContent("""{"id":999,"name":"Ada"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5, await ReadResultAsync(response));
    }

    // "Positional records work the same way."
    [Fact]
    public async Task Patch_PositionalRecordCommand_UsesTheRouteValueAndNotTheBodyValue()
    {
        var builder = CreateReadmeBuilder(useTestServer: true);
        await using var app = builder.Build();

        app.MapGroup("readme-positional").Patch("{id}", (ISender sender, int id, ReadmeUpdateStudentPositionalCommand command, CancellationToken ct) =>
            sender.Send(command with { Id = id }, ct));

        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.PatchAsync("readme-positional/5", JsonContent("""{"id":999,"name":"Ada"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5, await ReadResultAsync(response));
    }

    // "Alternatively, let ASP.NET Core bind everything and skip the manual assignment" - the [FromBody] plus
    // [AsParameters] variant. Here the framework fills Id from the route, with no "with { }" in the endpoint.
    [Fact]
    public async Task Patch_FromBodyWithAsParameters_BindsTheRouteValueWithoutAManualAssignment()
    {
        var builder = CreateReadmeBuilder(useTestServer: true);
        await using var app = builder.Build();

        app.MapGroup("readme-asparameters").Patch("{id}", (ISender sender, [AsParameters] ReadmeUpdateStudentBodyCommand command, CancellationToken ct) =>
            sender.Send(command, ct));

        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.PatchAsync("readme-asparameters/5", JsonContent("""{"name":"Ada"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5, await ReadResultAsync(response));
    }

    // Request-shape table, row 2: "class with { get; set; }" is assigned with "command.Id = id;".
    [Fact]
    public async Task Patch_ClassCommandWithSetters_CanBeAssignedTheRouteValue()
    {
        var builder = CreateReadmeBuilder(useTestServer: true);
        await using var app = builder.Build();

        app.MapGroup("readme-settable").Patch("{id}", (ISender sender, int id, ReadmeSettableStudentCommand command, CancellationToken ct) =>
        {
            command.Id = id;
            return sender.Send(command, ct);
        });

        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.PatchAsync("readme-settable/5", JsonContent("""{"id":999,"name":"Ada"}"""));

        Assert.Equal(5, await ReadResultAsync(response));
    }

    // Request-shape table, row 3: "class with { get; init; }, or get-only properties set by a constructor" is
    // marked "not possible". The endpoint cannot write the member, so the handler sees default - which is
    // exactly the silent 0/Guid.Empty/null the README warns about.
    [Fact]
    public async Task Patch_ClassCommandWithInitOnlyMembers_LeavesTheRouteMemberAtItsDefault()
    {
        var builder = CreateReadmeBuilder(useTestServer: true);
        await using var app = builder.Build();

        app.MapGroup("readme-initonly").Patch("{id}", (ISender sender, int id, ReadmeInitOnlyStudentCommand command, CancellationToken ct) =>
            sender.Send(command, ct));

        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.PatchAsync("readme-initonly/5", JsonContent("""{"id":999,"name":"Ada"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await ReadResultAsync(response));
    }

    // The same forgotten assignment on a writable record: the member is excluded from the body, so it is
    // default rather than the 999 the caller sent. "An excluded member is always default after body binding."
    [Fact]
    public async Task Patch_RecordCommandWithoutTheAssignment_LeavesTheRouteMemberAtItsDefault()
    {
        var builder = CreateReadmeBuilder(useTestServer: true);
        await using var app = builder.Build();

        app.MapGroup("readme-forgotten").Patch("{id}", (ISender sender, int id, ReadmeUpdateStudentCommand command, CancellationToken ct) =>
            sender.Send(command, ct));

        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.PatchAsync("readme-forgotten/5", JsonContent("""{"id":999,"name":"Ada"}"""));

        Assert.Equal(0, await ReadResultAsync(response));
    }

    // "Only the JSON body is filtered. A request bound from a form ... still reads those members from the form
    // fields, so a caller can post Id=999 to /students/5." Documented limit, not a bug: assert it stays true.
    [Fact]
    public async Task Post_FormBoundRequest_StillReadsRouteMembersFromTheFormFields()
    {
        var builder = CreateReadmeBuilder(useTestServer: true);
        await using var app = builder.Build();

        app.MapGroup("readme-formbound").Post("{id}", (ISender sender, [FromForm] ReadmeSettableStudentCommand command, CancellationToken ct) =>
            sender.Send(command, ct)).DisableAntiforgery();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["Id"] = "999", ["Name"] = "Ada" });
        var response = await client.PostAsync("readme-formbound/5", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(999, await ReadResultAsync(response));
    }

    // "This only affects the Minimal API JSON options. Serializing the request with other
    // JsonSerializerOptions, for example in logs, still includes the member."
    [Fact]
    public void ExcludedRouteMembers_AreStillSerializedByOtherJsonSerializerOptions()
    {
        var services = new ServiceCollection();
        services.AddMediator();

        using var provider = services.BuildServiceProvider();
        var minimalApiOptions = provider.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;
        var command = new ReadmeUpdateStudentCommand { Id = 7, Name = "Ada" };

        // The control. Without it this test would pass just as well against a build where the exclusion never
        // happens anywhere, which is the one regression the paragraph it pins is there to bound.
        var minimalApiJson = JsonSerializer.Serialize(command, minimalApiOptions);
        Assert.DoesNotContain("id", minimalApiJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ada", minimalApiJson, StringComparison.Ordinal);

        Assert.Contains("\"Id\":7", JsonSerializer.Serialize(command), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // README "Authorization"
    // ---------------------------------------------------------------------------------------------
    // ReviewFixTests.cs covers the enum handling in depth. These pin the README's AdminEndpoints snippet and
    // each bullet under it, because the enum member name IS the role claim the identity provider must issue.

    // The README's AdminEndpoints group, mapped as written: one endpoint gated on a single role and one on two.
    [Fact]
    public async Task AdminEndpoints_GateTheirRoutesOnTheDocumentedRoles()
    {
        await using var app = CreateAuthorizationApp();

        new ReadmeHost<object>.ReadmeAdminEndpoints().Map(app);

        Assert.Equal("Admin", Assert.Single(AuthorizeData(app, "readmeadmin/admin/stats")).Roles);
        Assert.Equal("Admin,Manager", Assert.Single(AuthorizeData(app, "readmeadmin/reports")).Roles);
    }

    // "Enum member names are used as the role-claim values" - including their casing, since matching is
    // case-sensitive. Lower-casing the name here would 403 every caller.
    [Fact]
    public async Task RequireRole_SingleRole_UsesTheEnumMemberNameVerbatim()
    {
        await using var app = CreateAuthorizationApp();

        app.MapGroup("readme-auth").Get("stats", () => Results.Ok()).RequireRole(ReadmeAppRole.Admin);

        Assert.Equal("Admin", Assert.Single(AuthorizeData(app, "readme-auth/stats")).Roles);
    }

    // "Multiple roles in one call are OR-combined" - one requirement holding a comma-separated list, which is
    // AuthorizeAttribute.Roles semantics.
    [Fact]
    public async Task RequireRole_MultipleRolesInOneCall_ProduceOneOrCombinedRequirement()
    {
        await using var app = CreateAuthorizationApp();

        app.MapGroup("readme-auth").Post("reports", () => Results.Ok()).RequireRole(ReadmeAppRole.Admin, ReadmeAppRole.Manager);

        Assert.Equal("Admin,Manager", Assert.Single(AuthorizeData(app, "readme-auth/reports")).Roles);
    }

    // "Calling it more than once ... is AND: each call adds its own requirement." Collapsing these into one
    // requirement would quietly widen access from "both roles" to "either role".
    [Fact]
    public async Task RequireRole_CalledTwice_AddsOneRequirementPerCall()
    {
        await using var app = CreateAuthorizationApp();

        app.MapGroup("readme-auth").Get("both", () => Results.Ok())
            .RequireRole(ReadmeAppRole.Admin)
            .RequireRole(ReadmeAppRole.Manager);

        Assert.Equal(
            new string?[] { "Admin", "Manager" },
            AuthorizeData(app, "readme-auth/both").Select(static data => data.Roles).ToArray());
    }

    // "When your identity provider issues a different spelling, map it with [EnumMember]."
    [Fact]
    public async Task RequireRole_UsesTheEnumMemberValueWhenTheProviderSpellsTheRoleDifferently()
    {
        await using var app = CreateAuthorizationApp();

        app.MapGroup("readme-auth").Get("super", () => Results.Ok()).RequireRole(ReadmeAppRole.SuperAdmin);

        Assert.Equal("super-admin", Assert.Single(AuthorizeData(app, "readme-auth/super")).Roles);
    }

    // "[Flags] combinations (AppRole.Admin | AppRole.Manager) and undefined values are rejected with an
    // ArgumentException at startup: a combination is ambiguous." Also the second bullet of the upgrade notes.
    [Fact]
    public async Task RequireRole_RejectsFlagsCombinations()
    {
        await using var app = CreateAuthorizationApp();
        var group = app.MapGroup("readme-auth");

        var exception = Assert.Throws<ArgumentException>(() =>
            group.Get("flags", () => Results.Ok()).RequireRole(ReadmeFlagsRole.Admin | ReadmeFlagsRole.Manager));

        Assert.Contains("RequireRole(role1, role2)", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequireRole_RejectsUndefinedEnumValues()
    {
        await using var app = CreateAuthorizationApp();
        var group = app.MapGroup("readme-auth");

        var exception = Assert.Throws<ArgumentException>(() =>
            group.Get("undefined", () => Results.Ok()).RequireRole((ReadmeAppRole)42));

        Assert.Contains("is not a defined ReadmeAppRole member", exception.Message, StringComparison.Ordinal);
    }

    // "Calling RequireRole<TBuilder, TRole>() with no roles is equivalent to RequireAuthorization() (any
    // authenticated user)", and the README notes both type parameters have to be written out for that case.
    [Fact]
    public async Task RequireRole_WithNoRoles_IsEquivalentToRequireAuthorization()
    {
        await using var app = CreateAuthorizationApp();

        app.MapGroup("readme-auth").Get("any", () => Results.Ok()).RequireRole<RouteHandlerBuilder, ReadmeAppRole>();

        Assert.Null(Assert.Single(AuthorizeData(app, "readme-auth/any")).Roles);
    }

    // "Both type parameters are inferred at the call site" - RequireRole must work on more than
    // RouteHandlerBuilder, e.g. on a whole group, which is the README's "on the group and again on the
    // endpoint" example.
    [Fact]
    public async Task RequireRole_InfersBothTypeParametersOnAGroupBuilder()
    {
        await using var app = CreateAuthorizationApp();

        var group = app.MapGroup("readme-authgroup");
        group.RequireRole(ReadmeAppRole.Admin);
        group.Get("stats", () => Results.Ok()).RequireRole(ReadmeAppRole.Manager);

        var roles = AuthorizeData(app, "readme-authgroup/stats").Select(static data => data.Roles).ToArray();

        // Which of the two requirements routing records first is not documented; that there are two of them,
        // one per call, is.
        Assert.Equal(2, roles.Length);
        Assert.Contains("Admin", roles);
        Assert.Contains("Manager", roles);
    }

    // ---------------------------------------------------------------------------------------------
    // README "Validation"
    // ---------------------------------------------------------------------------------------------

    // "Validation failures are returned as 400 with the errors response body."
    [Fact]
    public async Task ValidationFailure_ReturnsBadRequestWithTheErrorsBody()
    {
        var builder = CreateReadmeBuilder(useTestServer: true);
        builder.Services.AddMediatorValidation(ReadmeValidatorAssembly());
        await using var app = builder.Build();

        app.UsePhoenixExceptionHandling();
        app.MapGroup("readme-validation").Post("students", (ISender sender, ReadmeCreateStudentCommand command, CancellationToken ct) =>
            sender.Send(command, ct));

        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.PostAsync("readme-validation/students", JsonContent("""{"name":""}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonBody(await response.Content.ReadAsStringAsync());
        Assert.Equal("Name is required.", body.GetProperty("errors")[0].GetString());
        Assert.True(body.TryGetProperty("traceId", out _));
    }

    // The same endpoint must let a valid request through, otherwise the test above would pass against a
    // behavior that rejects everything.
    [Fact]
    public async Task ValidationSuccess_LetsTheRequestReachTheHandler()
    {
        var builder = CreateReadmeBuilder(useTestServer: true);
        builder.Services.AddMediatorValidation(ReadmeValidatorAssembly());
        await using var app = builder.Build();

        app.UsePhoenixExceptionHandling();
        app.MapGroup("readme-validation").Post("students", (ISender sender, ReadmeCreateStudentCommand command, CancellationToken ct) =>
            sender.Send(command, ct));

        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.PostAsync("readme-validation/students", JsonContent("""{"name":"Ada"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Ada", JsonBody(await response.Content.ReadAsStringAsync()).GetProperty("result").GetString());
    }

    // "Visibility does not matter: public, internal, file-scoped and private nested validators are all
    // discovered, the same way handlers are." A validator skipped for being non-public is the silent-failure
    // case the next paragraph of the README describes: the request is simply never validated.
    [Fact]
    public async Task AddMediatorValidation_DiscoversValidatorsOfEveryVisibility()
    {
        var services = new ServiceCollection();

        services
            .AddMediator(new FakeAssembly("Readme.Visibility.Handlers", typeof(ReadmeVisibilityCommandHandler)))
            .AddMediatorValidation(new FakeAssembly(
                "Readme.Visibility",
                typeof(ReadmeHost<object>.ReadmePublicVisibilityValidator),
                typeof(ReadmeHost<object>.ReadmeInternalVisibilityValidator),
                typeof(ReadmeFileScopedVisibilityValidator)));

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var exception = await Assert.ThrowsAsync<HttpResponseException>(() =>
            scope.ServiceProvider.GetRequiredService<ISender>()
                .Send<ReadmeVisibilityCommand, SingleResponse<string>>(new ReadmeVisibilityCommand()));

        // Sorted: every validator ran, which is the claim. The order the scan yields them in is not documented.
        Assert.Equal(
            new[] { "file-scoped", "internal", "public" },
            exception.Errors.Order(StringComparer.Ordinal).ToArray());
    }

    // The private nested case from the same sentence, asserted at the registration the scan produces - that is
    // what "discovered" means here, and the type is not nameable from outside its declaring type.
    [Fact]
    public void AddMediatorValidation_DiscoversAPrivateNestedValidator()
    {
        var services = new ServiceCollection();

        services.AddMediatorValidation(new FakeAssembly(
            "Readme.Visibility.Private",
            ReadmeHost<object>.PrivateVisibilityValidatorType));

        Assert.Single(services, descriptor =>
            descriptor.ServiceType == typeof(IValidator<ReadmeVisibilityCommand>)
            && descriptor.ImplementationType == ReadmeHost<object>.PrivateVisibilityValidatorType);
    }

    // "A validator that is never registered fails silently ... invalid input is accepted exactly as if it had
    // been checked." ValidatorDiagnosticsTests.cs covers the startup warnings; this is the silence itself.
    [Fact]
    public async Task ValidationBehaviorWithoutValidators_AcceptsInvalidInput()
    {
        var services = new ServiceCollection();
        // No assembly is given to AddMediator or AddMediatorValidation and the handler is registered by hand, so nothing
        // is scanned: no IValidator<T> exists for this request and the pipeline has nothing to fail on.
        services.AddMediator().AddMediatorValidation();
        services.AddTransient<IRequestHandler<ReadmeCreateStudentCommand, SingleResponse<string>>, ReadmeCreateStudentCommandHandler>();

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var response = await sender.Send<ReadmeCreateStudentCommand, SingleResponse<string>>(new ReadmeCreateStudentCommand());

        Assert.Equal(string.Empty, response.Result);
    }

    // ---------------------------------------------------------------------------------------------
    // README "Pipeline behaviors"
    // ---------------------------------------------------------------------------------------------
    // RequestBehaviorTests.cs covers ordering, short-circuiting and constraints; this runs the README's behavior.

    // The README's TimingBehavior, registered the README's way: one class, a request with a response and one without.
    [Fact]
    public async Task TimingBehavior_RunsForARequestWithAResponseAndForOneWithout()
    {
        var logs = new RecordingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(logs));
        services.AddMediator(new FakeAssembly(
            "Readme.Behaviors",
            typeof(ReadmeGetGreetingQuery), typeof(ReadmeGetGreetingQueryHandler), typeof(ReadmeCompleteCommand), typeof(ReadmeCompleteCommandHandler)));

        services.AddMediatorBehavior(typeof(ReadmeTimingBehavior<>));

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var greeting = await sender.Send(new ReadmeGetGreetingQuery { Name = "Ada" });
        await sender.Send(new ReadmeCompleteCommand());

        Assert.Equal("Hello Ada", greeting.Result);
        Assert.Contains(logs.Messages, static message => message.StartsWith($"{nameof(ReadmeGetGreetingQuery)} took ", StringComparison.Ordinal));
        Assert.Contains(logs.Messages, static message => message.StartsWith($"{nameof(ReadmeCompleteCommand)} took ", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------
    // README "Checking handlers at startup"
    // ---------------------------------------------------------------------------------------------

    // The README's snippet: Throw in Development, Warn elsewhere.
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task MissingHandlerHandling_ThrowsInDevelopmentAndWarnsElsewhere(string environment)
    {
        var logs = new RecordingLoggerProvider();
        var builder = TestApps.CreateBuilder(environment, logs);
        builder.WebHost.UseTestServer();
        var assembly = new FakeAssembly("Readme.MissingHandlers", typeof(ReadmeUnhandledQuery));

        builder.Services.AddMediator(options =>
        {
            options.MissingHandlerHandling = builder.Environment.IsDevelopment()
                ? MissingHandlerHandling.Throw
                : MissingHandlerHandling.Warn;
        }, assembly);

        await using var app = builder.Build();

        if (builder.Environment.IsDevelopment())
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
            Assert.Contains(typeof(ReadmeUnhandledQuery).FullName!, exception.Message);
        }
        else
        {
            await app.StartAsync();
            Assert.Contains(logs.Warnings, static warning => warning.Message.Contains(typeof(ReadmeUnhandledQuery).FullName!));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // README "Optional logging helpers"
    // ---------------------------------------------------------------------------------------------

    // "Sentry PII remains disabled unless you explicitly set Sentry:SendDefaultPii=true. Client-IP log
    // enrichment is also off unless PII is enabled (or you pass logClientIp: true); the trace id is always
    // enriched." The client IP is personal data: logging it because a default flipped is a privacy incident
    // that no test failure would otherwise announce, and it lands in every log line of every request.
    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, "false", false)]
    [InlineData(null, "true", true)]
    [InlineData(true, null, true)]
    [InlineData(false, "true", false)]
    public async Task UsePhoenixRequestLogEnrichment_LogsTheClientIpOnlyWhenItIsEnabled(bool? logClientIp, string? sendDefaultPii, bool expectClientIp)
    {
        var logEvent = await CaptureEnrichedLogEventAsync(logClientIp, sendDefaultPii);

        Assert.Equal(
            expectClientIp ? ReadmeClientIpMiddleware.ClientIp : string.Empty,
            ScalarProperty(logEvent, "ClientIP"));
    }

    // "the trace id is always enriched" - checked in the default configuration, where the client IP is off:
    // the trace id is what correlates a log line with the traceId the documented error body carries, and
    // turning PII off must not take the correlation with it.
    [Fact]
    public async Task UsePhoenixRequestLogEnrichment_WithPiiOff_StillEnrichesTheTraceId()
    {
        var logEvent = await CaptureEnrichedLogEventAsync(logClientIp: null, sendDefaultPii: null);

        Assert.False(string.IsNullOrWhiteSpace(ScalarProperty(logEvent, "TraceId")));
        Assert.Equal(string.Empty, ScalarProperty(logEvent, "ClientIP"));
    }

    // ---------------------------------------------------------------------------------------------
    // README "Upgrading / 2.3.0"
    // ---------------------------------------------------------------------------------------------

    // "API versioning no longer fails startup." Owned by DuplicateEndpointTests.cs.
    [Fact]
    public void Upgrade_OneEndpointPerApiVersionIsNotReportedAsADuplicate()
    {
        var builder = TestApps.CreateBuilder();
        builder.Services.AddMediator();
        builder.Services.AddApiVersioning();
        var app = builder.Build();
        var versions = app.NewApiVersionSet().HasApiVersion(new Asp.Versioning.ApiVersion(1, 0)).HasApiVersion(new Asp.Versioning.ApiVersion(2, 0)).Build();

        app.MapGroup("readme-versions").Get("/", () => "v1").WithApiVersionSet(versions).MapToApiVersion(1.0);
        app.MapGroup("readme-versions").Get("/", () => "v2").WithApiVersionSet(versions).MapToApiVersion(2.0);

        app.ValidateNoDuplicateEndpoints();
    }

    // "A bare null passed with one explicit type argument no longer compiles; cast it." The cast form, compiled here.
    [Fact]
    public async Task Upgrade_ANullRequestWithOneExplicitTypeArgument_CompilesOnceCast()
    {
        await using var provider = CreateReadmeProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await Assert.ThrowsAsync<ArgumentNullException>(() => sender.Send<IRequest>((IRequest)null!));
    }

    // ---------------------------------------------------------------------------------------------
    // README "Upgrading / 2.1.0"
    // ---------------------------------------------------------------------------------------------
    // Every bullet in that list is a promise an upgrading consumer reads and then relies on. Most are covered
    // in depth elsewhere in the suite; the duplication here is deliberate, so that editing the README against
    // the code (or the code against the README) trips a test named after the bullet. The file that owns each
    // behavior is named in the comment above it.

    // "Duplicate handlers now fail at startup ... AddMediator/AddMediatorHandlers now throw and name both
    // types." Depth: ReviewFixTests.AddMediatorHandlers_ThrowsWhenTwoHandlersHandleTheSameRequest.
    [Fact]
    public void Upgrade_DuplicateHandlersFailAtStartupAndNameBothTypes()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddMediatorHandlers(
            new FakeAssembly(
                "Readme.DuplicateHandlers",
                typeof(ReadmeHost<object>.ReadmeFirstDuplicateHandler),
                typeof(ReadmeHost<object>.ReadmeSecondDuplicateHandler))));

        Assert.Contains("ReadmeFirstDuplicateHandler", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ReadmeSecondDuplicateHandler", exception.Message, StringComparison.Ordinal);
    }

    // "RequireRole rejects [Flags] combinations and undefined enum values with an ArgumentException at
    // startup" is asserted above in RequireRole_RejectsFlagsCombinations /
    // RequireRole_RejectsUndefinedEnumValues. Depth: ReviewFixTests.RequireRole_RejectsFlagsCombinationsAndUndefinedValues.

    // "Cancelled requests are no longer turned into 500." 3.0.0 refined how: only a timeout is rethrown, and a
    // client disconnect ends with 499 (asserted in the 3.0.0 test below).
    // Depth: ReviewFixTests.ExceptionHandlingMiddleware_DoesNotReportCancellationAsAServerError_WhenRequestWasAborted.
    [Fact]
    public async Task Upgrade_CancelledRequestsAreNoLongerTurnedIntoServerErrors()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        var response = await RunExceptionMiddlewareAsync(
            new OperationCanceledException(aborted.Token),
            requestAborted: aborted.Token);

        Assert.NotEqual(StatusCodes.Status500InternalServerError, response.StatusCode);
    }

    // README "Upgrading / 3.0.0", kept beside the 2.1.0 note it refines: "A client that disconnects no longer leaves
    // an unhandled exception. The exception-handling middleware ends the request with 499 ... A request timeout is
    // still rethrown, so UseRequestTimeouts writes its 504." Depth: ExceptionHandlingMiddlewareTests, the
    // BehindUseRequestTimeouts tests.
    [Fact]
    public async Task Upgrade_AClientDisconnectEndsWith499AndATimeoutIsStillRethrown()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        var disconnect = await RunExceptionMiddlewareAsync(
            new OperationCanceledException(aborted.Token),
            requestAborted: aborted.Token);

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, disconnect.StatusCode);
        Assert.Empty(disconnect.Body);

        var timedOut = new DefaultHttpContext { RequestAborted = aborted.Token };
        timedOut.Features.Set<IHttpRequestTimeoutFeature>(new TestRequestTimeoutFeature(aborted.Token));
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new OperationCanceledException(aborted.Token),
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            new ConfigurationBuilder().Build());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => middleware.InvokeAsync(timedOut));
    }

    // A cancellation that is NOT an abort is still a server fault, so the exemption must not swallow every
    // OperationCanceledException.
    [Fact]
    public async Task Upgrade_CancellationWithoutAnAbortIsStillReportedAsAServerError()
    {
        var response = await RunExceptionMiddlewareAsync(new OperationCanceledException("handler gave up"));

        Assert.Equal(StatusCodes.Status500InternalServerError, response.StatusCode);
    }

    // "Framework bad requests keep their status code. Malformed JSON, missing required parameters, invalid
    // antiforgery tokens and oversized forms return their real status (400, 413, ...) with the standard
    // {"errors":[...],"traceId":"..."} body." Depth: ReviewFixTests.ExceptionHandlingMiddleware_KeepsStatusCodeFromBadHttpRequestException.
    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status413PayloadTooLarge)]
    [InlineData(StatusCodes.Status415UnsupportedMediaType)]
    public async Task Upgrade_FrameworkBadRequestsKeepTheirStatusCode(int statusCode)
    {
        var response = await RunExceptionMiddlewareAsync(new BadHttpRequestException("Request rejected.", statusCode));

        Assert.Equal(statusCode, response.StatusCode);
        Assert.Equal("Request rejected.", response.Json.GetProperty("errors")[0].GetString());
        Assert.True(response.Json.TryGetProperty("traceId", out _));
    }

    // "UnauthorizedAccessException is logged (still mapped to 401; since 3.0.0 a 500 unless MapCommonExceptions is on).
    // .NET throws it for file-permission errors too, so it should never pass silently."
    // Depth: ReviewFixTests.ExceptionHandlingMiddleware_LogsUnauthorizedAccessException.
    [Fact]
    public async Task Upgrade_UnauthorizedAccessExceptionIsLogged_AndIs401OnlyWithMapCommonExceptions()
    {
        var logs = new RecordingLoggerProvider();

        var mapped = await RunExceptionMiddlewareAsync(
            new UnauthorizedAccessException("Access to the path 'x' is denied."),
            logger: new Logger<ExceptionHandlingMiddleware>(logs),
            options: new ExceptionHandlingOptions { MapCommonExceptions = true });
        var byDefault = await RunExceptionMiddlewareAsync(new UnauthorizedAccessException("Access to the path 'x' is denied."));

        Assert.Equal(StatusCodes.Status401Unauthorized, mapped.StatusCode);
        Assert.Contains(logs.Warnings, entry => entry.Exception is UnauthorizedAccessException);
        Assert.Equal(StatusCodes.Status500InternalServerError, byDefault.StatusCode);
    }

    // "MultiResponse<T> takes an IReadOnlyList<T> in its constructor and exposes PageSize, so it can be
    // deserialized as well as serialized." Depth: ReviewFixTests.MultiResponse_RoundTripsThroughSystemTextJson.
    [Fact]
    public void Upgrade_MultiResponseCanBeReadBackFromJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(new MultiResponse<string>(["a", "b"], totalCount: 5, pageSize: 2), options);

        var roundTripped = JsonSerializer.Deserialize<MultiResponse<string>>(json, options)!;

        Assert.Equal(new[] { "a", "b" }, roundTripped.Data.ToArray());
        Assert.Equal(5, roundTripped.TotalCount);
        Assert.Equal(2, roundTripped.PageSize);
        Assert.Equal(3, roundTripped.PagesCount);
    }

    // "BaseEndpointGroup.GroupName lower-cases with the invariant culture, so Turkish/Azerbaijani servers no
    // longer produce 'invoice' route prefixes with a dotless i."
    // Depth: ReviewFixTests.GroupName_IsCultureInvariant.
    [Theory]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    [InlineData("az-Latn-AZ")]
    public void Upgrade_GroupNameLowerCasesWithTheInvariantCulture(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            Assert.Equal("readmeinvoice", new ReadmeHost<object>.ReadmeInvoiceEndpoints().GroupName);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // "2.0.6: AddMediator began excluding mediator-request members marked [FromRoute]/[FromQuery]/[FromHeader]
    // from the JSON body. If an app previously relied on those values arriving in the body, they now arrive as
    // default." Asserted over HTTP above in Patch_RecordCommandWithoutTheAssignment_LeavesTheRouteMemberAtItsDefault;
    // this is the same promise at the level the upgrade note is written at, for [FromQuery] and [FromHeader] too.
    [Fact]
    public void Upgrade_QueryAndHeaderMembersAlsoArriveAsDefaultFromTheBody()
    {
        var services = new ServiceCollection();
        services.AddMediator();

        using var provider = services.BuildServiceProvider();
        var serializerOptions = provider.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;

        var command = JsonSerializer.Deserialize<ReadmeNonBodyMembersCommand>(
            """{"id":5,"force":true,"tenant":"acme","name":"Ada"}""",
            serializerOptions)!;

        Assert.Equal(0, command.Id);
        Assert.False(command.Force);
        Assert.Null(command.Tenant);
        Assert.Equal("Ada", command.Name);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The README's request and handler as their own assembly, so the quick-start registration snippet can be
    /// exercised without disturbing the scans other test files run over the real test assembly.
    /// </summary>
    private static Assembly ReadmeGreetingAssembly()
    {
        return new FakeAssembly(
            "Readme.QuickStart",
            typeof(ReadmeGetGreetingQuery),
            typeof(ReadmeGetGreetingQueryHandler),
            typeof(ReadmeCreateStudentCommand),
            typeof(ReadmeCreateStudentCommandHandler));
    }

    /// <summary>
    /// The README's validator as its own assembly. The validator itself is nested in a generic host so the
    /// real assembly's validator scan skips it and it never runs for other test files' requests.
    /// </summary>
    private static Assembly ReadmeValidatorAssembly()
    {
        return new FakeAssembly("Readme.Validators", typeof(ReadmeHost<object>.ReadmeCreateStudentCommandValidator));
    }

    private static WebApplicationBuilder CreateReadmeBuilder(
        Action<MediatorOptions>? configureOptions = null,
        ILoggerProvider? loggerProvider = null,
        bool useTestServer = false)
    {
        var builder = TestApps.CreateBuilder(loggerProvider: loggerProvider);

        if (useTestServer)
            builder.WebHost.UseTestServer();

        var assembly = typeof(ReadmeExampleTests).Assembly;

        if (configureOptions is null)
            builder.Services.AddMediator(assembly);
        else
            builder.Services.AddMediator(configureOptions, assembly);

        return builder;
    }

    private static WebApplication CreateReadmeApp(
        Action<MediatorOptions>? configureOptions = null,
        ILoggerProvider? loggerProvider = null,
        bool registerDiscoveredGroupDependencies = false)
    {
        var builder = CreateReadmeBuilder(configureOptions, loggerProvider);

        if (registerDiscoveredGroupDependencies)
            RegisterDiscoveredGroupDependencies(builder.Services);

        return builder.Build();
    }

    private static ServiceProvider CreateReadmeProvider()
    {
        var services = new ServiceCollection();
        services.AddMediator(typeof(ReadmeExampleTests).Assembly);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static WebApplication CreateAuthorizationApp()
    {
        var builder = TestApps.CreateBuilder();
        builder.Services.AddMediator(typeof(ReadmeExampleTests).Assembly);
        builder.Services.AddAuthorization();
        return builder.Build();
    }

    /// <summary>
    /// A started test-server app whose discovery sees only <paramref name="groups"/> (the quick start group by default):
    /// the application name matches no assembly, so the other groups elsewhere in this assembly are not mapped under
    /// the shared settings. Callers are signed in through the role header.
    /// </summary>
    private static async Task<WebApplication> StartSharedSettingsAppAsync(MapEndpointsOptions options, params Type[] groups)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = "Phoenix.Mediator.Tests.ReadmeSharedSettings",
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddMediator(ReadmeGreetingAssembly());
        builder.Services
            .AddAuthentication(RoleHeaderAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, RoleHeaderAuthenticationHandler>(RoleHeaderAuthenticationHandler.SchemeName, static _ => { });
        builder.Services.AddAuthorization();

        var app = builder.Build();
        try
        {
            app.MapEndpoints(options, new FakeAssembly(
                "Readme.SharedSettings.Groups",
                groups.Length > 0 ? groups : [typeof(ReadmeHost<object>.ReadmeGreetingEndpoints)]));
            await app.StartAsync();
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// A started test-server app for the README's error-handling claims: the Phoenix middleware, the validation package,
    /// and one endpoint per kind of failure under <c>readme-errors/</c>.
    /// </summary>
    private static async Task<WebApplication> StartErrorReadmeAppAsync(Action<IServiceCollection>? configure, ILoggerProvider? loggerProvider = null)
    {
        var builder = TestApps.CreateBuilder(loggerProvider: loggerProvider);
        builder.WebHost.UseTestServer();
        builder.Services.AddMediator().AddMediatorValidation();
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        app.UsePhoenixExceptionHandling();

        var errors = app.MapGroup("readme-errors");
        errors.Post("subscribe", (ISender sender, ReadmeSubscribeCommand command, CancellationToken ct) => sender.Send(command, ct));
        errors.MapGet("concurrency", () => { throw new ReadmeStandIns.DbUpdateConcurrencyException("0 rows were affected."); });
        errors.MapGet("circuit", () => { throw new ReadmeStandIns.BrokenCircuitException("The circuit is now open."); });
        errors.MapGet("argument", () => { throw new ArgumentException("readme-argument"); });
        errors.MapGet("missing", () => { throw new NotFoundException("Student 5 was not found."); });
        errors.MapGet("boom", () => { throw new InvalidOperationException("readme-boom"); });

        await app.StartAsync();
        return app;
    }

    /// <summary>
    /// MapEndpoints() constructs every non-generic <see cref="BaseEndpointGroup"/> in this assembly from DI,
    /// including the ones other test files declare. Their constructor dependencies are registered here by
    /// reflection, so this file names no type it does not own and a discovery pass cannot fail for a reason
    /// unrelated to the README claim under test.
    /// </summary>
    private static void RegisterDiscoveredGroupDependencies(IServiceCollection services)
    {
        var dependencies = typeof(ReadmeExampleTests).Assembly
            .GetTypes()
            .Where(static type => type.IsClass
                && !type.IsAbstract
                && !type.ContainsGenericParameters
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
    /// Drops the antiforgery services so the "no antiforgery services are registered" branch is reached
    /// whether or not the host happens to register them by default on this framework version.
    /// </summary>
    private static void RemoveAntiforgeryServices(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(static d => d.ServiceType == typeof(IAntiforgery)).ToArray())
            services.Remove(descriptor);
    }

    private static IReadOnlyList<IAuthorizeData> AuthorizeData(WebApplication app, string routeSuffix)
    {
        return app.Endpoint(routeSuffix).Metadata.GetOrderedMetadata<IAuthorizeData>();
    }

    private static int[] SuccessAndErrorStatusCodes(WebApplication app, string routeSuffix)
    {
        return app.Endpoint(routeSuffix).Metadata
            .OfType<IProducesResponseTypeMetadata>()
            .Select(static metadata => metadata.StatusCode)
            .ToArray();
    }

    private static RouteHandlerBuilder MapByVerb(IEndpointRouteBuilder builder, string verb, string pattern, Delegate handler)
    {
        return verb switch
        {
            "GET" => builder.Get(pattern, handler),
            "POST" => builder.Post(pattern, handler),
            "PUT" => builder.Put(pattern, handler),
            "DELETE" => builder.Delete(pattern, handler),
            "PATCH" => builder.Patch(pattern, handler),
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "Unsupported verb.")
        };
    }

    private static StringContent JsonContent(string json)
    {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    /// <summary>
    /// Runs one request through <c>UsePhoenixRequestLogEnrichment</c> and returns the event a Serilog logger
    /// wrote from inside it, so the ambient properties the middleware pushed can be read back. The logger is
    /// local to the call: nothing here touches Serilog's static <c>Log</c> or writes a file.
    /// </summary>
    private static async Task<LogEvent> CaptureEnrichedLogEventAsync(bool? logClientIp, string? sendDefaultPii)
    {
        var sink = new ReadmeCapturingSink();
        using var logger = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();

        var builder = CreateReadmeBuilder(useTestServer: true);

        if (sendDefaultPii is not null)
        {
            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Sentry:SendDefaultPii"] = sendDefaultPii });
        }

        await using var app = builder.Build();

        // TestServer leaves the connection's remote address unset, and the enricher reads exactly that.
        app.UseMiddleware<ReadmeClientIpMiddleware>();
        app.UsePhoenixRequestLogEnrichment(logClientIp);
        app.MapGet("readme-enrichment/log", () =>
        {
            logger.Information("inside the request");
            return Results.Ok();
        });

        await app.StartAsync();
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("readme-enrichment/log")).StatusCode);

        return Assert.Single(sink.Events);
    }

    /// <summary>The value of a scalar log property, or the empty string when it is absent.</summary>
    private static string ScalarProperty(LogEvent logEvent, string name)
    {
        return logEvent.Properties.TryGetValue(name, out var value) && value is ScalarValue { Value: string text }
            ? text
            : string.Empty;
    }

    private static JsonElement JsonBody(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<int> ReadResultAsync(HttpResponseMessage response)
    {
        return JsonBody(await response.Content.ReadAsStringAsync()).GetProperty("result").GetInt32();
    }

    private static bool ContainsException<TException>(Exception? exception) where TException : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException)
                return true;
        }

        return false;
    }

    /// <summary>The README's ErrorMessages values, keyed the way the README's JSON block keys them.</summary>
    private static Dictionary<string, string> ReadmeErrorMessages()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // The README's Arabic unknown-error message, kept verbatim so the lookup really is by key.
            ["Ar"] = "حصل خطأ غير معرف",
            ["En"] = "Unknown error occurred"
        };
    }

    private static IConfiguration ReadmeErrorMessagesConfiguration()
    {
        var messages = ReadmeErrorMessages();

        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ErrorMessages:Default"] = "En",
                ["ErrorMessages:Ar"] = messages["Ar"],
                ["ErrorMessages:En"] = messages["En"]
            })
            .Build();
    }

    private static async Task<ReadmeMiddlewareResponse> RunExceptionMiddlewareAsync(
        Exception exception,
        IConfiguration? configuration = null,
        string? acceptLanguage = null,
        ILogger<ExceptionHandlingMiddleware>? logger = null,
        CancellationToken requestAborted = default,
        ExceptionHandlingOptions? options = null)
    {
        var context = new DefaultHttpContext { RequestAborted = requestAborted };
        context.Response.Body = new MemoryStream();

        if (!string.IsNullOrWhiteSpace(acceptLanguage))
            context.Request.Headers.AcceptLanguage = acceptLanguage;

        var middleware = options is null
            ? new ExceptionHandlingMiddleware(
                _ => throw exception,
                logger ?? NullLogger<ExceptionHandlingMiddleware>.Instance,
                configuration ?? new ConfigurationBuilder().Build())
            : new ExceptionHandlingMiddleware(
                _ => throw exception,
                logger ?? NullLogger<ExceptionHandlingMiddleware>.Instance,
                configuration ?? new ConfigurationBuilder().Build(),
                Options.Create(options));

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);

        return new ReadmeMiddlewareResponse(context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private sealed record ReadmeMiddlewareResponse(int StatusCode, string Body)
    {
        public JsonElement Json
        {
            get
            {
                using var document = JsonDocument.Parse(Body);
                return document.RootElement.Clone();
            }
        }
    }
}

// =================================================================================================
// The README's own types, renamed with this file's prefix.
//
// Everything at namespace scope here is picked up by the assembly scans other test files run, so each
// request has exactly one handler and every handler has a parameterless constructor. Types that must stay
// invisible to those scans (endpoint groups, the validator, the second handler for one request) live in
// ReadmeHost<TMarker> at the bottom.
// =================================================================================================

/// <summary>README "2. Create a request + handler".</summary>
public sealed class ReadmeGetGreetingQuery : IRequest<SingleResponse<string>>
{
    public string Name { get; set; } = string.Empty;
}

public sealed class ReadmeGetGreetingQueryHandler : IRequestHandler<ReadmeGetGreetingQuery, SingleResponse<string>>
{
    public Task<SingleResponse<string>> Handle(ReadmeGetGreetingQuery request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new SingleResponse<string>($"Hello {request.Name}"));
    }
}

/// <summary>An <c>IRequest</c> with no response, for the configured empty-response status.</summary>
public sealed class ReadmeCompleteCommand : IRequest
{
}

public sealed class ReadmeCompleteCommandHandler : IRequestHandler<ReadmeCompleteCommand>
{
    public Task Handle(ReadmeCompleteCommand request, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Carries its own sink so a test can prove the handler ran without any static state, which would not
/// survive the test classes running in parallel.
/// </summary>
public sealed class ReadmeNoteCommand : IRequest
{
    public List<string> Handled { get; } = [];
}

public sealed class ReadmeNoteCommandHandler : IRequestHandler<ReadmeNoteCommand>
{
    public Task Handle(ReadmeNoteCommand request, CancellationToken cancellationToken)
    {
        request.Handled.Add("handled");
        return Task.CompletedTask;
    }
}

/// <summary>README "Validation": the request whose validator rejects an empty name.</summary>
public sealed class ReadmeCreateStudentCommand : IRequest<SingleResponse<string>>
{
    public string Name { get; set; } = string.Empty;
}

public sealed class ReadmeCreateStudentCommandHandler : IRequestHandler<ReadmeCreateStudentCommand, SingleResponse<string>>
{
    public Task<SingleResponse<string>> Handle(ReadmeCreateStudentCommand request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new SingleResponse<string>(request.Name));
    }
}

/// <summary>
/// README "Validation": the request whose validators are declared at four different visibilities. Each
/// validator reports its own visibility as the failure message, so one rejection names every one that ran.
/// </summary>
public sealed class ReadmeVisibilityCommand : IRequest<SingleResponse<string>>
{
    public string Name { get; set; } = string.Empty;
}

public sealed class ReadmeVisibilityCommandHandler : IRequestHandler<ReadmeVisibilityCommand, SingleResponse<string>>
{
    public Task<SingleResponse<string>> Handle(ReadmeVisibilityCommand request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new SingleResponse<string>(request.Name));
    }
}

/// <summary>README "Built-in exception types": NotFoundException.</summary>
public sealed class ReadmeMissingStudentQuery : IRequest<SingleResponse<string>>
{
}

public sealed class ReadmeMissingStudentQueryHandler : IRequestHandler<ReadmeMissingStudentQuery, SingleResponse<string>>
{
    public Task<SingleResponse<string>> Handle(ReadmeMissingStudentQuery request, CancellationToken cancellationToken)
        => throw new NotFoundException("Student 5 was not found.");
}

/// <summary>README "Built-in exception types": BadRequestException.</summary>
public sealed class ReadmeInvalidStudentQuery : IRequest<SingleResponse<string>>
{
}

public sealed class ReadmeInvalidStudentQueryHandler : IRequestHandler<ReadmeInvalidStudentQuery, SingleResponse<string>>
{
    public Task<SingleResponse<string>> Handle(ReadmeInvalidStudentQuery request, CancellationToken cancellationToken)
        => throw new BadRequestException("Student id must be positive.");
}

/// <summary>
/// README: "HttpResponseException (or derived exceptions)". A project's own exception type only has to hand
/// the base class an ErrorResponse to get the documented status code and body.
/// </summary>
public sealed class ReadmeConflictException(string message)
    : HttpResponseException(new ErrorResponse(HttpStatusCode.Conflict, [message]));

/// <summary>README "Route, query, and header members in body requests": the UpdateStudentCommand snippet.</summary>
public record ReadmeUpdateStudentCommand : IRequest<SingleResponse<int>>
{
    [FromRoute]
    public int Id { get; init; }

    public string? Name { get; init; }
}

public sealed class ReadmeUpdateStudentCommandHandler : IRequestHandler<ReadmeUpdateStudentCommand, SingleResponse<int>>
{
    public Task<SingleResponse<int>> Handle(ReadmeUpdateStudentCommand request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new SingleResponse<int>(request.Id));
    }
}

/// <summary>README: "Positional records work the same way."</summary>
public record ReadmeUpdateStudentPositionalCommand([FromRoute] int Id, string? Name) : IRequest<SingleResponse<int>>;

public sealed class ReadmeUpdateStudentPositionalCommandHandler : IRequestHandler<ReadmeUpdateStudentPositionalCommand, SingleResponse<int>>
{
    public Task<SingleResponse<int>> Handle(ReadmeUpdateStudentPositionalCommand request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new SingleResponse<int>(request.Id));
    }
}

/// <summary>README: the [FromBody] + [AsParameters] alternative that needs no manual assignment.</summary>
public record ReadmeStudentBody(string? Name);

public record ReadmeUpdateStudentBodyCommand([FromRoute] int Id, [FromBody] ReadmeStudentBody Body) : IRequest<SingleResponse<int>>;

public sealed class ReadmeUpdateStudentBodyCommandHandler : IRequestHandler<ReadmeUpdateStudentBodyCommand, SingleResponse<int>>
{
    public Task<SingleResponse<int>> Handle(ReadmeUpdateStudentBodyCommand request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new SingleResponse<int>(request.Id));
    }
}

/// <summary>Request-shape table, row 2: a class with settable properties, assigned in the endpoint.</summary>
public sealed class ReadmeSettableStudentCommand : IRequest<SingleResponse<int>>
{
    [FromRoute]
    public int Id { get; set; }

    public string? Name { get; set; }
}

public sealed class ReadmeSettableStudentCommandHandler : IRequestHandler<ReadmeSettableStudentCommand, SingleResponse<int>>
{
    public Task<SingleResponse<int>> Handle(ReadmeSettableStudentCommand request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new SingleResponse<int>(request.Id));
    }
}

/// <summary>Request-shape table, row 3: a class with init-only members, which the endpoint cannot write to.</summary>
public sealed class ReadmeInitOnlyStudentCommand : IRequest<SingleResponse<int>>
{
    [FromRoute]
    public int Id { get; init; }

    public string? Name { get; init; }
}

public sealed class ReadmeInitOnlyStudentCommandHandler : IRequestHandler<ReadmeInitOnlyStudentCommand, SingleResponse<int>>
{
    public Task<SingleResponse<int>> Handle(ReadmeInitOnlyStudentCommand request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new SingleResponse<int>(request.Id));
    }
}

/// <summary>
/// All three non-body sources at once, for the 2.0.6 upgrade note. Never sent, so it needs no handler.
/// </summary>
public sealed record ReadmeNonBodyMembersCommand : IRequest<SingleResponse<int>>
{
    [FromRoute]
    public int Id { get; init; }

    [FromQuery]
    public bool Force { get; init; }

    [FromHeader(Name = "X-Tenant")]
    public string? Tenant { get; init; }

    public string? Name { get; init; }
}

/// <summary>README "File uploads (multipart)": the UploadDocumentCommand the snippet posts.</summary>
public sealed class ReadmeUploadDocumentCommand : IRequest<SingleResponse<string>>
{
    public IFormFile? File { get; set; }

    public string? Title { get; set; }
}

public sealed class ReadmeUploadDocumentCommandHandler : IRequestHandler<ReadmeUploadDocumentCommand, SingleResponse<string>>
{
    /// <summary>
    /// Reports the form field, the uploaded file's name and its size, so an end-to-end multipart test can tell
    /// "the command was bound" apart from "the file actually arrived".
    /// </summary>
    public Task<SingleResponse<string>> Handle(ReadmeUploadDocumentCommand request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new SingleResponse<string>(
            $"{request.Title}:{request.File?.FileName ?? "no-file"}:{request.File?.Length ?? 0}"));
    }
}

/// <summary>
/// Handled by two competing handlers, both nested in <see cref="ReadmeHost{TMarker}"/> so the real assembly
/// scan never sees them. Deliberately has no handler at namespace scope.
/// </summary>
public sealed class ReadmeDuplicateHandlerRequest : IRequest<SingleResponse<string>>
{
}

/// <summary>README "Authorization": the AppRole enum, plus the [EnumMember] spelling the notes describe.</summary>
public enum ReadmeAppRole
{
    Admin,
    Manager,
    User,

    [EnumMember(Value = "super-admin")]
    SuperAdmin
}

/// <summary>A [Flags] enum, which RequireRole must reject: a combination is ambiguous.</summary>
[Flags]
public enum ReadmeFlagsRole
{
    None = 0,
    Admin = 1,
    Manager = 2
}

/// <summary>README "Validation": the request a handler validates on its own, with ValidateAndThrowAsync.</summary>
public sealed class ReadmeSubscribeCommand : IRequest<SingleResponse<string>>
{
    public string Email { get; set; } = string.Empty;
}

/// <summary>README "Checking handlers at startup": deliberately left without a handler, so the check reports it.</summary>
public sealed class ReadmeUnhandledQuery : IRequest<string>;

/// <summary>Stand-ins for the two exception types the README's mapping snippet names, from EF Core and Polly.</summary>
public static class ReadmeStandIns
{
    public sealed class DbUpdateConcurrencyException(string message) : Exception(message);

    public sealed class BrokenCircuitException(string message) : Exception(message);
}

/// <summary>README "Pipeline behaviors": the TimingBehavior, verbatim apart from its name.</summary>
public sealed class ReadmeTimingBehavior<TRequest>(ILogger<ReadmeTimingBehavior<TRequest>> logger) : IRequestBehavior<TRequest>
{
    public async Task<TResponse> Handle<TResponse>(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await next();
        }
        finally
        {
            logger.LogInformation("{Request} took {ElapsedMs} ms", typeof(TRequest).Name, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}

/// <summary>
/// Endpoint groups, a validator and a duplicate handler pair, nested inside an open generic so they inherit
/// its type parameter. <c>ContainsGenericParameters</c> is therefore true for every one of them and the
/// assembly-wide scans that <c>MapEndpoints()</c>, <c>AddMediatorHandlers</c> and
/// <c>AddMediatorValidation</c> run over the real test assembly skip them. Tests close the host
/// (<c>ReadmeHost&lt;object&gt;</c>) and map or register what they need by hand.
/// </summary>
public static class ReadmeHost<TMarker>
{
    /// <summary>README "3. Map endpoints via endpoint groups", verbatim apart from the prefix.</summary>
    public sealed class ReadmeGreetingEndpoints : BaseEndpointGroup
    {
        public override void Map(IEndpointRouteBuilder app)
        {
            app.MapGroup(GroupName)
                .Get("hello", async (ISender sender, [AsParameters] ReadmeGetGreetingQuery query, CancellationToken ct) =>
                    await sender.Send(query, ct));
        }
    }

    /// <summary>README "Authorization": the AdminEndpoints group, with its elided bodies filled in.</summary>
    public sealed class ReadmeAdminEndpoints : BaseEndpointGroup
    {
        public override void Map(IEndpointRouteBuilder app)
        {
            app.MapGroup(GroupName)
                .Get("admin/stats", (ISender sender, CancellationToken ct) =>
                    sender.Send(new ReadmeGetGreetingQuery { Name = "stats" }, ct))
                .RequireRole(ReadmeAppRole.Admin);                  // single role

            app.MapGroup(GroupName)
                .Post("reports", (ISender sender, CancellationToken ct) =>
                    sender.Send(new ReadmeGetGreetingQuery { Name = "reports" }, ct))
                .RequireRole(ReadmeAppRole.Admin, ReadmeAppRole.Manager);  // OR - either role works
        }
    }

    /// <summary>README "Duplicate routes": the UserEndpoints half of "GET users/{id}" vs "GET users/{userId}".</summary>
    public sealed class ReadmeDuplicateUserEndpoints : BaseEndpointGroup
    {
        public override void Map(IEndpointRouteBuilder app)
            => app.MapGroup("readme-dupes").Get("{id}", (string id) => Results.Ok(id));
    }

    /// <summary>The AdminEndpoints half: the same route shape under a different parameter name.</summary>
    public sealed class ReadmeDuplicateAdminEndpoints : BaseEndpointGroup
    {
        public override void Map(IEndpointRouteBuilder app)
            => app.MapGroup("readme-dupes").Get("{userId}", (string userId) => Results.Ok(userId));
    }

    /// <summary>The README's Turkish-server example for GroupName. Maps nothing; only its name is asserted on.</summary>
    public sealed class ReadmeInvoiceEndpoints : BaseEndpointGroup
    {
        public override void Map(IEndpointRouteBuilder app)
        {
        }
    }

    /// <summary>README "Validation": a handler that validates on its own, with ValidateAndThrowAsync.</summary>
    public sealed class ReadmeSubscribeCommandHandler : IRequestHandler<ReadmeSubscribeCommand, SingleResponse<string>>
    {
        private static readonly InlineValidator<ReadmeSubscribeCommand> Validator = CreateValidator();

        public async Task<SingleResponse<string>> Handle(ReadmeSubscribeCommand request, CancellationToken cancellationToken)
        {
            await Validator.ValidateAndThrowAsync(request, cancellationToken);
            return new SingleResponse<string>("subscribed");
        }

        private static InlineValidator<ReadmeSubscribeCommand> CreateValidator()
        {
            var validator = new InlineValidator<ReadmeSubscribeCommand>();
            validator.RuleFor(command => command.Email).EmailAddress();
            return validator;
        }
    }

    /// <summary>README "Validation": the validator whose failure becomes the documented 400 body.</summary>
    public sealed class ReadmeCreateStudentCommandValidator : AbstractValidator<ReadmeCreateStudentCommand>
    {
        public ReadmeCreateStudentCommandValidator()
        {
            RuleFor(command => command.Name).NotEmpty().WithMessage("Name is required.");
        }
    }

    /// <summary>README "Validation", visibility row 1: a public validator.</summary>
    public sealed class ReadmePublicVisibilityValidator : AbstractValidator<ReadmeVisibilityCommand>
    {
        public ReadmePublicVisibilityValidator() => RuleFor(command => command.Name).NotEmpty().WithMessage("public");
    }

    /// <summary>Visibility row 2: an internal validator, the case an app that keeps its types internal hits.</summary>
    internal sealed class ReadmeInternalVisibilityValidator : AbstractValidator<ReadmeVisibilityCommand>
    {
        public ReadmeInternalVisibilityValidator() => RuleFor(command => command.Name).NotEmpty().WithMessage("internal");
    }

    /// <summary>
    /// Visibility row 4: a private nested validator. The type cannot be named from outside its declaring type,
    /// so the test asks for it here; closing <c>ReadmeHost</c> is what makes it a constructed, scannable type.
    /// </summary>
    public static Type PrivateVisibilityValidatorType => typeof(ReadmePrivateVisibilityValidator);

    private sealed class ReadmePrivateVisibilityValidator : AbstractValidator<ReadmeVisibilityCommand>
    {
        public ReadmePrivateVisibilityValidator() => RuleFor(command => command.Name).NotEmpty().WithMessage("private nested");
    }

    public sealed class ReadmeFirstDuplicateHandler : IRequestHandler<ReadmeDuplicateHandlerRequest, SingleResponse<string>>
    {
        public Task<SingleResponse<string>> Handle(ReadmeDuplicateHandlerRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new SingleResponse<string>("first"));
    }

    public sealed class ReadmeSecondDuplicateHandler : IRequestHandler<ReadmeDuplicateHandlerRequest, SingleResponse<string>>
    {
        public Task<SingleResponse<string>> Handle(ReadmeDuplicateHandlerRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new SingleResponse<string>("second"));
    }
}

/// <summary>
/// Collects the events a test's own Serilog logger writes, so the properties
/// <c>UsePhoenixRequestLogEnrichment</c> pushed onto the ambient log context can be read back. Emit runs on
/// the request thread while the test thread reads, hence the lock.
/// </summary>
internal sealed class ReadmeCapturingSink : global::Serilog.Core.ILogEventSink
{
    private readonly List<LogEvent> events = [];

    public IReadOnlyList<LogEvent> Events
    {
        get
        {
            lock (events)
                return events.ToArray();
        }
    }

    public void Emit(LogEvent logEvent)
    {
        lock (events)
            events.Add(logEvent);
    }
}

/// <summary>
/// README "Validation", visibility row 3: a <c>file</c>-scoped validator. Unlike the other visibility fixtures
/// this one cannot be nested inside <see cref="ReadmeHost{TMarker}"/> — <c>file</c> types are only legal at
/// namespace scope — so the real assembly's validator scan can see it. That is harmless: it validates only
/// this file's own request type, and it has the public parameterless constructor the container needs.
/// </summary>
file sealed class ReadmeFileScopedVisibilityValidator : AbstractValidator<ReadmeVisibilityCommand>
{
    public ReadmeFileScopedVisibilityValidator() => RuleFor(command => command.Name).NotEmpty().WithMessage("file-scoped");
}

/// <summary>
/// Gives the request a connection address. TestServer leaves <c>Connection.RemoteIpAddress</c> null, and that
/// is precisely what the request-log enrichment reads, so without this every case would look like "no IP".
/// 203.0.113.0/24 is the documentation range, so the value can never be a real address.
/// </summary>
internal sealed class ReadmeClientIpMiddleware(RequestDelegate next)
{
    public const string ClientIp = "203.0.113.7";

    public Task InvokeAsync(HttpContext context)
    {
        context.Connection.RemoteIpAddress = IPAddress.Parse(ClientIp);
        return next(context);
    }
}
