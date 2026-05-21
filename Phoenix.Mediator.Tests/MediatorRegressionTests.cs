using System.Net;
using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Exceptions;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Web;
using Phoenix.Mediator.Web.Middlewares;
using Phoenix.Mediator.Wrappers;
using Xunit;

namespace Phoenix.Mediator.Tests;

public sealed class MediatorRegressionTests
{
    [Fact]
    public void AddMediator_DoesNotDuplicateBuiltInRegistrations()
    {
        var services = new ServiceCollection();

        services.AddMediator(typeof(ValidatedRequest).Assembly);
        services.AddMediator(typeof(ValidatedRequest).Assembly);

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var behaviors = scope.ServiceProvider
            .GetServices<IPipelineBehavior<ValidatedRequest, SingleResponse<string>>>()
            .ToArray();
        var validators = scope.ServiceProvider
            .GetServices<IValidator<ValidatedRequest>>()
            .ToArray();

        Assert.Collection(
            behaviors,
            behavior => Assert.IsType<SentryBehavior<ValidatedRequest, SingleResponse<string>>>(behavior),
            behavior => Assert.IsType<ValidationBehavior<ValidatedRequest, SingleResponse<string>>>(behavior));
        Assert.Single(validators);
    }

    [Fact]
    public async Task MapEndpoints_DiscoversEndpointGroupsFromMediatorAssemblies()
    {
        await using var app = CreateApp();

        app.MapEndpoints();

        var routePatterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static endpoint => endpoint.RoutePattern.RawText)
            .ToArray();

        Assert.Contains(routePatterns, route => route is not null
            && route.EndsWith("discovered/ping", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MapEndpoints_CanConstructEndpointGroupsWithScopedDependencies()
    {
        ScopedDependencyEndpoints.Reset();

        await using var app = CreateApp();

        app.MapEndpoints();

        Assert.True(ScopedDependencyEndpoints.WasConstructed);
    }

    [Fact]
    public void HttpResponseException_UsesErrorsAsMessage()
    {
        var exception = new HttpResponseException(new ErrorResponse(HttpStatusCode.BadRequest, ["first", "second"]));

        Assert.Equal("first; second", exception.Message);
    }

    [Theory]
    [InlineData("ar", "حصل خطأ غير معرف")]
    [InlineData("Arabic", "حصل خطأ غير معرف")]
    [InlineData("en", "Unknown error occurred")]
    [InlineData("English", "Unknown error occurred")]
    public async Task ExceptionHandlingMiddleware_LocalizesUnhandledErrorsFromAcceptLanguage(string acceptLanguage, string expectedMessage)
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["ErrorMessages:Ar"] = "حصل خطأ غير معرف",
            ["ErrorMessages:En"] = "Unknown error occurred"
        });

        var message = await InvokeUnhandledExceptionMiddleware(configuration, acceptLanguage);

