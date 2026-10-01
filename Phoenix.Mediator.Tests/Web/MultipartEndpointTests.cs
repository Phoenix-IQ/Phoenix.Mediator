using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Tests.Infrastructure;
using Phoenix.Mediator.Web;
using Phoenix.Mediator.Wrappers;
using System.Net;
using Xunit;

namespace Phoenix.Mediator.Tests;

// ---------------------------------------------------------------------------------------------
// Support types. Everything here is prefixed "Multipart" because this assembly is scanned by other
// tests. None of them is a handler, an endpoint group or a validator, so AddMediator,
// AddMediatorValidation and MapEndpoints in the sibling test files ignore all of them.
// ---------------------------------------------------------------------------------------------

/// <summary>The response shape used to check that 200 is inferred with the right schema.</summary>
public sealed record MultipartUploadResult(string FileName);

/// <summary>A mediator request carrying a response, declared as a record (the documented shape).</summary>
public sealed record MultipartUploadCommand(string Name) : IRequest<MultipartUploadResult>;

/// <summary>A mediator request with no response, declared as a class rather than a record.</summary>
public sealed class MultipartEmptyCommand : IRequest
{
    public string? Name { get; set; }
}

/// <summary>A payload that is deliberately NOT a mediator request, so nothing can be inferred from it.</summary>
public sealed record MultipartPlainPayload(string Name);

/// <summary>
/// A third-party form-binding attribute. The detection is written against the framework interface, not
/// against the concrete MVC attribute, so anything implementing it has to be recognised too.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class MultipartCustomFromFormAttribute : Attribute, IFromFormMetadata
{
    public string? Name => null;
}

/// <summary>An [AsParameters] bag that hides a form file one level below the delegate signature.</summary>
public sealed class MultipartParametersBag
{
    public IFormFile? File { get; set; }
}

/// <summary>
/// An <see cref="IAntiforgery"/> that only has to exist. Every member throws, so a test fails loudly if
/// the library ever resolves and uses it while merely probing whether antiforgery is registered.
/// </summary>
internal sealed class MultipartStubAntiforgery : IAntiforgery
{
    public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => throw new NotSupportedException();

    public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => throw new NotSupportedException();

    public Task<bool> IsRequestValidAsync(HttpContext httpContext) => throw new NotSupportedException();

    public Task ValidateRequestAsync(HttpContext httpContext) => throw new NotSupportedException();

    public void SetCookieTokenAndHeader(HttpContext httpContext) => throw new NotSupportedException();
}

/// <summary>
/// A service provider that pretends a given service was never registered. Used to hide
/// <see cref="IServiceProviderIsService"/>, which containers other than the built-in one need not
/// provide, so the fallback branch of the antiforgery probe can be exercised.
/// </summary>
internal sealed class MultipartFilteredServiceProvider(IServiceProvider inner, params Type[] hiddenServices) : IServiceProvider
{
    public object? GetService(Type serviceType)
        => Array.IndexOf(hiddenServices, serviceType) >= 0 ? null : inner.GetService(serviceType);
}

/// <summary>
/// A route builder that maps into a real application but hands the library a different
/// <see cref="IServiceProvider"/>, so a test can control exactly what the library is able to see.
/// </summary>
internal sealed class MultipartRouteBuilder(IEndpointRouteBuilder inner, IServiceProvider serviceProvider) : IEndpointRouteBuilder
{
    public IServiceProvider ServiceProvider { get; } = serviceProvider;

    public ICollection<EndpointDataSource> DataSources => inner.DataSources;

    public IApplicationBuilder CreateApplicationBuilder() => inner.CreateApplicationBuilder();
}

/// <summary>
/// PostMultiPart / PutMultiPart / PatchMultiPart and everything ConfigureMultiPart attaches: the HTTP
/// method, the multipart Accepts metadata, the two body-size limits that have to agree, the request
/// timeout, the antiforgery opt-out, the startup warning about missing antiforgery services, and the
/// OpenAPI responses these routes inherit from the shared AddResponses helper.
/// </summary>
public sealed class MultipartEndpointTests
{
    private const long DocumentedDefaultBodySize = 5_000_000;
    private const int DocumentedDefaultTimeoutSeconds = 120;

    // ---------------------------------------------------------------------------------------
    // HTTP method and route pattern
    // ---------------------------------------------------------------------------------------

    // A multipart helper that answered more than its own verb would silently shadow a sibling route
    // mapped at the same pattern for GET or DELETE, and duplicate detection would not see it coming.
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task MultiPartHelpers_MapOnlyTheirOwnHttpMethod(string httpMethod)
    {
        await using var app = CreateApp();
        var pattern = $"multipart-method-{Slug(httpMethod)}/files";

        MapMultiPart(app, httpMethod, pattern, () => Results.Ok());

        var methods = app.Endpoint(pattern).Metadata.GetMetadata<IHttpMethodMetadata>();

        Assert.NotNull(methods);
        Assert.Equal(new[] { httpMethod }, methods!.HttpMethods);
    }

    // Uploads normally hang off a parent resource, so the pattern - route parameters and constraints
    // included - has to reach MapPost/MapPut/MapPatch untouched.
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task MultiPartHelpers_KeepRouteParametersInThePattern(string httpMethod)
    {
        await using var app = CreateApp();
        var pattern = $"multipart-route-{Slug(httpMethod)}/{{tenantId}}/documents/{{documentId:int}}";

        MapMultiPart(app, httpMethod, pattern, (string tenantId, int documentId) => Results.Ok(tenantId));

        var parameters = app.Endpoint(pattern).RoutePattern.Parameters.Select(static part => part.Name).ToArray();

        Assert.Equal(new[] { "tenantId", "documentId" }, parameters);
    }

