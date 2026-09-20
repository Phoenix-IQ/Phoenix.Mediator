using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Phoenix.Mediator.Exceptions;
using Phoenix.Mediator.Tests.Infrastructure;
using Phoenix.Mediator.Web.Middlewares;
using Phoenix.Mediator.Wrappers;
using Xunit;

namespace Phoenix.Mediator.Tests;

/// <summary>
/// Every branch of <see cref="ExceptionHandlingMiddleware"/>: the pass-through path, each catch arm,
/// the status mapping, the trace id, and the <c>ErrorMessages</c> language selection.
/// The middleware is constructed directly around a <see cref="DefaultHttpContext"/> with a seekable
/// response body, which is the only way to observe what it actually wrote.
/// </summary>
public sealed class ExceptionHandlingMiddlewareTests
{
    // The text the middleware hard-codes when nothing is configured. Kept as a literal on purpose:
    // it is part of the public wire contract, so a change to it must break a test.
    private const string ExBuiltInUnknownMessage = "Unknown error occurred";

    private const string ExEnglishMessage = "ex-english-unknown-error";
    private const string ExArabicMessage = "ex-arabic-unknown-error";
    private const string ExFrenchMessage = "ex-french-unknown-error";

    // Written as escapes so the assertion cannot be weakened by however this source file is decoded.
    private const string ExArabicUnicodeMessage = "\u062d\u0635\u0644 \u062e\u0637\u0623 \u063a\u064a\u0631 \u0645\u0639\u0631\u0648\u0641";

    private const string ExTraceIdentifierValue = "ex-trace-identifier";
    private const string ExRequestMethod = "POST";
    private const string ExRequestPath = "/ex-tests/orders";

    // ---------------------------------------------------------------------------------------------
    // No exception: the middleware must be invisible.
    // ---------------------------------------------------------------------------------------------

