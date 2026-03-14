using System.Net;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Exceptions;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Web;
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

    private static WebApplication CreateApp()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(MediatorRegressionTests).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Development
        });

        builder.Services.AddScoped<ScopedDependencyMarker>();
        builder.Services.AddMediator(typeof(MediatorRegressionTests).Assembly);

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