    // Mapped inside a group the route has to inherit the group prefix: the helpers take the pattern
    // from the group builder they were called on, not from the application.
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task MultiPartHelpers_MapUnderTheRouteGroupPrefix(string httpMethod)
    {
        await using var app = CreateApp();
        var prefix = $"multipart-group-{Slug(httpMethod)}";

        MapMultiPart(app.MapGroup(prefix), httpMethod, "documents", () => Results.Ok());

        var endpoint = app.Endpoint($"{prefix}/documents");

        Assert.Equal(new[] { httpMethod }, endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
        Assert.Contains(endpoint.Metadata.OfType<IAcceptsMetadata>(), static metadata => metadata.ContentTypes.Contains("multipart/form-data"));
    }

    // A null delegate has to fail at the mapping call. Reflecting over it later (BindsFormData reads
    // handler.Method) would turn a typo into a NullReferenceException with no route in the message.
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task MultiPartHelpers_NullHandler_ThrowArgumentNullException(string httpMethod)
    {
        await using var app = CreateApp();

        Assert.Throws<ArgumentNullException>(() =>
        {
            MapMultiPart(app, httpMethod, $"multipart-null-handler-{Slug(httpMethod)}/files", null!);
        });
    }

    // ---------------------------------------------------------------------------------------
    // Accepts metadata
    // ---------------------------------------------------------------------------------------

    // Without this, generated clients and Swagger send application/json to an endpoint that can only
    // read a multipart body, and the mismatch only shows up at runtime as a 415 or an empty form.
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task MultiPartHelpers_DeclareMultipartFormDataAsTheAcceptedContentType(string httpMethod)
    {
        await using var app = CreateApp();
        var pattern = $"multipart-accepts-{Slug(httpMethod)}/files";

        MapMultiPart(app, httpMethod, pattern, () => Results.Ok());

        var accepts = Assert.Single(app.Endpoint(pattern).Metadata.OfType<IAcceptsMetadata>());

        Assert.Contains("multipart/form-data", accepts.ContentTypes);
        Assert.Equal(typeof(IFormFileCollection), accepts.RequestType);
    }

    // The body of an upload endpoint is not optional; declaring it optional tells clients they may call
    // it with nothing at all.
    [Fact]
    public async Task PostMultiPart_DeclaresTheMultipartBodyAsRequired()
    {
        await using var app = CreateApp();

        app.PostMultiPart("multipart-accepts-required/files", () => Results.Ok());

        var accepts = Assert.Single(app.Endpoint("multipart-accepts-required/files").Metadata.OfType<IAcceptsMetadata>());

        Assert.False(accepts.IsOptional);
    }

    // Baseline for every "the multipart helper attaches X" assertion in this file: the plain Post helper
    // shares AddResponses but none of the multipart configuration, so those assertions really are
    // observing ConfigureMultiPart and not something the framework does for every route.
    [Fact]
    public async Task Post_DoesNotAttachAnyOfTheMultipartConfiguration()
    {
        await using var app = CreateApp();

        app.Post("multipart-baseline/files", () => Results.Ok());

        var endpoint = app.Endpoint("multipart-baseline/files");

        Assert.Empty(endpoint.Metadata.OfType<IAcceptsMetadata>());
        Assert.Empty(endpoint.Metadata.OfType<IRequestSizeLimitMetadata>());
        Assert.Empty(endpoint.Metadata.OfType<IFormOptionsMetadata>());
        Assert.Empty(endpoint.Metadata.OfType<RequestTimeoutPolicy>());
    }

    // ---------------------------------------------------------------------------------------
    // Body size limits
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task MultiPartHelpers_DefaultTheRequestSizeLimitToFiveMillionBytes(string httpMethod)
    {
        await using var app = CreateApp();
        var pattern = $"multipart-size-default-{Slug(httpMethod)}/files";

        MapMultiPart(app, httpMethod, pattern, () => Results.Ok());

        Assert.Equal(DocumentedDefaultBodySize, (long?)RequestSizeLimit(app, pattern).MaxRequestBodySize);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task MultiPartHelpers_DefaultTheMultipartFormLengthLimitToFiveMillionBytes(string httpMethod)
    {
        await using var app = CreateApp();
        var pattern = $"multipart-form-default-{Slug(httpMethod)}/files";

        MapMultiPart(app, httpMethod, pattern, () => Results.Ok());

        Assert.Equal(DocumentedDefaultBodySize, (long?)FormOptions(app, pattern).MultipartBodyLengthLimit);
    }

    // The contract spelled out in the source: the request-size limit alone is not enough, because form
    // parsing has its own 128 MB ceiling. Setting only one of the two fails silently - the server accepts
    // the bytes and the form reader then rejects them. Both numbers must be present and must agree.
    [Theory]
    [InlineData("POST", 1L)]
    [InlineData("POST", 5_000_000L)]
    [InlineData("POST", 200_000_000L)]
    [InlineData("PUT", 64L)]
    [InlineData("PUT", 134_217_728L)]
    [InlineData("PUT", 200_000_000L)]
    [InlineData("PATCH", 1_048_576L)]
    [InlineData("PATCH", 200_000_000L)]
    public async Task MultiPartHelpers_ApplyTheConfiguredSizeToBothTheRequestLimitAndTheFormLimit(string httpMethod, long maxRequestBodySize)
    {
        await using var app = CreateApp();
        var pattern = $"multipart-size-{Slug(httpMethod)}-{maxRequestBodySize}/files";

        MapMultiPartWithSizeLimit(app, httpMethod, pattern, () => Results.Ok(), maxRequestBodySize);

        // Both numbers are compared against the argument rather than against each other: asserting that
        // the two agree says nothing once each has been checked against the value the caller passed.
        Assert.Equal(maxRequestBodySize, (long?)RequestSizeLimit(app, pattern).MaxRequestBodySize);
        Assert.Equal(maxRequestBodySize, (long?)FormOptions(app, pattern).MultipartBodyLengthLimit);
    }

    // 0 is a real value - "accept no body at all" - and has to survive as one. A rewrite that treated the
    // parameter as "unset, use the default" would silently give the route a 5 MB ceiling instead, and no
    // metadata test that only ever passes positive numbers would notice.
    [Fact]
    public async Task PostMultiPart_ZeroMaxRequestBodySize_IsCarriedThroughRatherThanTreatedAsUnset()
    {
        await using var app = CreateApp();

        app.PostMultiPart("multipart-size-zero/files", () => Results.Ok(), maxRequestBodySize: 0);

        Assert.Equal(0L, (long?)RequestSizeLimit(app, "multipart-size-zero/files").MaxRequestBodySize);
        Assert.Equal(0L, (long?)FormOptions(app, "multipart-size-zero/files").MultipartBodyLengthLimit);
    }

    // Raising the ceiling must not quietly retune anything else about form parsing: changing the key or
    // value limits, or turning on body buffering, changes how every form post to that route behaves.
    [Fact]
    public async Task PostMultiPart_LeavesEveryOtherFormOptionAtTheFrameworkDefault()
    {
        await using var app = CreateApp();

        app.PostMultiPart("multipart-form-untouched/files", () => Results.Ok(), maxRequestBodySize: 7_000_000);

        var formOptions = FormOptions(app, "multipart-form-untouched/files");

        Assert.Equal(7_000_000L, (long?)formOptions.MultipartBodyLengthLimit);
        Assert.Null(formOptions.BufferBody);
        Assert.Null(formOptions.MemoryBufferThreshold);
        Assert.Null(formOptions.BufferBodyLengthLimit);
        Assert.Null(formOptions.ValueCountLimit);
        Assert.Null(formOptions.KeyLengthLimit);
        Assert.Null(formOptions.ValueLengthLimit);
        Assert.Null(formOptions.MultipartBoundaryLengthLimit);
        Assert.Null(formOptions.MultipartHeadersCountLimit);
        Assert.Null(formOptions.MultipartHeadersLengthLimit);
    }

    // Several upload routes in one application must each keep their own numbers. Anything shared or
    // static between calls would give whichever route was mapped last the final say for all of them.
    [Fact]
    public async Task MultiPartHelpers_KeepPerRouteLimits_WhenSeveralAreMappedOnOneApp()
    {
        await using var app = CreateApp();

        app.PostMultiPart("multipart-independent/small", () => Results.Ok(), maxRequestBodySize: 1_000, timeoutSeconds: 5);
        app.PutMultiPart("multipart-independent/medium", () => Results.Ok(), maxRequestBodySize: 2_000, timeoutSeconds: 10);
        app.PatchMultiPart("multipart-independent/large", () => Results.Ok(), maxRequestBodySize: 3_000, timeoutSeconds: 15);

        Assert.Equal(1_000L, (long?)RequestSizeLimit(app, "multipart-independent/small").MaxRequestBodySize);
        Assert.Equal(2_000L, (long?)RequestSizeLimit(app, "multipart-independent/medium").MaxRequestBodySize);
        Assert.Equal(3_000L, (long?)RequestSizeLimit(app, "multipart-independent/large").MaxRequestBodySize);

        Assert.Equal(1_000L, (long?)FormOptions(app, "multipart-independent/small").MultipartBodyLengthLimit);
        Assert.Equal(2_000L, (long?)FormOptions(app, "multipart-independent/medium").MultipartBodyLengthLimit);
        Assert.Equal(3_000L, (long?)FormOptions(app, "multipart-independent/large").MultipartBodyLengthLimit);

        Assert.Equal(TimeSpan.FromSeconds(5), RequestTimeout(app, "multipart-independent/small").Timeout);
        Assert.Equal(TimeSpan.FromSeconds(10), RequestTimeout(app, "multipart-independent/medium").Timeout);
        Assert.Equal(TimeSpan.FromSeconds(15), RequestTimeout(app, "multipart-independent/large").Timeout);
    }

    // ---------------------------------------------------------------------------------------
    // Request timeout
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task MultiPartHelpers_DefaultTheRequestTimeoutToTwoMinutes(string httpMethod)
    {
        await using var app = CreateApp();
        var pattern = $"multipart-timeout-default-{Slug(httpMethod)}/files";

        MapMultiPart(app, httpMethod, pattern, () => Results.Ok());

        Assert.Equal(TimeSpan.FromSeconds(DocumentedDefaultTimeoutSeconds), RequestTimeout(app, pattern).Timeout);
    }

    [Theory]
    [InlineData("POST", 1)]
    [InlineData("POST", 600)]
    [InlineData("PUT", 30)]
    [InlineData("PUT", 3_600)]
    [InlineData("PATCH", 45)]
    public async Task MultiPartHelpers_CarryTheConfiguredTimeoutAsATimeSpan(string httpMethod, int timeoutSeconds)
    {
        await using var app = CreateApp();
        var pattern = $"multipart-timeout-{Slug(httpMethod)}-{timeoutSeconds}/files";

        MapMultiPartWithTimeout(app, httpMethod, pattern, () => Results.Ok(), timeoutSeconds);

        Assert.Equal(TimeSpan.FromSeconds(timeoutSeconds), RequestTimeout(app, pattern).Timeout);
    }

    // The helper configures a duration, not a named policy. Naming a policy instead would drop the
    // duration and hand control to whatever the application registered under that name.
    [Fact]
    public async Task PostMultiPart_ConfiguresADurationRatherThanANamedTimeoutPolicy()
    {
        await using var app = CreateApp();

        app.PostMultiPart("multipart-timeout-policy/files", () => Results.Ok(), timeoutSeconds: 90);

        var timeout = RequestTimeout(app, "multipart-timeout-policy/files");

        Assert.Equal(TimeSpan.FromSeconds(90), timeout.Timeout);
        // The named-policy overload of WithRequestTimeout records a RequestTimeoutAttribute instead.
        // Its absence is what makes this a duration the helper owns rather than a policy the app must
        // have registered by name under AddRequestTimeouts.
        Assert.Empty(app.Endpoint("multipart-timeout-policy/files").Metadata.OfType<RequestTimeoutAttribute>());
    }

    // ---------------------------------------------------------------------------------------
    // Antiforgery metadata
    // ---------------------------------------------------------------------------------------

    // Asserted through GetMetadata rather than by searching the whole collection, because GetMetadata is
    // what the antiforgery middleware calls and it takes the LAST entry: an opt-out that is present but
    // overridden by a later "validation required" entry would satisfy a Contains check while the route
    // still demanded a token.
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task MultiPartHelpers_DisableAntiforgeryTrue_AttachMetadataThatSkipsValidation(string httpMethod)
    {
        await using var app = CreateApp();
        var pattern = $"multipart-antiforgery-off-{Slug(httpMethod)}/files";

        MapMultiPartWithAntiforgery(app, httpMethod, pattern, () => Results.Ok(), disableAntiforgery: true);

        var effective = app.Endpoint(pattern).Metadata.GetMetadata<IAntiforgeryMetadata>();

        Assert.NotNull(effective);
        Assert.False(effective!.RequiresValidation);
    }

    // Opting out has to stay explicit: a cookie-authenticated upload route that quietly skipped antiforgery
    // validation would be a CSRF hole introduced by a default. The delegate here really does bind a file, so
    // this is the shape that is actually at risk, and the assertion is again about what the middleware would
    // read - nothing the helper attaches may leave the route opted out.
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task MultiPartHelpers_DefaultToAntiforgeryValidationEnabled(string httpMethod)
    {
        await using var app = CreateApp(static services => services.AddAntiforgery());
        var pattern = $"multipart-antiforgery-default-{Slug(httpMethod)}/files";

        MapMultiPart(app, httpMethod, pattern, (IFormFile file) => Results.Ok());

        Assert.DoesNotContain(
            app.Endpoint(pattern).Metadata.OfType<IAntiforgeryMetadata>(),
            static entry => !entry.RequiresValidation);
    }

    // ---------------------------------------------------------------------------------------
    // The startup warning: WarnIfAntiforgeryIsUnavailable
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task MultiPartHelpers_WarnOnceWhenAFormBindingDelegateHasNoAntiforgeryServices(string httpMethod)
    {
        var pattern = $"multipart-warn-{Slug(httpMethod)}/files";

        var warnings = await CaptureWarnings(httpMethod, pattern, (IFormFile file) => Results.Ok());

        var warning = Assert.Single(warnings);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(pattern, warning.Message, StringComparison.Ordinal);
    }

    // The warning is only useful if it says what to do about it: the runtime failure it predicts is an
    // opaque 500, so both documented ways out have to be in the text.
    [Fact]
    public async Task PostMultiPart_AntiforgeryWarning_NamesBothWaysOut()
    {
        var warnings = await CaptureWarnings("POST", "multipart-warn-text/files", (IFormFile file) => Results.Ok());

        var warning = Assert.Single(warnings);

        Assert.Contains("services.AddAntiforgery()", warning.Message, StringComparison.Ordinal);
        Assert.Contains("app.UseAntiforgery()", warning.Message, StringComparison.Ordinal);
        Assert.Contains("disableAntiforgery: true", warning.Message, StringComparison.Ordinal);
        Assert.Contains("500", warning.Message, StringComparison.Ordinal);
        Assert.Null(warning.Exception);
    }

    // Logged under the library's own category so an application can filter or escalate it. This is the
    // one place the category is asserted directly instead of being used as a filter.
    [Fact]
    public async Task PostMultiPart_AntiforgeryWarning_IsLoggedUnderTheEndpointsExtensionsCategory()
    {
        var logs = new RecordingLoggerProvider();
        await using var app = TestApps.CreateBuilder(loggerProvider: logs).Build();

        app.PostMultiPart("multipart-warn-category/files", (IFormFile file) => Results.Ok());

        var warning = Assert.Single(logs.Warnings, static entry => entry.Message.Contains("antiforgery", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("Phoenix.Mediator.Web.EndpointsExtensions", warning.Category);
    }

    // One route's problem must not mask another's, and routes that are fine must not be named.
    [Fact]
    public async Task MultiPartHelpers_WarnOncePerAffectedRoute()
    {
        var logs = new RecordingLoggerProvider();
        await using var app = TestApps.CreateBuilder(loggerProvider: logs).Build();

        app.PostMultiPart("multipart-warn-many/alpha", (IFormFile file) => Results.Ok());
        app.PutMultiPart("multipart-warn-many/beta", (IFormCollection form) => Results.Ok());
        app.PatchMultiPart("multipart-warn-many/gamma", (IFormFileCollection files) => Results.Ok());
        app.PostMultiPart("multipart-warn-many/delta", () => Results.Ok());

        var warnings = EndpointWarnings(logs);

        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, static entry => entry.Message.Contains("multipart-warn-many/alpha", StringComparison.Ordinal));
        Assert.Contains(warnings, static entry => entry.Message.Contains("multipart-warn-many/beta", StringComparison.Ordinal));
        Assert.Contains(warnings, static entry => entry.Message.Contains("multipart-warn-many/gamma", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, static entry => entry.Message.Contains("multipart-warn-many/delta", StringComparison.Ordinal));
    }

    // Inside a group the warning quotes the pattern exactly as written at the call site, not the full
    // prefixed route. Worth pinning: it is what a developer greps for, and it is not the URL a client
    // calls, so anyone matching the two up needs to know which one they are reading.
    [Fact]
    public async Task PostMultiPart_InsideAGroup_WarnsWithThePatternAsWrittenAtTheCallSite()
    {
        var logs = new RecordingLoggerProvider();
        await using var app = TestApps.CreateBuilder(loggerProvider: logs).Build();

        app.MapGroup("multipart-grouped-warning").PostMultiPart("documents", (IFormFile file) => Results.Ok());

        var warning = Assert.Single(EndpointWarnings(logs));

        Assert.Contains("'documents'", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("multipart-grouped-warning", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostMultiPart_AntiforgeryServicesRegistered_DoesNotWarn()
    {
        var warnings = await CaptureWarnings(
            "POST",
            "multipart-warn-registered/files",
            (IFormFile file) => Results.Ok(),
            registerAntiforgery: true);

        Assert.Empty(warnings);
    }

    // Opting out is the documented answer for bearer-token APIs, so it has to silence the warning rather
    // than leave a permanent false alarm in the startup log.
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task MultiPartHelpers_DisableAntiforgeryTrue_DoNotWarn(string httpMethod)
    {
        var warnings = await CaptureWarnings(
            httpMethod,
            $"multipart-warn-disabled-{Slug(httpMethod)}/files",
            (IFormFile file) => Results.Ok(),
            disableAntiforgery: true);

        Assert.Empty(warnings);
    }

    // ---------------------------------------------------------------------------------------
    // Form-data detection: BindsFormData, one shape at a time
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task PostMultiPart_IFormFileParameter_Warns()
    {
        Assert.Single(await CaptureWarnings("POST", "multipart-binds/form-file", (IFormFile file) => Results.Ok()));
    }

    // Assignability, not equality: a delegate declaring the concrete FormFile binds form data just the
    // same, and the antiforgery requirement applies to it identically.
    [Fact]
    public async Task PostMultiPart_ConcreteFormFileParameter_Warns()
    {
        Assert.Single(await CaptureWarnings("POST", "multipart-binds/concrete-form-file", (FormFile file) => Results.Ok()));
    }

    [Fact]
    public async Task PostMultiPart_IFormFileCollectionParameter_Warns()
    {
        Assert.Single(await CaptureWarnings("POST", "multipart-binds/form-file-collection", (IFormFileCollection files) => Results.Ok()));
    }

    [Fact]
    public async Task PostMultiPart_IFormCollectionParameter_Warns()
    {
        Assert.Single(await CaptureWarnings("POST", "multipart-binds/form-collection", (IFormCollection form) => Results.Ok()));
    }

    [Fact]
    public async Task PostMultiPart_FromFormParameter_Warns()
    {
        Assert.Single(await CaptureWarnings(
            "POST",
            "multipart-binds/from-form",
            ([Microsoft.AspNetCore.Mvc.FromForm] string name) => Results.Ok(name)));
    }

    // The detection is written against the framework interface, so a form-binding attribute that did not
    // come from Microsoft.AspNetCore.Mvc still counts.
    [Fact]
    public async Task PostMultiPart_CustomFromFormMetadataAttribute_Warns()
    {
        Assert.Single(await CaptureWarnings(
            "POST",
            "multipart-binds/custom-from-form",
            ([MultipartCustomFromForm] string name) => Results.Ok(name)));
    }

    // One form parameter among several non-form ones is still form binding.
    [Fact]
    public async Task PostMultiPart_FormParameterAlongsideOthers_Warns()
    {
        Assert.Single(await CaptureWarnings(
            "POST",
            "multipart-binds/mixed",
            (string tenantId, IFormFile file, CancellationToken cancellationToken) => Results.Ok(tenantId)));
    }

    // Called out explicitly in the source: an endpoint that reads HttpRequest.Form itself is never
    // validated whatever disableAntiforgery says, because validation is enforced by parameter binding.
    // Warning about it would send people chasing a problem the flag cannot fix.
    [Fact]
    public async Task PostMultiPart_HttpRequestParameter_DoesNotWarn()
    {
        Assert.Empty(await CaptureWarnings("POST", "multipart-binds/http-request", (HttpRequest request) => Results.Ok()));
    }

    [Fact]
    public async Task PostMultiPart_HttpContextParameter_DoesNotWarn()
    {
        Assert.Empty(await CaptureWarnings("POST", "multipart-binds/http-context", (HttpContext context) => Results.Ok()));
    }

    [Fact]
    public async Task PostMultiPart_NoParameters_DoesNotWarn()
    {
        Assert.Empty(await CaptureWarnings("POST", "multipart-binds/none", () => Results.Ok()));
    }

    [Fact]
    public async Task PostMultiPart_JsonBodyParameter_DoesNotWarn()
    {
        Assert.Empty(await CaptureWarnings("POST", "multipart-binds/json-body", (MultipartPlainPayload payload) => Results.Ok(payload)));
    }

    // A binding attribute that is not a form attribute must not be mistaken for one.
    [Fact]
    public async Task PostMultiPart_FromQueryParameter_DoesNotWarn()
    {
        Assert.Empty(await CaptureWarnings(
            "POST",
            "multipart-binds/from-query",
            ([Microsoft.AspNetCore.Mvc.FromQuery] string q) => Results.Ok(q)));
    }

    // Known blind spot, pinned so that it stays a deliberate choice: BindsFormData only inspects the
    // delegate's own parameters, so a form file reached through [AsParameters] really does bind form data
    // at runtime and still produces no warning. If this ever starts warning that is an improvement, and
    // this test should be updated rather than the behaviour reverted.
    [Fact]
    public async Task PostMultiPart_AsParametersFormFile_DoesNotWarnBecauseOnlyTopLevelParametersAreInspected()
    {
        Assert.Empty(await CaptureWarnings(
            "POST",
            "multipart-binds/as-parameters",
            ([AsParameters] MultipartParametersBag bag) => Results.Ok()));
    }

    // ---------------------------------------------------------------------------------------
    // IsAntiforgeryRegistered
    // ---------------------------------------------------------------------------------------

    // The probe asks IServiceProviderIsService precisely so that checking whether antiforgery exists does
    // not build it. Constructing the real service is neither free nor side-effect free, and every
    // multipart route mapped would pay for it. The registration here throws if it is ever constructed,
    // so the mapping call itself would fail.
    [Fact]
    public async Task PostMultiPart_DoesNotConstructTheAntiforgeryServiceWhileProbingForIt()
    {
        var logs = new RecordingLoggerProvider();
        var builder = TestApps.CreateBuilder(loggerProvider: logs);
        builder.Services.AddSingleton<IAntiforgery>(static _ =>
            throw new InvalidOperationException("Antiforgery must not be constructed just to check whether it is registered."));

        await using var app = builder.Build();

        app.PostMultiPart("multipart-probe/files", (IFormFile file) => Results.Ok());

        Assert.Empty(EndpointWarnings(logs));
    }

    // Containers other than the built-in one need not supply IServiceProviderIsService, so the probe
    // falls back to resolving IAntiforgery. The fallback has to reach the same conclusion.
    [Fact]
    public async Task PostMultiPart_WithoutIServiceProviderIsService_StillSeesARegisteredAntiforgeryService()
    {
        var logs = new RecordingLoggerProvider();
        var builder = TestApps.CreateBuilder(loggerProvider: logs);
        builder.Services.AddSingleton<IAntiforgery>(new MultipartStubAntiforgery());

        await using var app = builder.Build();
        var routes = new MultipartRouteBuilder(app, new MultipartFilteredServiceProvider(app.Services, typeof(IServiceProviderIsService)));

        routes.PostMultiPart("multipart-fallback/registered", (IFormFile file) => Results.Ok());

        Assert.Empty(EndpointWarnings(logs));
    }

    [Fact]
    public async Task PostMultiPart_WithoutIServiceProviderIsServiceAndWithoutAntiforgery_Warns()
    {
        var logs = new RecordingLoggerProvider();
        await using var app = TestApps.CreateBuilder(loggerProvider: logs).Build();
        var routes = new MultipartRouteBuilder(app, new MultipartFilteredServiceProvider(app.Services, typeof(IServiceProviderIsService)));

        routes.PostMultiPart("multipart-fallback/missing", (IFormFile file) => Results.Ok());

        var warning = Assert.Single(EndpointWarnings(logs));
        Assert.Contains("multipart-fallback/missing", warning.Message, StringComparison.Ordinal);
    }

    // The warning resolves ILoggerFactory from the container and a container need not have one - a route
    // builder can be handed any IServiceProvider at all. Mapping a route must not become the thing that
    // brings the application down because logging happens to be absent, so the warning is skipped instead.
    // The control half of the test maps the identical route through the unfiltered app: without it, "no
    // warning was logged" would also be satisfied by a route that was never warning-worthy.
    [Fact]
    public async Task PostMultiPart_WithoutALoggerFactory_SkipsTheWarningInsteadOfThrowing()
    {
        var logs = new RecordingLoggerProvider();
        await using var app = TestApps.CreateBuilder(loggerProvider: logs).Build();
        var routes = new MultipartRouteBuilder(app, new MultipartFilteredServiceProvider(app.Services, typeof(ILoggerFactory)));

        routes.PostMultiPart("multipart-nologger/silent", (IFormFile file) => Results.Ok());

        Assert.Empty(EndpointWarnings(logs));

        app.PostMultiPart("multipart-nologger/control", (IFormFile file) => Results.Ok());

        var warning = Assert.Single(EndpointWarnings(logs));
        Assert.Contains("multipart-nologger/control", warning.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // OpenAPI responses inherited from AddResponses
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task MultiPartHelpers_DeclareTheDefaultErrorResponses(string httpMethod)
    {
        await using var app = CreateApp();
        var pattern = $"multipart-errors-{Slug(httpMethod)}/files";

        MapMultiPart(app, httpMethod, pattern, () => Results.Ok());

        var statusCodes = ProducedStatusCodes(app, pattern);

        Assert.Contains(400, statusCodes);
        Assert.Contains(401, statusCodes);
        Assert.Contains(403, statusCodes);
        Assert.Contains(500, statusCodes);
    }

    // The error bodies are documented as ErrorsResponse; declaring the status without the schema leaves
    // generated clients guessing at the failure payload.
    [Fact]
    public async Task PostMultiPart_DeclaresErrorsResponseAsTheBodyOfTheBadRequestAndServerError()
    {
        await using var app = CreateApp();

        app.PostMultiPart("multipart-error-body/files", () => Results.Ok());

        var responses = ResponseMetadata(app, "multipart-error-body/files");

        // The content type is part of the same promise: a schema declared under a content type the endpoint
        // never produces is as useless to a generated client as no schema at all.
        Assert.Contains(responses, static metadata => metadata.StatusCode == 400
            && metadata.Type == typeof(ErrorsResponse)
            && metadata.ContentTypes.Contains("application/json"));
        Assert.Contains(responses, static metadata => metadata.StatusCode == 500
            && metadata.Type == typeof(ErrorsResponse)
            && metadata.ContentTypes.Contains("application/json"));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task MultiPartHelpers_InferTheResponseSchemaFromAMediatorRequestParameter(string httpMethod)
    {
        await using var app = CreateApp();
        var pattern = $"multipart-infer-response-{Slug(httpMethod)}/files";

        MapMultiPart(app, httpMethod, pattern, (MultipartUploadCommand command) => Results.Ok());

        Assert.Contains(
            ResponseMetadata(app, pattern),
            static metadata => metadata.StatusCode == 200 && metadata.Type == typeof(MultipartUploadResult));
    }

    // A request with no response must not advertise 200 as well: Swagger would then show both 200 and 204
    // for a route that only ever returns one of them.
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task MultiPartHelpers_InferNoContentForARequestWithoutAResponse(string httpMethod)
    {
        await using var app = CreateApp();
        var pattern = $"multipart-infer-empty-{Slug(httpMethod)}/files";

        MapMultiPart(app, httpMethod, pattern, (MultipartEmptyCommand command) => Results.NoContent());

        var statusCodes = ProducedStatusCodes(app, pattern);

        Assert.Contains(204, statusCodes);
        Assert.DoesNotContain(200, statusCodes);
    }

    // The control route proves the framework really does infer a 200 for this delegate shape, so the
    // multipart route's missing 200 is the helper removing it and not an accident of the return type.
    [Fact]
    public async Task PostMultiPart_EmptyRequest_RemovesTheFrameworkInferredOkResponse()
    {
        await using var app = CreateApp();

        app.MapPost("multipart-removal/control", (MultipartEmptyCommand command) => Task.FromResult<object?>(null));
        app.PostMultiPart("multipart-removal/helper", (MultipartEmptyCommand command) => Task.FromResult<object?>(null));

        var control = ProducedStatusCodes(app, "multipart-removal/control");
        var helper = ProducedStatusCodes(app, "multipart-removal/helper");

        Assert.Contains(200, control);
        Assert.DoesNotContain(200, helper);
        Assert.Contains(204, helper);
    }

    // With nothing to infer from, the helper must leave the framework's own inferred success response
    // alone; stripping it would misreport every hand-written multipart endpoint.
    [Fact]
    public async Task PostMultiPart_DelegateWithoutAMediatorRequest_LeavesTheInferredSuccessResponseAlone()
    {
        await using var app = CreateApp();

        app.MapPost("multipart-noinfer/control", (MultipartPlainPayload payload) => Task.FromResult<object?>(null));
        app.PostMultiPart("multipart-noinfer/helper", (MultipartPlainPayload payload) => Task.FromResult<object?>(null));

        Assert.Contains(200, ProducedStatusCodes(app, "multipart-noinfer/control"));
        Assert.Contains(200, ProducedStatusCodes(app, "multipart-noinfer/helper"));
    }

    // Multipart routes are not exempt from MediatorOptions: an application that maps empty responses to
    // 200 must see 200 documented here too, or its Swagger contradicts its own responses.
    [Fact]
    public async Task PostMultiPart_ConfiguredOkEmptyResponse_DeclaresOkInsteadOfNoContent()
    {
        await using var app = CreateApp(services => services.Configure<MediatorOptions>(
            static options => options.EmptyResponseStatusCode = EmptyResponseStatusCode.Ok));

        app.PostMultiPart("multipart-empty-ok/files", (MultipartEmptyCommand command) => Results.Ok());

        var statusCodes = ProducedStatusCodes(app, "multipart-empty-ok/files");

        Assert.Contains(200, statusCodes);
        Assert.DoesNotContain(204, statusCodes);
    }

    // ---------------------------------------------------------------------------------------
    // End-to-end shape
    // ---------------------------------------------------------------------------------------

    // The realistic upload route - a route parameter plus the file, with antiforgery properly registered -
    // has to survive real endpoint construction with every convention attached, not just metadata
    // inspection: reading app.Endpoint(...) is what forces the request delegate to be built, and that is
    // where a convention conflicting with form binding surfaces. It must also stay quiet, because a route
    // that has done everything right and still warns trains people to ignore the warning.
    [Fact]
    public async Task PostMultiPart_RouteParameterAndFormFile_BuildsWithEveryMultipartConventionAndNoWarning()
    {
        var logs = new RecordingLoggerProvider();
        var builder = TestApps.CreateBuilder(loggerProvider: logs);
        builder.Services.AddAntiforgery();
        await using var app = builder.Build();

        app.PostMultiPart(
            "multipart-upload/{tenantId}/documents",
            (string tenantId, IFormFile file) => Results.Ok(tenantId),
            maxRequestBodySize: 9_000_000,
            timeoutSeconds: 45);

        var endpoint = app.Endpoint("multipart-upload/{tenantId}/documents");

        Assert.Equal("tenantId", Assert.Single(endpoint.RoutePattern.Parameters).Name);
        Assert.Contains(endpoint.Metadata.OfType<IRequestSizeLimitMetadata>(), static metadata => metadata.MaxRequestBodySize == 9_000_000L);
        Assert.Contains(endpoint.Metadata.OfType<IFormOptionsMetadata>(), static metadata => metadata.MultipartBodyLengthLimit == 9_000_000L);
        Assert.Contains(endpoint.Metadata.OfType<RequestTimeoutPolicy>(), static metadata => metadata.Timeout == TimeSpan.FromSeconds(45));
        Assert.Contains(endpoint.Metadata.OfType<IAcceptsMetadata>(), static metadata => metadata.ContentTypes.Contains("multipart/form-data"));
        Assert.Empty(EndpointWarnings(logs));
    }

    // Every optional argument at once. Each is covered alone above; what this adds is that they do not
    // interfere - in particular that taking the disableAntiforgery branch does not skip the rest of the
    // configuration, the inferred success response included.
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task MultiPartHelpers_EveryOptionalArgumentSet_ApplyAllOfThemTogether(string httpMethod)
    {
        await using var app = CreateApp();
        var pattern = $"multipart-everything-{Slug(httpMethod)}/files";

        MapMultiPartWithEverything(
            app,
            httpMethod,
            pattern,
            (MultipartUploadCommand command) => Results.Ok(),
            maxRequestBodySize: 12_345_678,
            timeoutSeconds: 321,
            disableAntiforgery: true);

        var endpoint = app.Endpoint(pattern);

        Assert.Equal(new[] { httpMethod }, endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
        Assert.Equal(12_345_678L, (long?)RequestSizeLimit(app, pattern).MaxRequestBodySize);
        Assert.Equal(12_345_678L, (long?)FormOptions(app, pattern).MultipartBodyLengthLimit);
        Assert.Equal(TimeSpan.FromSeconds(321), RequestTimeout(app, pattern).Timeout);
        Assert.False(endpoint.Metadata.GetMetadata<IAntiforgeryMetadata>()!.RequiresValidation);
        Assert.Contains(
            ResponseMetadata(app, pattern),
            static metadata => metadata.StatusCode == 200 && metadata.Type == typeof(MultipartUploadResult));
    }

    // The helpers return the RouteHandlerBuilder so the caller can keep configuring the same route -
    // .RequireAuthorization(), .WithName(), a filter. Returning a builder for a different endpoint, or one
    // whose conventions are never applied, loses all of that silently.
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task MultiPartHelpers_ReturnABuilderThatKeepsConfiguringTheSameEndpoint(string httpMethod)
    {
        await using var app = CreateApp();
        var pattern = $"multipart-chained-{Slug(httpMethod)}/files";
        var name = $"MultipartChained{Slug(httpMethod)}";

        MapMultiPart(app, httpMethod, pattern, () => Results.Ok()).WithName(name);

        var endpoint = app.Endpoint(pattern);

        Assert.Equal(name, endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);
        Assert.Contains(endpoint.Metadata.OfType<IAcceptsMetadata>(), static metadata => metadata.ContentTypes.Contains("multipart/form-data"));
    }

    // The two halves of the feature on one route: the delegate binds a file (so the antiforgery check
    // applies) and also takes a mediator request (so the success schema is inferred). Each is tested alone
    // above; a regression that made form detection and response inference read the parameter list
    // differently would only show up where both run over the same delegate.
    [Fact]
    public async Task PostMultiPart_FormBindingDelegateWithAMediatorRequest_KeepsTheFormConventionsAndTheInferredSchema()
    {
        var logs = new RecordingLoggerProvider();
        var builder = TestApps.CreateBuilder(loggerProvider: logs);
        builder.Services.AddAntiforgery();
        builder.Services.AddSingleton(new MultipartUploadCommand("configured"));
        await using var app = builder.Build();

        app.PostMultiPart(
            "multipart-combined/files",
            (IFormFile file, [Microsoft.AspNetCore.Mvc.FromServices] MultipartUploadCommand command) => Results.Ok(command.Name));

        var endpoint = app.Endpoint("multipart-combined/files");

        Assert.Contains(
            ResponseMetadata(app, "multipart-combined/files"),
            static metadata => metadata.StatusCode == 200 && metadata.Type == typeof(MultipartUploadResult));
        Assert.Contains(endpoint.Metadata.OfType<IAcceptsMetadata>(), static metadata => metadata.ContentTypes.Contains("multipart/form-data"));
        Assert.Empty(EndpointWarnings(logs));
    }

    // ---------------------------------------------------------------------------------------
    // Over real HTTP
    // ---------------------------------------------------------------------------------------

    // Metadata assertions cannot show that an upload actually works: every convention the helper attaches -
    // the Accepts content type, the two size limits, the timeout policy - sits on the same endpoint as the
    // form binder, and one that disagreed with form binding would only fail once a real multipart body
    // arrived. This posts one, and checks the file reached the delegate intact.
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task MultiPartHelpers_RealMultipartRequest_BindTheUploadedFile(string httpMethod)
    {
        var builder = TestApps.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        var pattern = $"multipart-http-{Slug(httpMethod)}/files";

        Func<IFormFile, Task<IResult>> handler = async file =>
        {
            using var reader = new StreamReader(file.OpenReadStream());
            return Results.Ok($"{file.FileName}|{await reader.ReadToEndAsync()}");
        };

        MapMultiPartWithAntiforgery(app, httpMethod, pattern, handler, disableAntiforgery: true);

        var (status, body) = await SendUploadAsync(app, httpMethod, pattern, "notes.txt", "hello upload");

        Assert.True(status == HttpStatusCode.OK, $"Expected 200 OK but got {(int)status}. Body: {body}");
        Assert.Contains("notes.txt|hello upload", body, StringComparison.Ordinal);
    }

    // The security contract, as a client sees it rather than as metadata: with the antiforgery services and
    // middleware in place, a tokenless upload is refused, and disableAntiforgery: true is exactly what lets
    // the same request through. If the flag stopped taking effect, nothing about the metadata tests above
    // would change - the route would simply start rejecting the uploads the application meant to accept -
    // and if it took effect when it was not asked for, cookie-authenticated uploads would lose their CSRF
    // protection with no other visible symptom.
    [Theory]
    [InlineData(false, HttpStatusCode.BadRequest)]
    [InlineData(true, HttpStatusCode.OK)]
    public async Task PostMultiPart_AntiforgeryMiddlewareRunning_DisableAntiforgeryDecidesWhetherATokenlessUploadIsAccepted(
        bool disableAntiforgery,
        HttpStatusCode expectedStatus)
    {
        var builder = TestApps.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAntiforgery();
        await using var app = builder.Build();
        app.UseAntiforgery();

        var pattern = $"multipart-csrf-{disableAntiforgery}/files";
        Func<IFormFile, IResult> handler = file => Results.Ok(file.FileName);

        app.PostMultiPart(pattern, handler, disableAntiforgery: disableAntiforgery);

        var (status, body) = await SendUploadAsync(app, "POST", pattern, "upload.txt", "payload");

        Assert.True(status == expectedStatus, $"Expected {(int)expectedStatus} but got {(int)status}. Body: {body}");
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private static WebApplication CreateApp(Action<IServiceCollection>? configureServices = null)
    {
        var builder = TestApps.CreateBuilder();
        configureServices?.Invoke(builder.Services);
        return builder.Build();
    }

    /// <summary>
    /// Maps through the helper under test leaving every optional argument alone, so the defaults these
    /// tests assert on are the library's and not this file's.
    /// </summary>
    private static RouteHandlerBuilder MapMultiPart(IEndpointRouteBuilder builder, string httpMethod, string pattern, Delegate handler)
        => httpMethod switch
        {
            "POST" => builder.PostMultiPart(pattern, handler),
            "PUT" => builder.PutMultiPart(pattern, handler),
            "PATCH" => builder.PatchMultiPart(pattern, handler),
            _ => throw new ArgumentOutOfRangeException(nameof(httpMethod), httpMethod, "Unknown multipart helper.")
        };

    private static RouteHandlerBuilder MapMultiPartWithSizeLimit(IEndpointRouteBuilder builder, string httpMethod, string pattern, Delegate handler, long maxRequestBodySize)
        => httpMethod switch
        {
            "POST" => builder.PostMultiPart(pattern, handler, maxRequestBodySize: maxRequestBodySize),
            "PUT" => builder.PutMultiPart(pattern, handler, maxRequestBodySize: maxRequestBodySize),
            "PATCH" => builder.PatchMultiPart(pattern, handler, maxRequestBodySize: maxRequestBodySize),
            _ => throw new ArgumentOutOfRangeException(nameof(httpMethod), httpMethod, "Unknown multipart helper.")
        };

    private static RouteHandlerBuilder MapMultiPartWithTimeout(IEndpointRouteBuilder builder, string httpMethod, string pattern, Delegate handler, int timeoutSeconds)
        => httpMethod switch
        {
            "POST" => builder.PostMultiPart(pattern, handler, timeoutSeconds: timeoutSeconds),
            "PUT" => builder.PutMultiPart(pattern, handler, timeoutSeconds: timeoutSeconds),
            "PATCH" => builder.PatchMultiPart(pattern, handler, timeoutSeconds: timeoutSeconds),
            _ => throw new ArgumentOutOfRangeException(nameof(httpMethod), httpMethod, "Unknown multipart helper.")
        };

    private static RouteHandlerBuilder MapMultiPartWithAntiforgery(IEndpointRouteBuilder builder, string httpMethod, string pattern, Delegate handler, bool disableAntiforgery)
        => httpMethod switch
        {
            "POST" => builder.PostMultiPart(pattern, handler, disableAntiforgery: disableAntiforgery),
            "PUT" => builder.PutMultiPart(pattern, handler, disableAntiforgery: disableAntiforgery),
            "PATCH" => builder.PatchMultiPart(pattern, handler, disableAntiforgery: disableAntiforgery),
            _ => throw new ArgumentOutOfRangeException(nameof(httpMethod), httpMethod, "Unknown multipart helper.")
        };

    private static RouteHandlerBuilder MapMultiPartWithEverything(
        IEndpointRouteBuilder builder,
        string httpMethod,
        string pattern,
        Delegate handler,
        long maxRequestBodySize,
        int timeoutSeconds,
        bool disableAntiforgery)
        => httpMethod switch
        {
            "POST" => builder.PostMultiPart(pattern, handler, maxRequestBodySize, timeoutSeconds, disableAntiforgery),
            "PUT" => builder.PutMultiPart(pattern, handler, maxRequestBodySize, timeoutSeconds, disableAntiforgery),
            "PATCH" => builder.PatchMultiPart(pattern, handler, maxRequestBodySize, timeoutSeconds, disableAntiforgery),
            _ => throw new ArgumentOutOfRangeException(nameof(httpMethod), httpMethod, "Unknown multipart helper.")
        };

    /// <summary>
    /// Maps one multipart route and returns the warnings the library logged. Nothing here enumerates the
    /// endpoints, so only a warning raised while the route was being mapped can show up.
    /// </summary>
    private static async Task<IReadOnlyList<LogEntry>> CaptureWarnings(
        string httpMethod,
        string pattern,
        Delegate handler,
        bool disableAntiforgery = false,
        bool registerAntiforgery = false)
    {
        var logs = new RecordingLoggerProvider();
        var builder = TestApps.CreateBuilder(loggerProvider: logs);

        if (registerAntiforgery)
            builder.Services.AddAntiforgery();

        await using var app = builder.Build();

        MapMultiPartWithAntiforgery(app, httpMethod, pattern, handler, disableAntiforgery);

        return EndpointWarnings(logs);
    }

    /// <summary>
    /// Starts the application on the test server and posts one real <c>multipart/form-data</c> body with a
    /// single file part named <c>file</c>, so it binds to a delegate parameter of that name.
    /// </summary>
    private static async Task<(HttpStatusCode Status, string Body)> SendUploadAsync(
        WebApplication app,
        string httpMethod,
        string pattern,
        string fileName,
        string fileContent)
    {
        await app.StartAsync();

        using var client = app.GetTestClient();
        using var content = new MultipartFormDataContent
        {
            { new StringContent(fileContent), "file", fileName }
        };
        using var request = new HttpRequestMessage(new HttpMethod(httpMethod), "/" + pattern) { Content = content };
        using var response = await client.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static IReadOnlyList<LogEntry> EndpointWarnings(RecordingLoggerProvider logs)
        => logs.Warnings.Where(static entry => entry.Category == typeof(EndpointsExtensions).FullName).ToArray();

    private static IRequestSizeLimitMetadata RequestSizeLimit(WebApplication app, string routeSuffix)
        => Assert.Single(app.Endpoint(routeSuffix).Metadata.OfType<IRequestSizeLimitMetadata>());

    private static IFormOptionsMetadata FormOptions(WebApplication app, string routeSuffix)
        => Assert.Single(app.Endpoint(routeSuffix).Metadata.OfType<IFormOptionsMetadata>());

    private static RequestTimeoutPolicy RequestTimeout(WebApplication app, string routeSuffix)
        => Assert.Single(app.Endpoint(routeSuffix).Metadata.OfType<RequestTimeoutPolicy>());

    private static IReadOnlyList<IProducesResponseTypeMetadata> ResponseMetadata(WebApplication app, string routeSuffix)
        => app.Endpoint(routeSuffix).Metadata.OfType<IProducesResponseTypeMetadata>().ToArray();

    private static int[] ProducedStatusCodes(WebApplication app, string routeSuffix)
        => ResponseMetadata(app, routeSuffix).Select(static metadata => metadata.StatusCode).Distinct().ToArray();

    private static string Slug(string httpMethod) => httpMethod.ToLowerInvariant();
}
