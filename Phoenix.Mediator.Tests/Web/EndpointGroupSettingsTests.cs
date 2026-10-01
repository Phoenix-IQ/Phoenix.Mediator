using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Tests.Infrastructure;
using Phoenix.Mediator.Web;
using Xunit;

namespace Phoenix.Mediator.Tests;

/// <summary>
/// <see cref="MapEndpointsOptions.RoutePrefix"/>, <see cref="MapEndpointsOptions.ConfigureEndpoints"/> and
/// <see cref="MapEndpointsOptions.TagEndpointsWithGroupName"/>, which reach every group overriding
/// <see cref="BaseEndpointGroup.Map(IEndpointRouteBuilder)"/>, and the rules for groups still on the
/// <c>WebApplication</c> overload.
/// <para>
/// The groups are nested in <see cref="EgHost{TMarker}"/> so default discovery never finds them, and the apps here use
/// an application name that matches no assembly, so <c>MapEndpoints</c> maps exactly the groups each test passes in —
/// not the older groups elsewhere in this assembly, which deliberately stay on the <c>WebApplication</c> overload.
/// </para>
/// </summary>
public sealed class EndpointGroupSettingsTests
{
    [Fact]
    public async Task MapEndpoints_WithARoutePrefix_MapsEveryGroupUnderIt()
    {
        await using var app = await StartAsync(new MapEndpointsOptions { RoutePrefix = "api" }, typeof(EgHost<object>.StudentsEndpoints), typeof(EgHost<object>.CoursesEndpoints));
        using var client = app.GetTestClient();

        var student = await client.GetAsync("api/students/5");
        var course = await client.GetAsync("api/courses/list");
        var unprefixed = await client.GetAsync("students/5");

        Assert.Equal(HttpStatusCode.OK, student.StatusCode);
        Assert.Equal("student 5", await student.Content.ReadFromJsonAsync<string>());
        Assert.Equal(HttpStatusCode.OK, course.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unprefixed.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1")]
    [InlineData("api/v1/")]
    [InlineData(" /api/v1/ ")]
    public async Task MapEndpoints_RoutePrefix_IgnoresSurroundingSlashesAndWhitespace(string prefix)
    {
        await using var app = await StartAsync(new MapEndpointsOptions { RoutePrefix = prefix }, typeof(EgHost<object>.StudentsEndpoints));
        using var client = app.GetTestClient();

        var response = await client.GetAsync("api/v1/students/5");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The health endpoint is infrastructure, probed by load balancers at a fixed path: the API prefix is not for it.
    [Fact]
    public async Task MapEndpoints_WithARoutePrefix_LeavesTheHealthEndpointWhereItWas()
    {
        await using var app = CreateApp();

        app.MapEndpoints(new MapEndpointsOptions { RoutePrefix = "api" }, Groups(typeof(EgHost<object>.StudentsEndpoints)));

        Assert.Contains(app.RouteEndpoints(), static endpoint => endpoint.RoutePattern.RawText == "/health");
        Assert.DoesNotContain(app.RouteEndpoints(), static endpoint => endpoint.RoutePattern.RawText?.Contains("api/health") == true);
    }

    // The case that motivated it: one RequireAuthorization for every group, instead of one per group that the next
    // group written can forget. An endpoint can still opt out for itself.
    [Fact]
    public async Task MapEndpoints_ConfigureEndpoints_AppliesToEveryGroupsEndpoints_WhichCanStillOptOut()
    {
        await using var app = await StartAsync(
            new MapEndpointsOptions { ConfigureEndpoints = static endpoints => endpoints.RequireAuthorization() },
            static services =>
            {
                services.AddAuthentication().AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, EgNoCredentialsHandler>("Eg", null);
                services.AddAuthorization();
            },
            typeof(EgHost<object>.StudentsEndpoints), typeof(EgHost<object>.CoursesEndpoints));
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("students/5")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("courses/list")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("students/public")).StatusCode);
    }

    // OpenAPI tools group endpoints by tag. The group's name is the natural one, and was already documented as such.
    [Fact]
    public async Task MapEndpoints_TagsEachGroupsEndpointsWithItsGroupName()
    {
        await using var app = CreateApp();

        app.MapEndpoints(Groups(typeof(EgHost<object>.StudentsEndpoints), typeof(EgHost<object>.CoursesEndpoints)));

        Assert.Equal(new[] { "students" }, Tags(app, "students/{id:int}"));
        Assert.Equal(new[] { "courses" }, Tags(app, "courses/list"));
    }

    // Tags add up. Tagging an endpoint that chose its own would list it under both.
    [Fact]
    public async Task MapEndpoints_LeavesTheTagsOfAnEndpointThatSetsItsOwn()
    {
        await using var app = CreateApp();

        app.MapEndpoints(Groups(typeof(EgHost<object>.StudentsEndpoints)));

        Assert.Equal(new[] { "Custom" }, Tags(app, "students/custom-tagged"));
    }

    [Fact]
    public async Task MapEndpoints_TagsFromConfigureEndpoints_WinOverTheGroupName()
    {
        await using var app = CreateApp();

        app.MapEndpoints(
            new MapEndpointsOptions { ConfigureEndpoints = static endpoints => endpoints.WithTags("Api") },
            Groups(typeof(EgHost<object>.StudentsEndpoints)));

        Assert.Equal(new[] { "Api" }, Tags(app, "students/{id:int}"));
    }

    [Fact]
    public async Task MapEndpoints_WithGroupNameTaggingOff_AddsNoTags()
    {
        await using var app = CreateApp();

        app.MapEndpoints(new MapEndpointsOptions { TagEndpointsWithGroupName = false }, Groups(typeof(EgHost<object>.StudentsEndpoints)));

        Assert.Empty(Tags(app, "students/{id:int}"));
    }

    // Groups are handed a route group, not the application: that is what lets the settings reach them.
    [Fact]
    public async Task MapEndpoints_HandsAGroupARouteGroup()
    {
        var recorder = new EgRecorder();
        await using var app = CreateApp(services => services.AddSingleton(recorder));

        app.MapEndpoints(Groups(typeof(EgHost<object>.RecordingEndpoints)));

        Assert.Equal(typeof(RouteGroupBuilder), recorder.BuilderType);
    }

    // The duplicate check sees routes the way the prefix left them.
    [Fact]
    public async Task MapEndpoints_ReportsADuplicateAcrossPrefixedGroups()
    {
        await using var app = CreateApp();

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.MapEndpoints(
            new MapEndpointsOptions { RoutePrefix = "api" },
            Groups(typeof(EgHost<object>.StudentsEndpoints), typeof(EgHost<object>.DuplicateStudentsEndpoints))));

        Assert.Equal("GET /api/students/{id:int}", Assert.Single(exception.Routes));
    }

