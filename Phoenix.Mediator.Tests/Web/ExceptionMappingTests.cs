using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Phoenix.Mediator.Exceptions;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Tests.Infrastructure;
using Phoenix.Mediator.Web;
using Phoenix.Mediator.Web.Middlewares;
using Phoenix.Mediator.Wrappers;
using Xunit;

namespace Phoenix.Mediator.Tests;

/// <summary>
/// <see cref="ExceptionHandlingOptions"/>: the app's own exception-to-status mappings, the switch for the common-exception
/// mappings, and the 401/403/409 exception types. Types are prefixed <c>Em</c>.
/// </summary>
public sealed class ExceptionMappingTests
{
    private const string UnknownError = "Unknown error occurred";

    // ---------------------------------------------------------------------------------------------
    // Map<TException>(...)
    // ---------------------------------------------------------------------------------------------

    // The exception's own message is never sent by this overload: EF Core's concurrency message, for one, describes
    // row counts and links to documentation. The caller gets the same generic message an unhandled error gets.
    [Fact]
    public async Task Map_WithAStatusOnly_WritesThatStatusWithTheGenericMessage()
    {
        var options = new ExceptionHandlingOptions().Map<EmConcurrencyException>(HttpStatusCode.Conflict);

        var context = await RunAsync(new EmConcurrencyException("em-affected-0-rows"), options);

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.Equal(new[] { UnknownError }, Errors(context));
        Assert.DoesNotContain("em-affected-0-rows", Body(context));
    }

    // ...and that generic message is localized like every other one.
    [Fact]
    public async Task Map_WithAStatusOnly_LocalizesTheGenericMessage()
    {
        var options = new ExceptionHandlingOptions().Map<EmConcurrencyException>(HttpStatusCode.Conflict);
        var configuration = Configuration(new Dictionary<string, string?> { ["ErrorMessages:Ar"] = "em-arabic", ["ErrorMessages:En"] = "em-english" });

        var context = await RunAsync(new EmConcurrencyException("em-detail"), options, configuration, acceptLanguage: "ar");

        Assert.Equal(new[] { "em-arabic" }, Errors(context));
    }

    [Fact]
    public async Task Map_WithAMessage_WritesThatMessage()
    {
        var options = new ExceptionHandlingOptions()
            .Map<EmConcurrencyException>(HttpStatusCode.Conflict, exception => $"Changed by someone else ({exception.GetType().Name}).");

        var context = await RunAsync(new EmConcurrencyException("em-detail"), options);

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.Equal(new[] { "Changed by someone else (EmConcurrencyException)." }, Errors(context));
    }

    [Fact]
    public async Task Map_WithFullControl_WritesTheResponseItReturns()
    {
        var options = new ExceptionHandlingOptions().Map<EmDuplicateKeyException>(exception =>
            new ErrorResponse(HttpStatusCode.Conflict, [$"{exception.Field} is taken."]));

        var context = await RunAsync(new EmDuplicateKeyException("email"), options);

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.Equal(new[] { "email is taken." }, Errors(context));
    }

    [Fact]
    public async Task Map_AppliesToExceptionsDerivedFromTheMappedType()
    {
        var options = new ExceptionHandlingOptions().Map<EmDataException>(HttpStatusCode.ServiceUnavailable);

        var context = await RunAsync(new EmConcurrencyException("em-detail"), options);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    // Registration order must not decide which of two mappings applies: the one for the exception's own type does.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Map_TheMostSpecificMappingWins(bool baseMappedFirst)
    {
        var options = new ExceptionHandlingOptions();
        if (baseMappedFirst)
            options.Map<EmDataException>(HttpStatusCode.ServiceUnavailable).Map<EmConcurrencyException>(HttpStatusCode.PreconditionFailed);
        else
            options.Map<EmConcurrencyException>(HttpStatusCode.PreconditionFailed).Map<EmDataException>(HttpStatusCode.ServiceUnavailable);

        var derived = await RunAsync(new EmConcurrencyException("em-detail"), options);
        var baseOnly = await RunAsync(new EmDataException("em-detail"), options);

        Assert.Equal(StatusCodes.Status412PreconditionFailed, derived.Response.StatusCode);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, baseOnly.Response.StatusCode);
    }