        Assert.Equal(expectedMessage, message);
    }

    [Fact]
    public async Task ExceptionHandlingMiddleware_UsesConfiguredDefaultWhenAcceptLanguageIsMissing()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["ErrorMessages:Default"] = "Ar",
            ["ErrorMessages:Ar"] = "حصل خطأ غير معرف",
            ["ErrorMessages:En"] = "Unknown error occurred"
        });

        var message = await InvokeUnhandledExceptionMiddleware(configuration);

        Assert.Equal("حصل خطأ غير معرف", message);
    }

    [Fact]
    public async Task ExceptionHandlingMiddleware_UsesBuiltInDefaultWhenErrorMessagesAreMissing()
    {
        var message = await InvokeUnhandledExceptionMiddleware(CreateConfiguration());

        Assert.Equal("Unknown error occurred", message);
    }

    [Fact]
    public async Task SendAsApiResult_DefaultsEmptyRequestsToNoContent()
    {
        await using var app = CreateApp();
        using var scope = app.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var result = await sender.SendAsApiResult(new EmptyRequest());

        AssertStatusCode(StatusCodes.Status204NoContent, result);
    }

    [Fact]
    public async Task SendAsApiResult_CanReturnOkForEmptyRequests()
    {
        await using var app = CreateApp(options => options.EmptyResponseStatusCode = EmptyResponseStatusCode.Ok);
        using var scope = app.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var result = await sender.SendAsApiResult(new EmptyRequest());

        AssertStatusCode(StatusCodes.Status200OK, result);
    }

    [Fact]
    public async Task EndpointHelpers_InferConfiguredEmptyRequestResponseStatus()
    {
        await using var app = CreateApp(options => options.EmptyResponseStatusCode = EmptyResponseStatusCode.Ok);

        app.MapEndpoints();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(static endpoint => endpoint.RoutePattern.RawText is not null
                && endpoint.RoutePattern.RawText.EndsWith("empty/complete", StringComparison.OrdinalIgnoreCase));

        var responseStatuses = endpoint.Metadata
            .OfType<IProducesResponseTypeMetadata>()
            .Select(static metadata => metadata.StatusCode)
            .ToArray();

        Assert.Contains(StatusCodes.Status200OK, responseStatuses);
        Assert.DoesNotContain(StatusCodes.Status204NoContent, responseStatuses);
    }

    private static void AssertStatusCode(int expectedStatusCode, IResult result)
    {
        var statusCodeResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);

        Assert.Equal(expectedStatusCode, statusCodeResult.StatusCode);
    }

    private static IConfiguration CreateConfiguration(Dictionary<string, string?>? values = null)
    {
        var builder = new ConfigurationBuilder();

        if (values is not null)
            builder.AddInMemoryCollection(values);

        return builder.Build();
    }

    private static async Task<string> InvokeUnhandledExceptionMiddleware(IConfiguration configuration, string? acceptLanguage = null)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        if (!string.IsNullOrWhiteSpace(acceptLanguage))
            context.Request.Headers.AcceptLanguage = acceptLanguage;

        RequestDelegate next = _ => throw new InvalidOperationException("boom");
        var middleware = new ExceptionHandlingMiddleware(
            next,
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            configuration);

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);

        return document.RootElement
            .GetProperty("errors")[0]
            .GetString()!;
    }

    private static WebApplication CreateApp(Action<MediatorOptions>? configureOptions = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(MediatorRegressionTests).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Development
        });

        builder.Services.AddScoped<ScopedDependencyMarker>();
        if (configureOptions is null)
            builder.Services.AddMediator(typeof(MediatorRegressionTests).Assembly);
        else
            builder.Services.AddMediator(configureOptions, typeof(MediatorRegressionTests).Assembly);

        return builder.Build();
    }
}

public sealed class ValidatedRequest : IRequest<SingleResponse<string>>
{
    public string Value { get; set; } = string.Empty;
}

public sealed class ValidatedRequestHandler : IRequestHandler<ValidatedRequest, SingleResponse<string>>
{
    public Task<SingleResponse<string>> Handle(ValidatedRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new SingleResponse<string>(request.Value));
    }
}

public sealed class ValidatedRequestValidator : AbstractValidator<ValidatedRequest>
{
    public ValidatedRequestValidator()
    {
        RuleFor(request => request.Value).NotEmpty();
    }
}

public sealed class DiscoveredEndpoints : BaseEndpointGroup
{
    public override void Map(WebApplication app)
    {
        app.MapGroup(GroupName)
            .Get("ping", () => Results.Ok("pong"));
    }
}

public sealed class EmptyRequest : IRequest
{
}

public sealed class EmptyRequestHandler : IRequestHandler<EmptyRequest>
{
    public Task Handle(EmptyRequest request, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public sealed class EmptyEndpoints : BaseEndpointGroup
{
    public override void Map(WebApplication app)
    {
        app.MapGroup(GroupName)
            .Post("complete", async (ISender sender, EmptyRequest request, CancellationToken ct) =>
                await sender.SendAsApiResult(request, ct));
    }
}

public sealed class ScopedDependencyEndpoints : BaseEndpointGroup
{
    public static bool WasConstructed { get; private set; }

    public ScopedDependencyEndpoints(ScopedDependencyMarker marker)
    {
        WasConstructed = marker.InstanceId != Guid.Empty;
    }

    public static void Reset()
    {
        WasConstructed = false;
    }

    public override void Map(WebApplication app)
    {
        app.MapGroup(GroupName)
            .Get("ready", () => Results.Ok("ready"));
    }
}

public sealed class ScopedDependencyMarker
{
    public Guid InstanceId { get; } = Guid.NewGuid();
}
