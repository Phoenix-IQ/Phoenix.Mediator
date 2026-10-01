using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Validation;
using Phoenix.Mediator.Web;
using Phoenix.Mediator.Web.Middlewares;
using Phoenix.Mediator.Wrappers;
using Phoenix.Mediator.Tests.Infrastructure;
using Xunit;

namespace Phoenix.Mediator.Tests;

/// <summary>
/// Regressions for the issues found in the 2026-09-17 review (see docs/REVIEW-FINDINGS.md).
/// </summary>
public sealed class ReviewFixTests
{
    // Finding 6: validators must be discovered regardless of visibility, like handlers are.
    [Fact]
    public void AddMediatorValidation_RegistersInternalValidators()
    {
        var services = new ServiceCollection();
        var assembly = typeof(ReviewFixTests).Assembly;

        services.AddMediator(assembly).AddMediatorValidation(assembly);

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.Single(scope.ServiceProvider.GetServices<IValidator<InternallyValidatedRequest>>());
    }

    // Finding 6, end to end: an internal validator actually rejects an invalid request.
    [Fact]
    public async Task ValidationBehavior_RunsInternalValidators()
    {
        var services = new ServiceCollection();
        var assembly = typeof(ReviewFixTests).Assembly;
        services.AddMediator(assembly).AddMediatorValidation(assembly);

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await Assert.ThrowsAsync<Exceptions.HttpResponseException>(() =>
            sender.Send<InternallyValidatedRequest, SingleResponse<string>>(new InternallyValidatedRequest()));
    }