    // Declining (null) is how a mapping handles only some exceptions of a type — a database error that reports a
    // unique-key violation, say. The next mapping to try is the base type's.
    [Fact]
    public async Task Map_ReturningNull_LeavesTheExceptionToTheBaseTypesMapping()
    {
        var options = new ExceptionHandlingOptions()
            .Map<EmDataException>(HttpStatusCode.ServiceUnavailable)
            .Map<EmConcurrencyException>(static _ => null);

        var context = await RunAsync(new EmConcurrencyException("em-detail"), options);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Fact]
    public async Task Map_ReturningNullWithNothingElseMapped_LeavesTheExceptionUnhandled()
    {
        var options = new ExceptionHandlingOptions().Map<EmConcurrencyException>(static _ => null);

        var context = await RunAsync(new EmConcurrencyException("em-detail"), options);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal(new[] { UnknownError }, Errors(context));
    }

    [Fact]
    public async Task Map_SameTypeTwice_TheLaterMappingReplacesTheEarlier()
    {
        var options = new ExceptionHandlingOptions()
            .Map<EmConcurrencyException>(HttpStatusCode.Conflict)
            .Map<EmConcurrencyException>(HttpStatusCode.Gone);

        var context = await RunAsync(new EmConcurrencyException("em-detail"), options);

        Assert.Equal(StatusCodes.Status410Gone, context.Response.StatusCode);
    }