    // A middleware that touched a successful response would corrupt the output of every endpoint in the
    // app, and a log line per request would make the error log useless. The happy path must do nothing.
    [Fact]
    public async Task InvokeAsync_WhenNextSucceeds_LeavesTheResponseAndTheLogUntouched()
    {
        var recorder = new RecordingLoggerProvider();
        var context = ExCreateContext();
        var middleware = new ExceptionHandlingMiddleware(
            async ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status201Created;
                ctx.Response.ContentType = "text/plain";
                await ctx.Response.WriteAsync("ex-created");
            },
            ExLogger(recorder),
            ExConfiguration());

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status201Created, context.Response.StatusCode);
        Assert.Equal("text/plain", context.Response.ContentType);
        Assert.Equal("ex-created", ExBody(context));
        Assert.Empty(recorder.Entries);
    }

    // ---------------------------------------------------------------------------------------------
    // HttpResponseException: the status and the messages come from the exception itself.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 400)]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    [InlineData(HttpStatusCode.NotFound, 404)]
    [InlineData(HttpStatusCode.Conflict, 409)]
    [InlineData(HttpStatusCode.UnprocessableEntity, 422)]
    public async Task InvokeAsync_HttpResponseException_UsesTheStatusCodeCarriedByTheErrorResponse(
        HttpStatusCode configuredStatusCode,
        int expectedStatusCode)
    {
        var context = await ExRunAsync(new HttpResponseException(new ErrorResponse(configuredStatusCode, ["ex-error"])));

        Assert.Equal(expectedStatusCode, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
        Assert.Equal("ex-error", ExFirstError(context));
    }

    // NotFoundException is one of the two built-in exceptions the README tells callers to throw, so its
    // end-to-end shape (404 + the message the domain wrote) is the documented contract.
    [Fact]
    public async Task InvokeAsync_NotFoundException_Writes404WithTheDomainMessage()
    {
        var context = await ExRunAsync(new NotFoundException("Order 42 was not found."));

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
        Assert.Equal("Order 42 was not found.", Assert.Single(ExErrors(context)));
    }

    [Fact]
    public async Task InvokeAsync_BadRequestException_Writes400WithTheDomainMessage()
    {
        var context = await ExRunAsync(new BadRequestException("Quantity must be greater than zero."));

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
        Assert.Equal("Quantity must be greater than zero.", Assert.Single(ExErrors(context)));
    }

    // Validation-style failures carry several messages; dropping or reordering them would leave a form
    // highlighting the wrong field.
    [Fact]
    public async Task InvokeAsync_HttpResponseException_WritesEveryMessageInOrder()
    {
        var exception = new HttpResponseException(
            new ErrorResponse(HttpStatusCode.BadRequest, ["ex-first", "ex-second", "ex-third"]));

        var context = await ExRunAsync(exception);

        Assert.Equal(new[] { "ex-first", "ex-second", "ex-third" }, ExErrors(context));
    }

    // An empty error list still has to produce a well-formed body: clients parse "errors" unconditionally.
    [Fact]
    public async Task InvokeAsync_HttpResponseExceptionWithoutMessages_WritesAnEmptyErrorsArray()
    {
        var context = await ExRunAsync(new HttpResponseException(new ErrorResponse(HttpStatusCode.Forbidden, [])));

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Empty(ExErrors(context));
        Assert.False(string.IsNullOrWhiteSpace(ExTraceId(context)));
    }

    // Unlike the unhandled path, an HttpResponseException carries messages the developer chose for the
    // caller, so they are sent verbatim even when the status is a 5xx.
    [Fact]
    public async Task InvokeAsync_HttpResponseExceptionWithServerErrorStatus_KeepsItsOwnMessages()
    {
        var exception = new HttpResponseException(
            new ErrorResponse(HttpStatusCode.ServiceUnavailable, ["ex-payment-provider-is-down"]));

        var context = await ExRunAsync(exception, ExLocalizedConfiguration(), acceptLanguage: "en");

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("ex-payment-provider-is-down", ExFirstError(context));
        Assert.DoesNotContain(ExEnglishMessage, ExBody(context));
    }

    // The warning is what ties a 404 seen by a client to the code path that produced it.
    [Fact]
    public async Task InvokeAsync_HttpResponseException_LogsAWarningCarryingTheExceptionAndTheRoute()
    {
        var recorder = new RecordingLoggerProvider();
        var exception = new NotFoundException("ex-order-missing");

        await ExRunAsync(exception, logger: ExLogger(recorder));

        var warning = Assert.Single(recorder.Warnings);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Same(exception, warning.Exception);
        Assert.Contains($"{ExRequestMethod} {ExRequestPath}", warning.Message);
        Assert.Contains(nameof(ExceptionHandlingMiddleware), warning.Category);
    }

    // ---------------------------------------------------------------------------------------------
    // BadHttpRequestException: the framework already decided the status, and the message belongs to
    // the caller's mistake.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(400)]
    [InlineData(413)]
    [InlineData(415)]
    public async Task InvokeAsync_BadHttpRequestException_KeepsTheFrameworkStatusAndMessageForClientErrors(int statusCode)
    {
        const string message = "Required parameter \"int id\" was not provided from route.";

        var context = await ExRunAsync(new BadHttpRequestException(message, statusCode));

        Assert.Equal(statusCode, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
        Assert.Equal(message, Assert.Single(ExErrors(context)));
        Assert.False(string.IsNullOrWhiteSpace(ExTraceId(context)));
    }

    // The single-argument constructor is what Minimal API binding throws; it defaults to 400 and the
    // middleware must not override it.
    [Fact]
    public async Task InvokeAsync_BadHttpRequestExceptionWithoutAStatus_Writes400()
    {
        var context = await ExRunAsync(new BadHttpRequestException("ex-malformed-json-body"));

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("ex-malformed-json-body", ExFirstError(context));
    }

    // A 5xx BadHttpRequestException describes a server-side pipeline fault, so its message would leak
    // internals to the caller; only 4xx messages are useful to whoever sent the request.
    [Fact]
    public async Task InvokeAsync_BadHttpRequestExceptionWithServerErrorStatus_ReplacesTheMessageWithTheUnknownError()
    {
        var context = await ExRunAsync(
            new BadHttpRequestException("ex-internal-pipe-detail", StatusCodes.Status500InternalServerError));

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal(ExBuiltInUnknownMessage, ExFirstError(context));
        Assert.DoesNotContain("ex-internal-pipe-detail", ExBody(context));
    }

    // The replacement message goes through the same localization as an unhandled error.
    [Fact]
    public async Task InvokeAsync_BadHttpRequestExceptionWithServerErrorStatus_LocalizesTheReplacementMessage()
    {
        var context = await ExRunAsync(
            new BadHttpRequestException("ex-internal-pipe-detail", StatusCodes.Status502BadGateway),
            ExLocalizedConfiguration(),
            acceptLanguage: "ar");

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        Assert.Equal(ExArabicMessage, ExFirstError(context));
    }

    // Bad requests are the caller's fault, not the server's: a warning keeps them out of the error log
    // while still leaving a trace of a client that is repeatedly sending garbage.
    [Fact]
    public async Task InvokeAsync_BadHttpRequestException_LogsAWarningCarryingTheException()
    {
        var recorder = new RecordingLoggerProvider();
        var exception = new BadHttpRequestException("ex-malformed-json-body", 400);

        await ExRunAsync(exception, logger: ExLogger(recorder));

        var warning = Assert.Single(recorder.Warnings);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Same(exception, warning.Exception);
        Assert.Contains($"{ExRequestMethod} {ExRequestPath}", warning.Message);
    }

    // The bound is the 4xx range, not "anything below 500". 499 is a real status (proxies use it for a
    // client that hung up mid-request) and its message still describes the request, so it stays.
    [Fact]
    public async Task InvokeAsync_BadHttpRequestExceptionAtTheTopOfTheClientErrorRange_KeepsItsMessage()
    {
        var context = await ExRunAsync(new BadHttpRequestException("ex-client-closed-request", 499));

        Assert.Equal(499, context.Response.StatusCode);
        Assert.Equal("ex-client-closed-request", ExFirstError(context));
    }

    // The other end of the same bound: a status below 400 does not describe a caller mistake, so the
    // message is not the caller's business either. A `< 500` test alone would not notice that edge move.
    [Fact]
    public async Task InvokeAsync_BadHttpRequestExceptionBelowTheClientErrorRange_ReplacesTheMessage()
    {
        var context = await ExRunAsync(new BadHttpRequestException("ex-internal-pipe-detail", 399));

        Assert.Equal(399, context.Response.StatusCode);
        Assert.Equal(ExBuiltInUnknownMessage, ExFirstError(context));
        Assert.DoesNotContain("ex-internal-pipe-detail", ExBody(context));
    }

    // Localization replaces the message the middleware substitutes, never the framework's own 4xx text.
    // Handing a translated "Unknown error" to someone whose request was missing a route parameter would
    // throw away the only sentence that says what to fix.
    [Fact]
    public async Task InvokeAsync_BadHttpRequestExceptionWithClientErrorStatus_DoesNotLocalizeTheFrameworkMessage()
    {
        var context = await ExRunAsync(
            new BadHttpRequestException("ex-missing-route-parameter", StatusCodes.Status400BadRequest),
            ExLocalizedConfiguration(),
            acceptLanguage: "ar");

        Assert.Equal("ex-missing-route-parameter", ExFirstError(context));
        Assert.DoesNotContain(ExArabicMessage, ExBody(context));
    }

    // ---------------------------------------------------------------------------------------------
    // Cancellation.
    // ---------------------------------------------------------------------------------------------

    // A client that hangs up, or a request timeout, must keep flowing outwards: swallowing it here would
    // hide the 504 that UseRequestTimeouts writes when it sees the exception.
    [Fact]
    public async Task InvokeAsync_OperationCanceledExceptionWhileRequestAborted_RethrowsTheSameException()
    {
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();

        var context = ExCreateContext();
        context.RequestAborted = aborted.Token;
        var thrown = new OperationCanceledException(aborted.Token);
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw thrown,
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            ExConfiguration());

        var caught = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));

        Assert.Same(thrown, caught);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Empty(ExBody(context));
    }

    // TaskCanceledException is what an awaited HttpClient/EF call actually throws on abort; it derives
    // from OperationCanceledException and must take the same path.
    [Fact]
    public async Task InvokeAsync_TaskCanceledExceptionWhileRequestAborted_IsRethrownToo()
    {
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();

        var context = ExCreateContext();
        context.RequestAborted = aborted.Token;
        var thrown = new TaskCanceledException("ex-await-cancelled");
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw thrown,
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            ExConfiguration());

        var caught = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));

        Assert.Same(thrown, caught);
    }

    // Aborted requests are routine on a public API. Logging them above Debug would bury real failures.
    [Fact]
    public async Task InvokeAsync_OperationCanceledExceptionWhileRequestAborted_LogsOnlyAtDebug()
    {
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();

        var recorder = new RecordingLoggerProvider();
        var context = ExCreateContext();
        context.RequestAborted = aborted.Token;
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new OperationCanceledException(aborted.Token),
            ExLogger(recorder),
            ExConfiguration());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));

        Assert.Empty(recorder.Warnings);
        Assert.Contains(recorder.Entries, entry => entry.Level == LogLevel.Debug
            && entry.Message.Contains($"Request cancelled for {ExRequestMethod} {ExRequestPath}"));
    }

    // Without the RequestAborted guard this would also be rethrown, and a cancellation raised by the
    // application's own code (a CancellationTokenSource of its own) would escape as an unhandled error.
    [Fact]
    public async Task InvokeAsync_OperationCanceledExceptionWhileRequestWasNotAborted_Writes500()
    {
        using var unrelated = new CancellationTokenSource();
        unrelated.Cancel();

        var context = await ExRunAsync(new OperationCanceledException(unrelated.Token));

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal(ExBuiltInUnknownMessage, ExFirstError(context));
    }

    [Fact]
    public async Task InvokeAsync_TaskCanceledExceptionWhileRequestWasNotAborted_Writes500()
    {
        var context = await ExRunAsync(new TaskCanceledException("ex-await-cancelled"));

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal(ExBuiltInUnknownMessage, ExFirstError(context));
    }

    // Catch-arm ordering: a domain failure raised on a request that happens to be aborted is still a
    // domain failure, so it keeps its own status instead of being rethrown as a cancellation.
    [Fact]
    public async Task InvokeAsync_HttpResponseExceptionOnAnAbortedRequest_IsStillTranslatedToItsStatus()
    {
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();

        var context = ExCreateContext();
        context.RequestAborted = aborted.Token;
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new NotFoundException("ex-order-missing"),
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            ExConfiguration());

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("ex-order-missing", ExFirstError(context));
    }

    // The rethrow is guarded on OperationCanceledException specifically. A handler that fails for an
    // unrelated reason on a request that happens to be aborted is still a server fault: widening the
    // guard to the catch-all would drop those 500s and their stack traces on every disconnect.
    [Fact]
    public async Task InvokeAsync_UnhandledExceptionOnAnAbortedRequest_IsStillReportedAs500()
    {
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();

        var recorder = new RecordingLoggerProvider();
        var context = ExCreateContext();
        context.RequestAborted = aborted.Token;
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new InvalidOperationException("ex-boom"),
            ExLogger(recorder),
            ExConfiguration());

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal(ExBuiltInUnknownMessage, ExFirstError(context));
        Assert.Contains(recorder.Entries, static entry => entry.Level == LogLevel.Error);
    }

    // ---------------------------------------------------------------------------------------------
    // UnauthorizedAccessException.
    // ---------------------------------------------------------------------------------------------

    // The source only sets the status: a 401 with no body lets the authentication middleware's own
    // challenge semantics apply. If a body appeared here it would break WWW-Authenticate flows.
    [Fact]
    public async Task InvokeAsync_UnauthorizedAccessException_Writes401WithNoBody()
    {
        var context = await ExRunAsync(new UnauthorizedAccessException("Access to the path 'x' is denied."));

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Empty(ExBody(context));
        Assert.Null(context.Response.ContentType);
    }

    // .NET throws UnauthorizedAccessException for file-system permission errors too, so a 401 that left
    // nothing in the log would make a broken directory permission look like a sign-in problem. Warning and
    // not Error, though: a caller arriving without a token is routine, and paging on it trains everyone to
    // ignore the error log. Asserting a single entry at Warning-or-above also pins "nothing was logged at Error".
    [Fact]
    public async Task InvokeAsync_UnauthorizedAccessException_LogsOneWarningCarryingTheExceptionAndTheRoute()
    {
        var recorder = new RecordingLoggerProvider();
        var exception = new UnauthorizedAccessException("Access to the path 'x' is denied.");

        await ExRunAsync(exception, logger: ExLogger(recorder));

        var warning = Assert.Single(recorder.Warnings);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Same(exception, warning.Exception);
        Assert.Contains($"{ExRequestMethod} {ExRequestPath}", warning.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // Unhandled exceptions.
    // ---------------------------------------------------------------------------------------------

    // The status mapping is the only thing standing between a repository's KeyNotFoundException and a
    // 500 for what is really a missing row.
    [Theory]
    [InlineData("key-not-found", 404)]
    [InlineData("argument", 400)]
    [InlineData("argument-null", 400)]
    [InlineData("argument-out-of-range", 400)]
    [InlineData("invalid-operation", 500)]
    [InlineData("timeout", 500)]
    [InlineData("plain", 500)]
    public async Task InvokeAsync_UnhandledException_MapsTheExceptionTypeToAStatusCode(string exceptionKind, int expectedStatusCode)
    {
        var context = await ExRunAsync(ExCreateException(exceptionKind));

        Assert.Equal(expectedStatusCode, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
        // The mapping moves the status and nothing else: whatever the type, the caller sees one generic
        // message. A 404 that started echoing the exception text would leak the key it failed on.
        Assert.Equal(ExBuiltInUnknownMessage, Assert.Single(ExErrors(context)));
    }

    // Exception messages routinely contain connection strings, file paths and SQL. None of that may
    // reach the caller.
    [Fact]
    public async Task InvokeAsync_UnhandledException_WritesTheGenericMessageInsteadOfTheExceptionMessage()
    {
        var context = await ExRunAsync(new InvalidOperationException("ex-secret-connection-string"));

        Assert.Equal(ExBuiltInUnknownMessage, Assert.Single(ExErrors(context)));
        Assert.DoesNotContain("ex-secret-connection-string", ExBody(context));
    }

    // The mapped 4xx statuses take the same generic body: the mapping changes the status only, never
    // the message, so a KeyNotFoundException cannot leak the key it failed on.
    [Fact]
    public async Task InvokeAsync_UnhandledExceptionMappedToAClientError_StillWritesTheGenericMessage()
    {
        var context = await ExRunAsync(new KeyNotFoundException("ex-secret-cache-key"));

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal(ExBuiltInUnknownMessage, ExFirstError(context));
        Assert.DoesNotContain("ex-secret-cache-key", ExBody(context));
    }

    // Unlike the handled arms, this one is a server fault and must be logged at Error with the stack
    // trace attached, or the generic 500 the caller sees leaves nothing to diagnose.
    [Fact]
    public async Task InvokeAsync_UnhandledException_LogsAtErrorCarryingTheExceptionAndTheRoute()
    {
        var recorder = new RecordingLoggerProvider();
        var exception = new InvalidOperationException("ex-boom");

        await ExRunAsync(exception, logger: ExLogger(recorder));

        var error = Assert.Single(recorder.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Same(exception, error.Exception);
        Assert.Contains($"{ExRequestMethod} {ExRequestPath}", error.Message);
    }

    // A handler that had already written part of a response before failing would otherwise produce a
    // body that is neither the payload nor valid JSON. The reset drops whatever was buffered.
    [Fact]
    public async Task InvokeAsync_UnhandledExceptionAfterAPartialWrite_DiscardsWhatThePipelineHadWritten()
    {
        var context = ExCreateContext();
        var middleware = new ExceptionHandlingMiddleware(
            async ctx =>
            {
                await ctx.Response.WriteAsync("ex-partial-output");
                throw new InvalidOperationException("ex-boom");
            },
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            ExConfiguration());

        await middleware.InvokeAsync(context);

        Assert.DoesNotContain("ex-partial-output", ExBody(context));
        Assert.Equal(ExBuiltInUnknownMessage, ExFirstError(context));
    }

    // The reset drops the headers too, not only the buffered bytes. A Content-Type left behind by a handler
    // that was half-way through streaming a CSV would describe the error body as something it is not, and a
    // stale custom header (a download filename, a cache directive) would be attached to a failure.
    [Fact]
    public async Task InvokeAsync_ExceptionAfterTheHandlerSetHeaders_ReplacesThemWithTheErrorHeaders()
    {
        var context = ExCreateContext();
        var middleware = new ExceptionHandlingMiddleware(
            ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status201Created;
                ctx.Response.ContentType = "text/csv";
                ctx.Response.Headers["X-Ex-Handler"] = "ex-stale";
                throw new NotFoundException("ex-order-missing");
            },
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            ExConfiguration());

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
        Assert.False(context.Response.Headers.ContainsKey("X-Ex-Handler"));
    }

    // ---------------------------------------------------------------------------------------------
    // The response has already started.
    // ---------------------------------------------------------------------------------------------

    // Once bytes are on the wire the headers cannot be rewritten: calling Clear() would throw a second
    // exception that tears the connection and hides the first one. Not throwing IS the contract here.
    [Theory]
    [InlineData("unhandled")]
    [InlineData("http-response")]
    [InlineData("bad-http-request")]
    [InlineData("unauthorized")]
    public async Task InvokeAsync_WhenTheResponseHasAlreadyStarted_KeepsTheOriginalStatusAndDoesNotThrow(string exceptionKind)
    {
        var recorder = new RecordingLoggerProvider();
        var context = ExCreateContext();
        context.Features.Set<IHttpResponseFeature>(new ExStartedResponseFeature
        {
            StatusCode = StatusCodes.Status206PartialContent
        });

        var middleware = new ExceptionHandlingMiddleware(
            _ => throw ExCreateArmException(exceptionKind),
            ExLogger(recorder),
            ExConfiguration());

        var thrown = await Record.ExceptionAsync(() => middleware.InvokeAsync(context));

        Assert.Null(thrown);
        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Empty(ExBody(context));
        Assert.Contains(recorder.Entries, entry => entry.Level == LogLevel.Error
            && entry.Message.Contains("Response already started"));
    }

    // ---------------------------------------------------------------------------------------------
    // traceId.
    // ---------------------------------------------------------------------------------------------

    // The README promises a traceId on every error body: it is what links a user's screenshot to the
    // log entry and the Sentry event.
    [Theory]
    [InlineData("unhandled")]
    [InlineData("http-response")]
    [InlineData("bad-http-request")]
    public async Task InvokeAsync_WithoutAnAmbientActivity_FallsBackToTheHttpContextTraceIdentifier(string exceptionKind)
    {
        using var noActivity = new ExNoAmbientActivityScope();

        var context = await ExRunAsync(ExCreateArmException(exceptionKind));

        Assert.Equal(ExTraceIdentifierValue, ExTraceId(context));
    }

    // When distributed tracing is on, the id in the body has to be the W3C trace id, otherwise it does not
    // match anything in the tracing backend. Every arm that writes a body has to agree on that: support
    // staff paste the same id whatever status code the caller happened to get.
    [Theory]
    [InlineData("unhandled")]
    [InlineData("http-response")]
    [InlineData("bad-http-request")]
    public async Task InvokeAsync_WithAnAmbientActivity_UsesTheActivityTraceId(string exceptionKind)
    {
        using var activity = new Activity("ex-error-body").SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();

        var context = await ExRunAsync(ExCreateArmException(exceptionKind));

        Assert.Equal(activity.TraceId.ToString(), ExTraceId(context));
    }

    // The documented body shape, read back the way a consumer would. Note that Web options match property
    // names case-insensitively, so this alone cannot see a camelCase slip — that is the next test's job.
    [Fact]
    public async Task InvokeAsync_ErrorBody_MatchesTheDocumentedErrorsResponseShape()
    {
        using var noActivity = new ExNoAmbientActivityScope();

        var context = await ExRunAsync(new NotFoundException("ex-order-missing"));

        var body = JsonSerializer.Deserialize<ErrorsResponse>(
            ExBody(context),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(body);
        Assert.Equal("ex-order-missing", Assert.Single(body!.Errors));
        Assert.Equal(ExTraceIdentifierValue, body.TraceId);
    }

    // The wire contract, pinned per arm and case-sensitively. ErrorsResponse spells its members in
    // PascalCase and only the camelCase serializer policy keeps the body spelled the way every deployed
    // client already reads it, so losing JsonSerializerDefaults.Web here would rename every client-visible
    // field at once — on an error path nobody exercises before production.
    [Theory]
    [InlineData("unhandled")]
    [InlineData("http-response")]
    [InlineData("bad-http-request")]
    public async Task InvokeAsync_ErrorBody_SpellsItsMembersInCamelCaseForEveryArm(string exceptionKind)
    {
        var root = ExJson(await ExRunAsync(ExCreateArmException(exceptionKind)));

        var memberNames = root.EnumerateObject()
            .Select(static member => member.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        // Exactly these two, sorted so the assertion does not depend on member order: the status code
        // travels as the HTTP status and must never appear in the payload as well.
        Assert.Equal(new[] { "errors", "traceId" }, memberNames);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("errors").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("traceId").ValueKind);
    }

    // The body is serialized, not concatenated. A message carrying quotes, a backslash or a newline — an
    // identifier echoed back from user input, say — has to arrive as the same string instead of tearing
    // the JSON in half and leaving the client with a parse error instead of a 404.
    [Fact]
    public async Task InvokeAsync_DomainMessageWithJsonSignificantCharacters_RoundTripsUnchanged()
    {
        const string message = "Order \"42\\7\" <b>was not</b> found\nanywhere";

        var context = await ExRunAsync(new NotFoundException(message));

        Assert.Equal(message, Assert.Single(ExErrors(context)));
    }

    // The configured messages are the whole point of the ErrorMessages section and the README's own example
    // is Arabic. If the body were written with anything but UTF-8, every non-Latin message would reach the
    // client as mojibake while all the ASCII tests above still passed.
    [Fact]
    public async Task InvokeAsync_WithANonAsciiConfiguredMessage_WritesItBackUnchanged()
    {
        var configuration = ExConfiguration(new Dictionary<string, string?>
        {
            ["ErrorMessages:Ar"] = ExArabicUnicodeMessage
        });

        var message = await ExUnknownMessageAsync(configuration, "ar");

        Assert.Equal(ExArabicUnicodeMessage, message);
    }

    // ---------------------------------------------------------------------------------------------
    // Language selection for the unknown-error message.
    // ---------------------------------------------------------------------------------------------

    // Browsers send Accept-Language in shapes the configuration file will never spell exactly:
    // region suffixes, quality lists, odd casing. Each has to resolve to a configured message.
    [Theory]
    [InlineData("En")]                  // exact key
    [InlineData("en")]                  // case-insensitive key match
    [InlineData("EN-us")]               // region suffix stripped to the primary language
    [InlineData("en-GB")]               // the documented "en-GB" -> "En" fallback
    [InlineData("English")]             // alias
    [InlineData("ENGLISH")]             // alias, case-insensitive
    [InlineData("en_US")]               // underscore form normalized to a dash
    [InlineData("de,en;q=0.7")]         // quality list, first configured language wins
    public async Task InvokeAsync_ResolvesEnglishAcceptLanguageVariantsToTheEnglishMessage(string acceptLanguage)
    {
        var message = await ExUnknownMessageAsync(ExLocalizedConfiguration(), acceptLanguage);

        Assert.Equal(ExEnglishMessage, message);
    }

    [Theory]
    [InlineData("Ar")]
    [InlineData("ar")]
    [InlineData("AR")]
    [InlineData("ar-SA")]
    [InlineData("ar_SA")]
    [InlineData("Arabic")]
    [InlineData("ARABIC")]
    [InlineData("fr-FR,ar;q=0.8")]      // French is not configured, so the next entry decides
    [InlineData("  ar ; q=0.8  ")]      // whitespace around the language and the quality value
    public async Task InvokeAsync_ResolvesArabicAcceptLanguageVariantsToTheArabicMessage(string acceptLanguage)
    {
        var message = await ExUnknownMessageAsync(ExLocalizedConfiguration(), acceptLanguage);

        Assert.Equal(ExArabicMessage, message);
    }

    // Accept-Language can arrive as several header lines; only looking at the first would ignore the
    // client's real preference.
    [Fact]
    public async Task InvokeAsync_WithSeveralAcceptLanguageHeaderValues_ConsidersAllOfThem()
    {
        var context = ExCreateContext();
        context.Request.Headers.AcceptLanguage = new StringValues(new[] { "fr-FR", "ar" });
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new InvalidOperationException("ex-boom"),
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            ExLocalizedConfiguration());

        await middleware.InvokeAsync(context);

        Assert.Equal(ExArabicMessage, ExFirstError(context));
    }

    // An unusable or unknown header must not produce an empty or missing message: it falls back to the
    // default language, which is English when nothing is configured.
    [Theory]
    [InlineData((string?)null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("de")]
    [InlineData("zh-Hans-CN")]
    [InlineData("*")]
    [InlineData(";q=0.8")]
    [InlineData(",")]
    public async Task InvokeAsync_WithAnUnusableAcceptLanguage_FallsBackToEnglish(string? acceptLanguage)
    {
        var message = await ExUnknownMessageAsync(ExLocalizedConfiguration(), acceptLanguage);

        Assert.Equal(ExEnglishMessage, message);
    }

    // All three spellings of the default-language key are documented/supported; a project that picked
    // the "wrong" one would silently get English.
    [Theory]
    [InlineData("Default")]
    [InlineData("DefaultLanguage")]
    [InlineData("DefaultAcceptLanguage")]
    public async Task InvokeAsync_WithoutAcceptLanguage_UsesTheConfiguredDefaultLanguage(string defaultKey)
    {
        var message = await ExUnknownMessageAsync(ExLocalizedConfiguration(defaultKey, "Ar"));

        Assert.Equal(ExArabicMessage, message);
    }

    // appsettings.json is written by hand, so the default language gets spelled however the developer
    // happens to think of it. Every spelling Accept-Language already accepts has to resolve here too, or
    // a project that wrote "ar-SA" silently ships English errors to an Arabic-only audience.
    [Theory]
    [InlineData("ar")]
    [InlineData("AR")]
    [InlineData("ar-SA")]
    [InlineData("ar_SA")]
    [InlineData("Arabic")]
    public async Task InvokeAsync_WithoutAcceptLanguage_ResolvesDefaultLanguageVariantsToTheSameMessage(string defaultValue)
    {
        var message = await ExUnknownMessageAsync(ExLocalizedConfiguration("Default", defaultValue));

        Assert.Equal(ExArabicMessage, message);
    }

    // The default is a fallback, not an override: a client that asked for a language it can read wins.
    [Fact]
    public async Task InvokeAsync_WithAcceptLanguage_PrefersItOverTheConfiguredDefault()
    {
        var message = await ExUnknownMessageAsync(ExLocalizedConfiguration("Default", "Ar"), "en");

        Assert.Equal(ExEnglishMessage, message);
    }

    // The default key names a language, it is not itself a message. If it were kept in the message map,
    // the last-resort fallback would answer a request with the literal word "Ar".
    [Theory]
    [InlineData("Default")]
    [InlineData("DefaultLanguage")]
    [InlineData("DefaultAcceptLanguage")]
    [InlineData("default")]             // configuration keys are case-insensitive, so the filter must be too
    public async Task InvokeAsync_DoesNotOfferTheDefaultLanguageKeyAsAMessage(string defaultKey)
    {
        var configuration = ExConfiguration(new Dictionary<string, string?>
        {
            [$"ErrorMessages:{defaultKey}"] = "Ar"
        });

        var message = await ExUnknownMessageAsync(configuration);

        Assert.Equal(ExBuiltInUnknownMessage, message);
    }

    // Both keys set is a configuration mistake; resolving it deterministically (first key listed wins)
    // beats depending on configuration-provider ordering.
    [Fact]
    public async Task InvokeAsync_WithSeveralDefaultLanguageKeys_UsesDefaultFirst()
    {
        var configuration = ExConfiguration(new Dictionary<string, string?>
        {
            ["ErrorMessages:Default"] = "En",
            ["ErrorMessages:DefaultLanguage"] = "Ar",
            ["ErrorMessages:En"] = ExEnglishMessage,
            ["ErrorMessages:Ar"] = ExArabicMessage
        });

        var message = await ExUnknownMessageAsync(configuration);

        Assert.Equal(ExEnglishMessage, message);
    }

    // A key left blank in appsettings.json (a placeholder nobody filled in) must not disable the
    // remaining default keys.
    [Fact]
    public async Task InvokeAsync_WithABlankDefaultKey_MovesOnToTheNextDefaultKey()
    {
        var configuration = ExConfiguration(new Dictionary<string, string?>
        {
            ["ErrorMessages:Default"] = "   ",
            ["ErrorMessages:DefaultLanguage"] = "Ar",
            ["ErrorMessages:En"] = ExEnglishMessage,
            ["ErrorMessages:Ar"] = ExArabicMessage
        });

        var message = await ExUnknownMessageAsync(configuration);

        Assert.Equal(ExArabicMessage, message);
    }

    // No default configured at all: English is the built-in preference.
    [Fact]
    public async Task InvokeAsync_WithoutAnyDefaultLanguage_PrefersTheEnglishMessage()
    {
        var message = await ExUnknownMessageAsync(ExLocalizedConfiguration());

        Assert.Equal(ExEnglishMessage, message);
    }

    // A project that never configured ErrorMessages still has to get a readable message.
    [Fact]
    public async Task InvokeAsync_WithoutAnErrorMessagesSection_UsesTheBuiltInMessage()
    {
        var message = await ExUnknownMessageAsync(ExConfiguration());

        Assert.Equal(ExBuiltInUnknownMessage, message);
    }

    // A section that exists but has no children (a scalar left over from an edit) is not a message map.
    [Fact]
    public async Task InvokeAsync_WithAnErrorMessagesSectionThatHasNoChildren_UsesTheBuiltInMessage()
    {
        var configuration = ExConfiguration(new Dictionary<string, string?>
        {
            ["ErrorMessages"] = "En"
        });

        var message = await ExUnknownMessageAsync(configuration);

        Assert.Equal(ExBuiltInUnknownMessage, message);
    }

    // Blank values are stripped, so an unfinished translation file cannot make the API answer errors
    // with an empty string.
    [Fact]
    public async Task InvokeAsync_WhenEveryConfiguredMessageIsBlank_UsesTheBuiltInMessage()
    {
        var configuration = ExConfiguration(new Dictionary<string, string?>
        {
            ["ErrorMessages:En"] = "",
            ["ErrorMessages:Ar"] = "   "
        });

        var message = await ExUnknownMessageAsync(configuration, "en");

        Assert.Equal(ExBuiltInUnknownMessage, message);
    }

    // Last-resort fallback: a project that only ships one language gets that language, rather than the
    // built-in English text the team deliberately replaced.
    [Theory]
    [InlineData((string?)null)]
    [InlineData("en")]
    [InlineData("de")]
    public async Task InvokeAsync_WithOnlyOneConfiguredLanguageThatMatchesNothing_UsesThatMessageAnyway(string? acceptLanguage)
    {
        var configuration = ExConfiguration(new Dictionary<string, string?>
        {
            ["ErrorMessages:Fr"] = ExFrenchMessage
        });

        var message = await ExUnknownMessageAsync(configuration, acceptLanguage);

        Assert.Equal(ExFrenchMessage, message);
    }

    // The middleware is a singleton for the life of the app and snapshots its messages once, precisely
    // so it does not re-read and re-allocate the configuration on every error. Reloading is therefore
    // not supported, and a test that asserted the opposite would be asserting a performance bug.
    [Fact]
    public async Task InvokeAsync_IgnoresErrorMessagesChangedAfterTheMiddlewareWasConstructed()
    {
        var configuration = ExConfiguration(new Dictionary<string, string?>
        {
            ["ErrorMessages:En"] = ExEnglishMessage
        });
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new InvalidOperationException("ex-boom"),
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            configuration);

        configuration["ErrorMessages:En"] = "ex-changed-after-construction";

        var context = ExCreateContext("en");
        await middleware.InvokeAsync(context);

        Assert.Equal(ExEnglishMessage, ExFirstError(context));
    }

    // One middleware instance serves every request the app ever handles. Resolving the language into a
    // field — an obvious-looking "cache the message we just built" optimisation — would answer the next
    // caller in the previous caller's language.
    [Fact]
    public async Task InvokeAsync_OnOneInstanceServingTwoRequests_ResolvesTheLanguagePerRequest()
    {
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new InvalidOperationException("ex-boom"),
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            ExLocalizedConfiguration());

        var arabic = ExCreateContext("ar");
        await middleware.InvokeAsync(arabic);

        var english = ExCreateContext("en");
        await middleware.InvokeAsync(english);

        Assert.Equal(ExArabicMessage, ExFirstError(arabic));
        Assert.Equal(ExEnglishMessage, ExFirstError(english));
    }

    // ---------------------------------------------------------------------------------------------
    // The exception types the middleware translates.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void NotFoundException_CarriesNotFoundAndTheMessage()
    {
        var exception = new NotFoundException("ex-order-missing");

        Assert.Equal(HttpStatusCode.NotFound, exception.HttpStatusCode);
        Assert.Equal("ex-order-missing", Assert.Single(exception.Errors));
        Assert.Equal("ex-order-missing", exception.Message);
    }

    [Fact]
    public void BadRequestException_CarriesBadRequestAndTheMessage()
    {
        var exception = new BadRequestException("ex-quantity-invalid");

        Assert.Equal(HttpStatusCode.BadRequest, exception.HttpStatusCode);
        Assert.Equal("ex-quantity-invalid", Assert.Single(exception.Errors));
        Assert.Equal("ex-quantity-invalid", exception.Message);
    }

    // With no messages there is nothing to join, and an exception with an empty Message is useless in a
    // log; the status name is the only description left.
    [Fact]
    public void HttpResponseException_WithoutErrors_UsesTheStatusCodeNameAsTheMessage()
    {
        var exception = new HttpResponseException(new ErrorResponse(HttpStatusCode.Forbidden, []));

        Assert.Equal(nameof(HttpStatusCode.Forbidden), exception.Message);
        Assert.Empty(exception.Errors);
    }

    // Message is what a log line and a Sentry issue title show. Keeping only the first of several messages
    // there would leave whoever is debugging a validation failure seeing one of its problems.
    [Fact]
    public void HttpResponseException_WithSeveralErrors_JoinsThemIntoTheMessage()
    {
        var exception = new HttpResponseException(
            new ErrorResponse(HttpStatusCode.BadRequest, ["ex-first", "ex-second"]));

        Assert.Equal("ex-first; ex-second", exception.Message);
        Assert.Equal(new[] { "ex-first", "ex-second" }, exception.Errors);
    }

    // The middleware reads Errors and HttpStatusCode off the exception rather than the ErrorResponse, so
    // both have to stay views over the response the thrower built instead of copies taken at construction.
    [Fact]
    public void HttpResponseException_ExposesTheErrorResponseItWasGiven()
    {
        var errorResponse = new ErrorResponse(HttpStatusCode.Conflict, ["ex-duplicate"]);

        var exception = new HttpResponseException(errorResponse);

        Assert.Same(errorResponse, exception.ErrorResponse);
        Assert.Equal(HttpStatusCode.Conflict, exception.HttpStatusCode);
        Assert.Same(errorResponse.Errors, exception.Errors);
    }

    // The guard fires while building the message, so a null ErrorResponse fails at the throw site
    // instead of producing a NullReferenceException inside the middleware later.
    [Fact]
    public void HttpResponseException_WithoutAnErrorResponse_ThrowsArgumentNullException()
    {
        var exception = Record.Exception(() => { _ = new HttpResponseException(null!); });

        Assert.IsType<ArgumentNullException>(exception);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    private static DefaultHttpContext ExCreateContext(string? acceptLanguage = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = ExRequestMethod;
        context.Request.Path = ExRequestPath;
        context.TraceIdentifier = ExTraceIdentifierValue;
        context.Response.Body = new MemoryStream();

        if (acceptLanguage is not null)
            context.Request.Headers.AcceptLanguage = acceptLanguage;

        return context;
    }

    private static async Task<DefaultHttpContext> ExRunAsync(
        Exception exception,
        IConfiguration? configuration = null,
        string? acceptLanguage = null,
        ILogger<ExceptionHandlingMiddleware>? logger = null)
    {
        var context = ExCreateContext(acceptLanguage);
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw exception,
            logger ?? NullLogger<ExceptionHandlingMiddleware>.Instance,
            configuration ?? ExConfiguration());

        await middleware.InvokeAsync(context);

        return context;
    }

    /// <summary>The single error message an unhandled exception produced for the given configuration.</summary>
    private static async Task<string> ExUnknownMessageAsync(IConfiguration configuration, string? acceptLanguage = null)
    {
        var context = await ExRunAsync(new InvalidOperationException("ex-boom"), configuration, acceptLanguage);

        return ExFirstError(context);
    }

    private static ILogger<ExceptionHandlingMiddleware> ExLogger(RecordingLoggerProvider recorder)
        => new Logger<ExceptionHandlingMiddleware>(recorder);

    private static IConfiguration ExConfiguration(Dictionary<string, string?>? values = null)
    {
        var builder = new ConfigurationBuilder();

        if (values is not null)
            builder.AddInMemoryCollection(values);

        return builder.Build();
    }

    /// <summary>English and Arabic messages, optionally with one of the default-language keys set.</summary>
    private static IConfiguration ExLocalizedConfiguration(string? defaultKey = null, string? defaultValue = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ErrorMessages:En"] = ExEnglishMessage,
            ["ErrorMessages:Ar"] = ExArabicMessage
        };

        if (defaultKey is not null)
            values[$"ErrorMessages:{defaultKey}"] = defaultValue;

        return ExConfiguration(values);
    }

    private static Exception ExCreateException(string exceptionKind) => exceptionKind switch
    {
        "key-not-found" => new KeyNotFoundException("ex-key-not-found-detail"),
        "argument" => new ArgumentException("ex-argument-detail"),
        "argument-null" => new ArgumentNullException("exParameter"),
        "argument-out-of-range" => new ArgumentOutOfRangeException("exParameter"),
        "invalid-operation" => new InvalidOperationException("ex-invalid-operation-detail"),
        "timeout" => new TimeoutException("ex-timeout-detail"),
        "plain" => new Exception("ex-plain-detail"),
        _ => throw new ArgumentOutOfRangeException(nameof(exceptionKind), exceptionKind, "Unknown exception kind.")
    };

    /// <summary>One representative exception per catch arm of <c>InvokeAsync</c>.</summary>
    private static Exception ExCreateArmException(string exceptionKind) => exceptionKind switch
    {
        "unhandled" => new InvalidOperationException("ex-boom"),
        "http-response" => new NotFoundException("ex-order-missing"),
        "bad-http-request" => new BadHttpRequestException("ex-malformed-json-body", StatusCodes.Status400BadRequest),
        "unauthorized" => new UnauthorizedAccessException("ex-denied"),
        _ => throw new ArgumentOutOfRangeException(nameof(exceptionKind), exceptionKind, "Unknown exception kind.")
    };

    private static string ExBody(HttpContext context)
        => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private static JsonElement ExJson(HttpContext context)
        => JsonDocument.Parse(ExBody(context)).RootElement.Clone();

    private static string[] ExErrors(HttpContext context)
        => ExJson(context)
            .GetProperty("errors")
            .EnumerateArray()
            .Select(static error => error.GetString()!)
            .ToArray();

    private static string ExFirstError(HttpContext context) => ExErrors(context)[0];

    private static string ExTraceId(HttpContext context) => ExJson(context).GetProperty("traceId").GetString()!;

    /// <summary>A response feature that reports bytes already on the wire, as a real server would.</summary>
    private sealed class ExStartedResponseFeature : IHttpResponseFeature
    {
        public Stream Body { get; set; } = new MemoryStream();
        public bool HasStarted => true;
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public string? ReasonPhrase { get; set; }
        public int StatusCode { get; set; } = StatusCodes.Status200OK;

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }
    }

    /// <summary>Clears <see cref="Activity.Current"/> for the duration of a test and restores it after.</summary>
    private sealed class ExNoAmbientActivityScope : IDisposable
    {
        private readonly Activity? previous = Activity.Current;

        public ExNoAmbientActivityScope() => Activity.Current = null;

        public void Dispose() => Activity.Current = previous;
    }
}