    // Finding 8: an open generic handler used to make BuildServiceProvider throw.
    [Fact]
    public void AddMediatorHandlers_SkipsOpenGenericHandlers()
    {
        var services = new ServiceCollection();

        services.AddMediator().AddMediatorHandlers(new FakeAssembly(typeof(OpenGenericHandler<>), typeof(ReviewFixRequestHandler)));

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<IRequestHandler<ReviewFixRequest, SingleResponse<string>>>());
        Assert.DoesNotContain(services, static descriptor => descriptor.ImplementationType == typeof(OpenGenericHandler<>));
    }

    // Finding 10: two handlers for one request used to be resolved by scan order, silently.
    [Fact]
    public void AddMediatorHandlers_ThrowsWhenTwoHandlersHandleTheSameRequest()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddMediatorHandlers(new FakeAssembly(
                typeof(DuplicateHandlers<object>.First),
                typeof(DuplicateHandlers<object>.Second))));

        Assert.Contains("First", exception.Message);
        Assert.Contains("Second", exception.Message);
    }

    // Finding 9: a variable typed as IRequest bound to Send<TRequest> with TRequest = IRequest.
    [Fact]
    public async Task Send_DispatchesByRuntimeType_WhenVariableIsTypedAsIRequest()
    {
        var services = new ServiceCollection();
        services.AddMediator(typeof(ReviewFixTests).Assembly);

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        IRequest command = new ReviewFixVoidRequest();
        var exception = await Record.ExceptionAsync(() => sender.Send(command));

        Assert.Null(exception);
    }

    [Fact]
    public async Task Send_DispatchesByRuntimeType_WhenVariableIsTypedAsIRequestOfResponse()
    {
        var services = new ServiceCollection();
        services.AddMediator(typeof(ReviewFixTests).Assembly);

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        IRequest<SingleResponse<string>> query = new ReviewFixRequest { Value = "hi" };
        var response = await sender.Send<IRequest<SingleResponse<string>>, SingleResponse<string>>(query);

        Assert.Equal("hi", response.Result);
    }

    // Finding 28: a null response used to ignore the configured empty-response status.
    [Theory]
    [InlineData(EmptyResponseStatusCode.Ok, HttpStatusCode.OK)]
    [InlineData(EmptyResponseStatusCode.NoContent, HttpStatusCode.NoContent)]
    public async Task Endpoint_NullResponse_UsesTheConfiguredEmptyStatus(EmptyResponseStatusCode configured, HttpStatusCode expectedStatusCode)
    {
        var builder = TestApps.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMediator(options => options.EmptyResponseStatusCode = configured, typeof(ReviewFixTests).Assembly);
        await using var app = builder.Build();
        app.Get("boxed", static (ISender sender, CancellationToken ct) => sender.Send((object)new NullResponseRequest(), ct));
        app.Get("typed", static (ISender sender, CancellationToken ct) => sender.Send<NullResponseRequest, string?>(new NullResponseRequest(), ct));
        await app.StartAsync();
        using var client = app.GetTestClient();

        Assert.Equal(expectedStatusCode, (await client.GetAsync("boxed")).StatusCode);
        Assert.Equal(expectedStatusCode, (await client.GetAsync("typed")).StatusCode);
    }

    // Finding 20: framework bad requests were reported as 500 with no detail.
    [Fact]
    public async Task ExceptionHandlingMiddleware_KeepsStatusCodeFromBadHttpRequestException()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        RequestDelegate next = _ => throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);
        var middleware = new ExceptionHandlingMiddleware(next, new RecordingLoggerProvider().CreateLogger<ExceptionHandlingMiddleware>(), new ConfigurationBuilder().Build());

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("Request body too large.", document.RootElement.GetProperty("errors")[0].GetString());
        Assert.True(document.RootElement.TryGetProperty("traceId", out _));
    }

    // Finding 22: a cancelled request must not be reported as a server error. A client disconnect ends
    // with 499; a request timeout is rethrown, so UseRequestTimeouts can still write its 504 (covered in
    // ExceptionHandlingMiddlewareTests).
    [Fact]
    public async Task ExceptionHandlingMiddleware_DoesNotReportCancellationAsAServerError_WhenRequestWasAborted()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        var context = new DefaultHttpContext { RequestAborted = aborted.Token };
        context.Response.Body = new MemoryStream();

        var recorder = new RecordingLoggerProvider();
        RequestDelegate next = _ => throw new OperationCanceledException(aborted.Token);
        var middleware = new ExceptionHandlingMiddleware(next, recorder.CreateLogger<ExceptionHandlingMiddleware>(), new ConfigurationBuilder().Build());

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, context.Response.StatusCode);
        Assert.Empty(recorder.Warnings);
    }

    // Finding 18: .NET throws UnauthorizedAccessException for file permission errors too, so a 401
    // that leaves no trace in the log hides server misconfiguration.
    [Fact]
    public async Task ExceptionHandlingMiddleware_LogsUnauthorizedAccessException()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var recorder = new RecordingLoggerProvider();
        RequestDelegate next = _ => throw new UnauthorizedAccessException("Access to the path 'x' is denied.");
        var middleware = new ExceptionHandlingMiddleware(next, recorder.CreateLogger<ExceptionHandlingMiddleware>(), new ConfigurationBuilder().Build());

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Contains(recorder.Warnings, static entry => entry.Exception is UnauthorizedAccessException);
    }

    // Finding 17: role values that are not valid C# identifiers need a mapping.
    [Fact]
    public async Task RequireRole_UsesEnumMemberValueAsRoleClaim()
    {
        await using var app = CreateBareApp();

        app.MapGroup("roles").Get("super", () => "ok").RequireRole(ReviewFixRole.SuperAdmin);

        Assert.Equal("super-admin", SingleRolesMetadata(app, "roles/super"));
    }

    [Fact]
    public async Task RequireRole_JoinsMultipleRolesForOrSemantics()
    {
        await using var app = CreateBareApp();

        app.MapGroup("roles").Get("either", () => "ok").RequireRole(ReviewFixRole.Admin, ReviewFixRole.Manager);

        Assert.Equal("Admin,Manager", SingleRolesMetadata(app, "roles/either"));
    }

    // Finding 19: a [Flags] combination silently meant "either role"; an undefined value required
    // a role named after its number.
    [Fact]
    public async Task RequireRole_RejectsFlagsCombinationsAndUndefinedValues()
    {
        await using var app = CreateBareApp();
        var group = app.MapGroup("roles");

        Assert.Throws<ArgumentException>(() =>
            group.Get("flags", () => "ok").RequireRole(ReviewFixFlagsRole.Admin | ReviewFixFlagsRole.Manager));
        Assert.Throws<ArgumentException>(() =>
            group.Get("undefined", () => "ok").RequireRole((ReviewFixRole)42));
    }

    // Finding 26: the response wrapper could be serialized but not read back.
    [Fact]
    public void MultiResponse_RoundTripsThroughSystemTextJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(new MultiResponse<int>([1, 2, 3], totalCount: 7, pageSize: 2), options);

        var roundTripped = JsonSerializer.Deserialize<MultiResponse<int>>(json, options)!;

        Assert.Equal([1, 2, 3], roundTripped.Data);
        Assert.Equal(7, roundTripped.TotalCount);
        Assert.Equal(2, roundTripped.PageSize);
        Assert.Equal(4, roundTripped.PagesCount);
    }

    // Finding 27: ToLower() turns InvoiceEndpoints into "ınvoice" on Turkish/Azerbaijani servers.
    [Theory]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    [InlineData("az-Latn-AZ")]
    public void GroupName_IsCultureInvariant(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Assert.Equal("invoice", new InvoiceEndpoints().GroupName);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static WebApplication CreateBareApp()
    {
        var builder = TestApps.CreateBuilder();

        builder.Services.AddMediator();
        builder.Services.AddAuthorization();

        return builder.Build();
    }

    private static string? SingleRolesMetadata(WebApplication app, string route)
    {
        return Assert.Single(app.Endpoint(route).Metadata.GetOrderedMetadata<IAuthorizeData>()).Roles;
    }

}