    // A mapping that throws must not replace the error the request actually hit: that one is still reported as an
    // unhandled 500, and the broken mapping is logged so someone fixes it.
    [Fact]
    public async Task Map_ThatThrows_ReportsTheOriginalExceptionAndLogsTheMappingFailure()
    {
        var recorder = new RecordingLoggerProvider();
        var options = new ExceptionHandlingOptions()
            .Map<EmConcurrencyException>(static _ => throw new InvalidOperationException("em-broken-mapping"));
        var original = new EmConcurrencyException("em-detail");

        var context = await RunAsync(original, options, logger: new Logger<ExceptionHandlingMiddleware>(recorder));

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Contains(recorder.Entries, static entry => entry.Level == LogLevel.Error && entry.Exception?.Message == "em-broken-mapping");
        Assert.Contains(recorder.Entries, entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, original));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(302)]
    [InlineData(399)]
    [InlineData(600)]
    public void Map_ToAStatusThatIsNotAnError_IsRejected(int statusCode)
    {
        var options = new ExceptionHandlingOptions();

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Map<EmConcurrencyException>((HttpStatusCode)statusCode));
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Map<EmConcurrencyException>((HttpStatusCode)statusCode, static _ => "em"));
    }

    [Fact]
    public async Task Map_ToAClientError_LogsLikeEveryClientError()
    {
        var recorder = new RecordingLoggerProvider();
        var options = new ExceptionHandlingOptions().Map<EmConcurrencyException>(HttpStatusCode.Conflict, static _ => "em-conflict");

        await RunAsync(new EmConcurrencyException("em-detail"), options, logger: new Logger<ExceptionHandlingMiddleware>(recorder));

        var entry = Assert.Single(recorder.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Contains("409", entry.Message);
    }

    [Fact]
    public async Task Map_ToAServerError_LogsAtErrorWithTheException()
    {
        var recorder = new RecordingLoggerProvider();
        var options = new ExceptionHandlingOptions().Map<EmDataException>(HttpStatusCode.ServiceUnavailable);
        var exception = new EmDataException("em-detail");

        await RunAsync(exception, options, logger: new Logger<ExceptionHandlingMiddleware>(recorder));

        var entry = Assert.Single(recorder.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(exception, entry.Exception);
    }

    // An app's own mapping is more specific than the compatibility mappings, so it wins over them.
    [Fact]
    public async Task Map_OverridesTheCommonExceptionMapping()
    {
        var options = new ExceptionHandlingOptions().Map<KeyNotFoundException>(HttpStatusCode.Gone);

        var context = await RunAsync(new KeyNotFoundException("em-key"), options);

        Assert.Equal(StatusCodes.Status410Gone, context.Response.StatusCode);
    }

    // HttpResponseException already is an error response; it is handled before any mapping is looked at.
    [Fact]
    public async Task Map_DoesNotApplyToHttpResponseExceptions()
    {
        var options = new ExceptionHandlingOptions().Map<HttpResponseException>(HttpStatusCode.Gone);

        var context = await RunAsync(new NotFoundException("em-missing"), options);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    // The cancellation of an aborted request is handled before any mapping: a client disconnect ends with 499.
    [Fact]
    public async Task Map_DoesNotApplyToTheCancellationOfAnAbortedRequest()
    {
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        var options = new ExceptionHandlingOptions().Map<OperationCanceledException>(HttpStatusCode.GatewayTimeout);
        var context = CreateContext();
        context.RequestAborted = aborted.Token;
        var middleware = CreateMiddleware(_ => throw new OperationCanceledException(aborted.Token), options);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, context.Response.StatusCode);
    }

    // A request timeout is rethrown before any mapping, so UseRequestTimeouts still writes its 504.
    [Fact]
    public async Task Map_DoesNotApplyToARequestTimeout()
    {
        using var timedOut = new CancellationTokenSource();
        timedOut.Cancel();
        var options = new ExceptionHandlingOptions().Map<OperationCanceledException>(HttpStatusCode.GatewayTimeout);
        var context = CreateContext();
        context.Features.Set<IHttpRequestTimeoutFeature>(new TestRequestTimeoutFeature(timedOut.Token));
        context.RequestAborted = timedOut.Token;
        var middleware = CreateMiddleware(_ => throw new OperationCanceledException(timedOut.Token), options);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));
    }

    // A cancellation the request did not cause — an HttpClient timeout inside a handler — is the app's to map.
    [Fact]
    public async Task Map_AppliesToACancellationTheRequestDidNotCause()
    {
        var options = new ExceptionHandlingOptions().Map<TaskCanceledException>(HttpStatusCode.GatewayTimeout);

        var context = await RunAsync(new TaskCanceledException("em-upstream-timeout"), options);

        Assert.Equal(StatusCodes.Status504GatewayTimeout, context.Response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // MapCommonExceptions
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void MapCommonExceptions_IsOnByDefault()
    {
        Assert.True(new ExceptionHandlingOptions().MapCommonExceptions);
    }

    // Off, these are what they usually are: server-side bugs, reported as 500 and logged at Error.
    [Theory]
    [InlineData("argument")]
    [InlineData("argument-null")]
    [InlineData("key-not-found")]
    [InlineData("unauthorized-access")]
    public async Task MapCommonExceptionsOff_ReportsTheExceptionAsAServerError(string kind)
    {
        var recorder = new RecordingLoggerProvider();
        var exception = kind switch
        {
            "argument" => (Exception)new ArgumentException("em-detail"),
            "argument-null" => new ArgumentNullException("emParameter"),
            "key-not-found" => new KeyNotFoundException("em-detail"),
            _ => new UnauthorizedAccessException("em-detail"),
        };

        var context = await RunAsync(exception, new ExceptionHandlingOptions { MapCommonExceptions = false }, logger: new Logger<ExceptionHandlingMiddleware>(recorder));

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal(new[] { UnknownError }, Errors(context));
        Assert.Contains(recorder.Entries, entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, exception));
    }

    // On (the default) the behavior of every earlier version is unchanged, 401 with no body included.
    [Theory]
    [InlineData("argument", 400)]
    [InlineData("key-not-found", 404)]
    [InlineData("unauthorized-access", 401)]
    public async Task MapCommonExceptionsOn_KeepsTheCompatibilityMappings(string kind, int expectedStatusCode)
    {
        var exception = kind switch
        {
            "argument" => (Exception)new ArgumentException("em-detail"),
            "key-not-found" => new KeyNotFoundException("em-detail"),
            _ => new UnauthorizedAccessException("em-detail"),
        };

        var context = await RunAsync(exception, new ExceptionHandlingOptions());

        Assert.Equal(expectedStatusCode, context.Response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // The new exception types.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("unauthorized", 401)]
    [InlineData("forbidden", 403)]
    [InlineData("conflict", 409)]
    public async Task NewExceptionTypes_CarryTheirStatusAndMessageToTheResponse(string kind, int expectedStatusCode)
    {
        HttpResponseException exception = kind switch
        {
            "unauthorized" => new UnauthorizedException("em-message"),
            "forbidden" => new ForbiddenException("em-message"),
            _ => new ConflictException("em-message"),
        };

        var context = await RunAsync(exception, new ExceptionHandlingOptions());

        Assert.Equal(expectedStatusCode, (int)exception.HttpStatusCode);
        Assert.Equal("em-message", exception.Message);
        Assert.Equal(expectedStatusCode, context.Response.StatusCode);
        Assert.Equal(new[] { "em-message" }, Errors(context));
    }

    // ---------------------------------------------------------------------------------------------
    // Wiring: options configured in the container reach the middleware MapEndpoints registers.
    // ---------------------------------------------------------------------------------------------

    // UseMiddleware picks the constructor that takes IOptions<ExceptionHandlingOptions>; the three-argument one would
    // silently ignore everything configured here.
    [Fact]
    public async Task ConfiguredMappings_ReachTheMiddlewareRegisteredFromTheContainer()
    {
        var builder = TestApps.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMediator();
        builder.Services.Configure<ExceptionHandlingOptions>(static options => options
            .Map<EmConcurrencyException>(HttpStatusCode.Conflict, static _ => "em-changed")
            .MapCommonExceptions = false);
        await using var app = builder.Build();

        app.UsePhoenixExceptionHandling();
        app.MapGet("em/concurrency", () => { throw new EmConcurrencyException("em-detail"); });
        app.MapGet("em/argument", () => { throw new ArgumentException("em-detail"); });
        await app.StartAsync();
        using var client = app.GetTestClient();

        var mapped = await client.GetAsync("em/concurrency");
        var unmapped = await client.GetAsync("em/argument");

        Assert.Equal(HttpStatusCode.Conflict, mapped.StatusCode);
        Assert.Contains("em-changed", await mapped.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.InternalServerError, unmapped.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    private static DefaultHttpContext CreateContext(string? acceptLanguage = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/em";
        context.Response.Body = new MemoryStream();

        if (acceptLanguage is not null)
            context.Request.Headers.AcceptLanguage = acceptLanguage;

        return context;
    }

    private static ExceptionHandlingMiddleware CreateMiddleware(
        RequestDelegate next,
        ExceptionHandlingOptions options,
        IConfiguration? configuration = null,
        ILogger<ExceptionHandlingMiddleware>? logger = null)
    {
        return new ExceptionHandlingMiddleware(
            next,
            logger ?? NullLogger<ExceptionHandlingMiddleware>.Instance,
            configuration ?? Configuration(),
            Options.Create(options));
    }

    private static async Task<DefaultHttpContext> RunAsync(
        Exception exception,
        ExceptionHandlingOptions options,
        IConfiguration? configuration = null,
        string? acceptLanguage = null,
        ILogger<ExceptionHandlingMiddleware>? logger = null)
    {
        var context = CreateContext(acceptLanguage);
        await CreateMiddleware(_ => throw exception, options, configuration, logger).InvokeAsync(context);
        return context;
    }

    private static IConfiguration Configuration(Dictionary<string, string?>? values = null)
    {
        var builder = new ConfigurationBuilder();
        if (values is not null)
            builder.AddInMemoryCollection(values);
        return builder.Build();
    }

    private static string Body(HttpContext context) => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private static JsonElement Json(HttpContext context) => JsonDocument.Parse(Body(context)).RootElement.Clone();

    private static string[] Errors(HttpContext context)
        => Json(context).GetProperty("errors").EnumerateArray().Select(static error => error.GetString()!).ToArray();
}

public class EmDataException(string message) : Exception(message);

public sealed class EmConcurrencyException(string message) : EmDataException(message);

public sealed class EmDuplicateKeyException(string field) : Exception($"Duplicate key on {field}.")
{
    public string Field { get; } = field;
}