    // ---------------------------------------------------------------------------------------------
    // Groups on the WebApplication overload.
    // ---------------------------------------------------------------------------------------------

    // Every group written before this release: without shared settings it maps exactly as before, untagged.
    [Fact]
    public async Task MapEndpoints_ALegacyGroupWithoutSharedSettings_IsMappedAsBefore()
    {
        await using var app = await StartAsync(new MapEndpointsOptions(), typeof(EgHost<object>.LegacyEndpoints), typeof(EgHost<object>.CoursesEndpoints));
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("legacy/ping")).StatusCode);
        Assert.Empty(Tags(app, "legacy/ping"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("courses/list")).StatusCode);
    }

    // A group written before the base class had Map(IEndpointRouteBuilder) can have a public method of that exact signature
    // of its own. That method hides the new virtual instead of overriding it, so it must not be taken for the new overload:
    // MapEndpoints would call the base virtual, which cannot map the group, and startup would fail on a group that worked.
    [Fact]
    public async Task MapEndpoints_ALegacyGroupWithItsOwnMapHelper_IsStillMappedThroughTheWebApplicationOverload()
    {
        await using var app = await StartAsync(new MapEndpointsOptions(), typeof(EgHost<object>.LegacyWithHelperEndpoints));
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("legacy-helper/ping")).StatusCode);
    }

    // A group mapped on the application itself cannot receive a prefix or shared conventions. Skipping it silently would
    // leave its endpoints outside a shared RequireAuthorization(), so the combination fails at startup, naming the group.
    [Fact]
    public async Task MapEndpoints_ALegacyGroupWithARoutePrefix_Throws()
    {
        await using var app = CreateApp();

        var exception = Assert.Throws<InvalidOperationException>(() => app.MapEndpoints(
            new MapEndpointsOptions { RoutePrefix = "api" },
            Groups(typeof(EgHost<object>.LegacyEndpoints))));

        Assert.Contains(typeof(EgHost<object>.LegacyEndpoints).FullName!, exception.Message);
        Assert.Contains("Map(IEndpointRouteBuilder)", exception.Message);
    }

    [Fact]
    public async Task MapEndpoints_ALegacyGroupWithConfigureEndpoints_Throws()
    {
        await using var app = CreateApp();

        Assert.Throws<InvalidOperationException>(() => app.MapEndpoints(
            new MapEndpointsOptions { ConfigureEndpoints = static endpoints => endpoints.RequireAuthorization() },
            Groups(typeof(EgHost<object>.LegacyEndpoints))));
    }

    // Map is no longer abstract, so a group that overrides neither overload compiles; it must fail loudly when mapped.
    [Fact]
    public async Task MapEndpoints_AGroupOverridingNeitherMap_ThrowsAClearError()
    {
        await using var app = CreateApp();

        var exception = Assert.Throws<InvalidOperationException>(() => app.MapEndpoints(Groups(typeof(EgHost<object>.EmptyGroupEndpoints))));

        Assert.Contains("must override Map(IEndpointRouteBuilder)", exception.Message);
    }

    // Code that maps a group by hand with the application (tests, a composition helper) keeps working for new groups.
    [Fact]
    public async Task Map_WithTheApplication_OnANewGroup_MapsItOnTheApplication()
    {
        await using var app = CreateApp();

        new EgHost<object>.StudentsEndpoints().Map(app);

        Assert.Contains(app.RouteEndpoints(), static endpoint => endpoint.RoutePattern.RawText == "students/{id:int}");
    }

    [Fact]
    public async Task Map_WithARouteGroup_OnALegacyGroup_ExplainsWhyItCannot()
    {
        await using var app = CreateApp();

        var exception = Assert.Throws<InvalidOperationException>(() => new EgHost<object>.LegacyEndpoints().Map(app.MapGroup("api")));

        Assert.Contains("can only be mapped on the application", exception.Message);
    }

    [Fact]
    public async Task Map_WithTheApplicationAsARouteBuilder_OnALegacyGroup_StillMapsIt()
    {
        await using var app = CreateApp();

        new EgHost<object>.LegacyEndpoints().Map((IEndpointRouteBuilder)app);

        Assert.Contains(app.RouteEndpoints(), static endpoint => endpoint.RoutePattern.RawText == "legacy/ping");
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    private static WebApplication CreateApp(Action<IServiceCollection>? configure = null, bool useTestServer = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // Matches no assembly, so discovery only sees the groups a test passes in.
            ApplicationName = "Phoenix.Mediator.Tests.EndpointGroupSettings",
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });

        if (useTestServer)
            builder.WebHost.UseTestServer();

        builder.Services.AddMediator();
        configure?.Invoke(builder.Services);
        return builder.Build();
    }

    private static Task<WebApplication> StartAsync(MapEndpointsOptions options, params Type[] groups)
        => StartAsync(options, configure: null, groups);

    private static async Task<WebApplication> StartAsync(MapEndpointsOptions options, Action<IServiceCollection>? configure, params Type[] groups)
    {
        var app = CreateApp(configure, useTestServer: true);
        app.MapEndpoints(options, Groups(groups));
        await app.StartAsync();
        return app;
    }

    private static FakeAssembly Groups(params Type[] groups) => new("Phoenix.Mediator.Tests.EndpointGroupSettings.Groups", groups);

    private static string[] Tags(WebApplication app, string routeSuffix)
        => app.Endpoint(routeSuffix).Metadata.OfType<ITagsMetadata>().SelectMany(static metadata => metadata.Tags).ToArray();
}