public sealed class ReviewFixRequest : IRequest<SingleResponse<string>>
{
    public string Value { get; set; } = string.Empty;
}

public sealed class ReviewFixRequestHandler : IRequestHandler<ReviewFixRequest, SingleResponse<string>>
{
    public Task<SingleResponse<string>> Handle(ReviewFixRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new SingleResponse<string>(request.Value));
}

public sealed class ReviewFixVoidRequest : IRequest { }

public sealed class ReviewFixVoidRequestHandler : IRequestHandler<ReviewFixVoidRequest>
{
    public Task Handle(ReviewFixVoidRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class NullResponseRequest : IRequest<string?> { }

public sealed class NullResponseRequestHandler : IRequestHandler<NullResponseRequest, string?>
{
    public Task<string?> Handle(NullResponseRequest request, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

public sealed class InternallyValidatedRequest : IRequest<SingleResponse<string>>
{
    public string Name { get; set; } = string.Empty;
}

public sealed class InternallyValidatedRequestHandler : IRequestHandler<InternallyValidatedRequest, SingleResponse<string>>
{
    public Task<SingleResponse<string>> Handle(InternallyValidatedRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new SingleResponse<string>(request.Name));
}

// Internal on purpose: FluentValidation's assembly scan skips non-public types unless asked not to.
internal sealed class InternallyValidatedRequestValidator : AbstractValidator<InternallyValidatedRequest>
{
    public InternallyValidatedRequestValidator() => RuleFor(request => request.Name).NotEmpty();
}

public sealed record OpenGenericRequest<T>(int Id) : IRequest<T>;

// Lives in the test assembly on purpose: every test that scans it would fail if open generic
// handlers were registered again.
public sealed class OpenGenericHandler<T> : IRequestHandler<OpenGenericRequest<T>, T>
{
    public Task<T> Handle(OpenGenericRequest<T> request, CancellationToken cancellationToken) => Task.FromResult(default(T)!);
}

// Same for endpoint groups: MapEndpoints must skip this instead of failing at startup.
public class GenericEndpointGroup<TEntity> : BaseEndpointGroup
{
    public override void Map(IEndpointRouteBuilder app) { }
}

public sealed class InvoiceEndpoints : BaseEndpointGroup
{
    public override void Map(IEndpointRouteBuilder app) { }
}

public sealed class DuplicateHandlerRequest : IRequest<SingleResponse<string>> { }

/// <summary>
/// Nested in an open generic type so the assembly scan skips these two (they carry its type parameter),
/// while the test can still hand the closed versions to a scan of its own. Otherwise every test that
/// scans this assembly would hit the duplicate-handler guard they exist to exercise.
/// </summary>
public static class DuplicateHandlers<TMarker>
{
    public sealed class First : IRequestHandler<DuplicateHandlerRequest, SingleResponse<string>>
    {
        public Task<SingleResponse<string>> Handle(DuplicateHandlerRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new SingleResponse<string>("first"));
    }

    public sealed class Second : IRequestHandler<DuplicateHandlerRequest, SingleResponse<string>>
    {
        public Task<SingleResponse<string>> Handle(DuplicateHandlerRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new SingleResponse<string>("second"));
    }
}

public enum ReviewFixRole
{
    Admin,
    Manager,

    [System.Runtime.Serialization.EnumMember(Value = "super-admin")]
    SuperAdmin,
}

[Flags]
public enum ReviewFixFlagsRole
{
    None = 0,
    Admin = 1,
    Manager = 2,
}