public sealed class EgRecorder
{
    public Type? BuilderType { get; set; }
}

/// <summary>Authenticates nobody, so an endpoint that requires authorization answers 401.</summary>
internal sealed class EgNoCredentialsHandler(
    Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> options,
    Microsoft.Extensions.Logging.ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder)
    : Microsoft.AspNetCore.Authentication.AuthenticationHandler<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync()
        => Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult());
}

/// <summary>Kept out of default discovery by the open type parameter; tests pass the closed types in.</summary>
public static class EgHost<TMarker>
{
    public sealed class StudentsEndpoints : BaseEndpointGroup
    {
        public override void Map(IEndpointRouteBuilder app)
        {
            var students = app.MapGroup(GroupName);
            students.Get("{id:int}", static (int id) => Results.Ok($"student {id}"));
            students.Get("public", static () => Results.Ok("public")).AllowAnonymous();
            students.Get("custom-tagged", static () => Results.Ok()).WithTags("Custom");
        }
    }

    public sealed class CoursesEndpoints : BaseEndpointGroup
    {
        public override void Map(IEndpointRouteBuilder app)
        {
            app.MapGroup(GroupName).Get("list", static () => Results.Ok("courses"));
        }
    }

    public sealed class DuplicateStudentsEndpoints : BaseEndpointGroup
    {
        public override void Map(IEndpointRouteBuilder app)
        {
            app.MapGroup("students").Get("{id:int}", static (int id) => Results.Ok(id));
        }
    }

    public sealed class RecordingEndpoints(EgRecorder recorder) : BaseEndpointGroup
    {
        public override void Map(IEndpointRouteBuilder app)
        {
            recorder.BuilderType = app.GetType();
        }
    }

    /// <summary>Written against the WebApplication overload, as every group before this release was.</summary>
    public sealed class LegacyEndpoints : BaseEndpointGroup
    {
        public override void Map(WebApplication app)
        {
            app.MapGroup(GroupName).Get("ping", static () => Results.Ok("pong"));
        }
    }

    public sealed class EmptyGroupEndpoints : BaseEndpointGroup;

    /// <summary>
    /// Written before the base class had Map(IEndpointRouteBuilder), with a public helper of that signature. Compiled
    /// against 2.2.0 the helper is a separate method that merely hides the new virtual; <c>new</c> says the same here.
    /// </summary>
    public sealed class LegacyWithHelperEndpoints : BaseEndpointGroup
    {
        public override void Map(WebApplication app) => Map((IEndpointRouteBuilder)app);

        public new void Map(IEndpointRouteBuilder app) => app.MapGroup("legacy-helper").Get("ping", static () => Results.Ok("pong"));
    }
}
