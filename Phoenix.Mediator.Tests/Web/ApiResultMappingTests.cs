using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Exceptions;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Tests.Infrastructure;
using Phoenix.Mediator.Web;
using Phoenix.Mediator.Web.Middlewares;
using Phoenix.Mediator.Wrappers;
using Xunit;

namespace Phoenix.Mediator.Tests;

/// <summary>
/// Result mapping (what the endpoint helpers make of what an endpoint, or <c>sender.Send(...)</c>, returns) and the
/// response wrappers (<c>SingleResponse</c>, <c>MultiResponse</c>, <c>ErrorResponse</c>, <c>ErrorsResponse</c>,
/// <c>HttpResponseException</c>).
/// <para>
/// Mapping assertions go through a real request, so the status code, content type and body are the ones a client
/// actually receives — a result type alone says nothing about what gets written.
/// </para>
/// </summary>
public sealed class MappingApiResultTests
{
    // ---------------------------------------------------------------------------------------------
    // Endpoint helpers: how a returned value is written
    // ---------------------------------------------------------------------------------------------

    // The one documented error body is produced by two different code paths with two different serializers. The
    // exception middleware pins camelCase; a RETURNED ErrorResponse goes through Results.Json and so follows whatever
    // naming policy the app configured. An app that turns the policy off therefore serves {"Errors":[...]} from a
    // handler that RETURNS an ErrorResponse and {"errors":[...]} from one that THROWS on the very next endpoint, and
    // only the second matches the Produces<ErrorsResponse> schema every endpoint advertises. Pinned rather than asserted
    // as desirable: which serializer should win is an API-contract decision, and this test is here so the divergence
    // cannot be changed or shipped unnoticed.
    [Fact]
    public async Task ErrorBody_IsCasedByTheAppPolicyWhenReturnedButAlwaysCamelCaseWhenThrown()
    {
        await using var app = await StartAppAsync(
            null,
            static app =>
            {
                app.Get("returned", static () => new ErrorResponse(HttpStatusCode.NotFound, ["gone"]));
                app.Get("thrown", static () => { throw new NotFoundException("gone"); });
            },
            static services => services.ConfigureHttpJsonOptions(static options => options.SerializerOptions.PropertyNamingPolicy = null));

        var returned = await GetAsync(app, "returned");
        var thrown = await GetAsync(app, "thrown");

        Assert.Equal(StatusCodes.Status404NotFound, returned.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, thrown.StatusCode);

        // Same status, same meaning, different field names.
        Assert.Contains("\"Errors\"", returned.Body, StringComparison.Ordinal);
        Assert.Contains("\"errors\"", thrown.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Errors\"", thrown.Body, StringComparison.Ordinal);
    }

    // EmptyResponseStatusCode is an enum, so any int can be cast into it. The helpers read the option when the endpoint
    // is mapped, so a value they cannot write fails at startup rather than on the first empty response.
    [Fact]
    public async Task UndefinedEmptyResponseStatusCode_FailsWhenTheEndpointIsMapped()
    {
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => StartAppAsync(
            (EmptyResponseStatusCode)201,
            static app => app.Get("void", static (ISender sender, CancellationToken ct) => sender.Send(new MappingVoidRequest(), ct))));

        Assert.Contains("200 OK", exception.Message);
        Assert.Contains("204 No Content", exception.Message);
    }

    // Handlers and pipeline behaviors are allowed to return Results.* directly; wrapping such a result in another one
    // would lose its status, headers and body. That holds even when the endpoint hands it over typed as object, and
    // whatever empty status is configured.
    [Fact]
    public async Task ReturnedIResult_IsPassedThroughWithTheStatusItWritesItself()
    {
        var custom = new MappingCustomResult();
        await using var app = await StartAppAsync(EmptyResponseStatusCode.Ok, app =>
        {
            app.Get("accepted", static () => (object)Results.Accepted("/mapping/queued"));
            app.Get("custom", () => (object)custom);
        });

        Assert.Equal(StatusCodes.Status202Accepted, (await GetAsync(app, "accepted")).StatusCode);
        Assert.Equal(StatusCodes.Status418ImATeapot, (await GetAsync(app, "custom")).StatusCode);
        Assert.True(custom.WasExecuted);
    }

    // A handler that returns an ErrorResponse instead of throwing still has to produce the real HTTP status; returning
    // 200 with an error body is the classic way clients end up ignoring failures. 599 is in here because the status is
    // cast through unchecked: an app using a code outside the HttpStatusCode enum must get that code, not a normalized 500.
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, StatusCodes.Status400BadRequest)]
    [InlineData(HttpStatusCode.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(HttpStatusCode.Conflict, StatusCodes.Status409Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity, StatusCodes.Status422UnprocessableEntity)]
    [InlineData(HttpStatusCode.InternalServerError, StatusCodes.Status500InternalServerError)]
    [InlineData((HttpStatusCode)599, 599)]
    public async Task ReturnedErrorResponse_UsesItsHttpStatusCode(HttpStatusCode httpStatusCode, int expectedStatusCode)
    {
        await using var app = await StartAppAsync(null, app => app.Get("error", () => new ErrorResponse(httpStatusCode, ["boom"])));

        var answer = await GetAsync(app, "error");

        Assert.Equal(expectedStatusCode, answer.StatusCode);
        Assert.Equal("boom", answer.Json.GetProperty("errors")[0].GetString());
    }

    // Clients parse one error shape. A RETURNED ErrorResponse has to come back exactly as the middleware writes a thrown
    // one — same status, content type, members and messages — or half the failures in an app come back in a shape the
    // client cannot read, which half depending on whether the handler threw or returned.
    [Fact]
    public async Task ReturnedErrorResponse_HasTheSameBodyAsAThrownOne()
    {
        await using var app = await StartAppAsync(null, static app =>
        {
            app.Get("returned", static () => new ErrorResponse(HttpStatusCode.BadRequest, ["first", "second"]));
            app.Get("thrown", static () => { throw new HttpResponseException(new ErrorResponse(HttpStatusCode.BadRequest, ["first", "second"])); });
        });

        var returned = await GetAsync(app, "returned");
        var thrown = await GetAsync(app, "thrown");

        Assert.Equal(thrown.StatusCode, returned.StatusCode);
        Assert.Equal(thrown.ContentType, returned.ContentType);
        Assert.Equal(MemberNames(thrown.Json), MemberNames(returned.Json));
        Assert.Equal(thrown.Json.GetProperty("errors").GetRawText(), returned.Json.GetProperty("errors").GetRawText());
        Assert.False(string.IsNullOrEmpty(returned.Json.GetProperty("traceId").GetString()));
    }

    // The other half of the trace id contract: an app with no logging or tracing has no request Activity. The body then
    // carries the request's TraceIdentifier, as the exception middleware's does, rather than a null the caller can't
    // quote back.
    [Fact]
    public async Task ReturnedErrorResponse_WithoutARequestActivity_StillCarriesATraceId()
    {
        await using var app = await StartAppAsync(
            null,
            static app => app.Get("error", static () => new ErrorResponse(HttpStatusCode.BadRequest, ["boom"])),
            static services => services.AddLogging(static logging => logging.ClearProviders()));

        var answer = await GetAsync(app, "error");

        Assert.Equal(StatusCodes.Status400BadRequest, answer.StatusCode);
        Assert.False(string.IsNullOrEmpty(answer.Json.GetProperty("traceId").GetString()));
    }

    // ErrorResponse is an unsealed record, so apps subclass it to carry an error code or a field name next to the
    // messages. A subclass still maps to its own status and to the standard body — the extra members are not part of
    // the wire contract.
    [Fact]
    public async Task ReturnedDerivedErrorResponse_IsWrittenLikeTheBaseErrorResponse()
    {
        await using var app = await StartAppAsync(null, static app => app.Get("error", static () => new MappingDerivedErrorResponse("CARD_DECLINED")));

        var answer = await GetAsync(app, "error");

        Assert.Equal(StatusCodes.Status402PaymentRequired, answer.StatusCode);
        Assert.Equal("card declined", answer.Json.GetProperty("errors")[0].GetString());
        Assert.Equal(new[] { "errors", "traceId" }, MemberNames(answer.Json));
    }

    // An error with no messages is still an error: the status has to survive even when there is nothing to say, instead
    // of collapsing into an empty 200.
    [Fact]
    public async Task ReturnedErrorResponseWithoutErrors_WritesAnEmptyErrorsArray()
    {
        await using var app = await StartAppAsync(null, static app => app.Get("error", static () => new ErrorResponse(HttpStatusCode.Forbidden, [])));

        var answer = await GetAsync(app, "error");

        Assert.Equal(StatusCodes.Status403Forbidden, answer.StatusCode);
        Assert.Empty(answer.Json.GetProperty("errors").EnumerateArray());
    }

    // ErrorsResponse is the BODY type, ErrorResponse is the ERROR type. Returning the body type produces an error-looking
    // payload with a 200 status — worth pinning so the trap is visible.
    [Fact]
    public async Task ReturnedErrorsResponse_IsWrittenAsAPlainValueWith200()
    {
        await using var app = await StartAppAsync(null, static app => app.Get("body", static () => new ErrorsResponse(["boom"], "trace-1")));

        var answer = await GetAsync(app, "body");

        Assert.Equal(StatusCodes.Status200OK, answer.StatusCode);
        Assert.Equal("boom", answer.Json.GetProperty("errors")[0].GetString());
    }

    // A value typed as object (what ISender.Send(object) returns) has to be serialized as its runtime type. Serializing
    // the declared type would write "{}" for every such response.
    [Fact]
    public async Task ReturnedValueTypedAsObject_WritesTheRuntimeTypesProperties()
    {
        await using var app = await StartAppAsync(null, static app => app.Get("value", static () => (object)new MappingPayload("echoed", 7)));

        var answer = await GetAsync(app, "value");

        Assert.Equal(StatusCodes.Status200OK, answer.StatusCode);
        Assert.Equal("application/json", answer.ContentType);
        Assert.Equal("echoed", answer.Json.GetProperty("name").GetString());
        Assert.Equal(7, answer.Json.GetProperty("count").GetInt32());
    }

    // "Always return JSON so Swagger/clients consistently get the documented content-type/schema". A string comes back
    // quoted as JSON, not as text/plain.
    [Theory]
    [InlineData(42, "42")]
    [InlineData(true, "true")]
    [InlineData(1.5, "1.5")]
    [InlineData("hello", "\"hello\"")]
    public async Task ReturnedScalar_IsAlwaysWrittenAsJson(object value, string expectedBody)
    {
        await using var app = await StartAppAsync(null, app => app.Get("value", () => value));

        var answer = await GetAsync(app, "value");

        Assert.Equal(StatusCodes.Status200OK, answer.StatusCode);
        Assert.Equal("application/json", answer.ContentType);
        Assert.Equal(expectedBody, answer.Body);
    }

    // "No rows" is a successful 200 with an empty array, not an empty response: a client that switches on the status
    // must not treat an empty page as "nothing was returned".
    [Fact]
    public async Task ReturnedCollections_AreWrittenAsJsonArrays_EvenWhenEmpty()
    {
        await using var app = await StartAppAsync(null, static app =>
        {
            app.Get("some", static () => new[] { 1, 2, 3 });
            app.Get("none", static () => Array.Empty<int>());
        });

        var some = await GetAsync(app, "some");
        var none = await GetAsync(app, "none");

        Assert.Equal((StatusCodes.Status200OK, "[1,2,3]"), (some.StatusCode, some.Body));
        Assert.Equal((StatusCodes.Status200OK, "[]"), (none.StatusCode, none.Body));
    }

    // "Found nothing" is often written as new SingleResponse<T>(null). The WRAPPER is not null, so this is a 200 with
    // {"result":null} and never the empty response — an endpoint that wants 204 has to return null itself. Pinned
    // because the two spellings look identical at the call site.
    [Fact]
    public async Task ReturnedSingleResponse_WritesTheWrappedResult_EvenANullOne()
    {
        await using var app = await StartAppAsync(null, static app =>
        {
            app.Get("wrapped", static () => new SingleResponse<string>("wrapped"));
            app.Get("wrapped-null", static () => new SingleResponse<string>(null!));
        });

        Assert.Equal("wrapped", (await GetAsync(app, "wrapped")).Json.GetProperty("result").GetString());

        var wrappedNull = await GetAsync(app, "wrapped-null");
        Assert.Equal(StatusCodes.Status200OK, wrappedNull.StatusCode);
        Assert.Equal("{\"result\":null}", wrappedNull.Body);
    }

    // The paging wrapper is what every list endpoint returns; its computed page count has to reach the client, not just
    // the raw constructor arguments.
    [Fact]
    public async Task ReturnedMultiResponse_WritesTheDataAndThePagingNumbers()
    {
        await using var app = await StartAppAsync(null, static app =>
            app.Get("page", static () => new MultiResponse<string>(["a", "b"], totalCount: 5, pageSize: 2)));

        var answer = await GetAsync(app, "page");

        Assert.Equal(StatusCodes.Status200OK, answer.StatusCode);
        Assert.Equal(2, answer.Json.GetProperty("data").GetArrayLength());
        Assert.Equal(5, answer.Json.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, answer.Json.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, answer.Json.GetProperty("pagesCount").GetInt32());
    }

    // ---------------------------------------------------------------------------------------------
    // Endpoint helpers: an endpoint that returns sender.Send(...)
    // ---------------------------------------------------------------------------------------------

    // A void request: the delegate returns a plain Task, which ASP.NET Core alone would answer with 200.
    [Theory]
    [InlineData(EmptyResponseStatusCode.Ok, StatusCodes.Status200OK)]
    [InlineData(EmptyResponseStatusCode.NoContent, StatusCodes.Status204NoContent)]
    public async Task Endpoint_VoidRequest_AnswersWithTheConfiguredEmptyStatus(EmptyResponseStatusCode configured, int expectedStatusCode)
    {
        await using var app = await StartAppAsync(configured, static app =>
            app.Get("void", static (ISender sender, CancellationToken ct) => sender.Send(new MappingVoidRequest(), ct)));

        AssertEmpty(await GetAsync(app, "void"), expectedStatusCode);
    }

    // A handler that legitimately has nothing to return (a lookup that found nothing to send back) maps the same way a
    // void request does, through every Send overload an endpoint can bind to.
    [Theory]
    [InlineData(EmptyResponseStatusCode.Ok, StatusCodes.Status200OK)]
    [InlineData(EmptyResponseStatusCode.NoContent, StatusCodes.Status204NoContent)]
    public async Task Endpoint_NullHandlerResult_AnswersWithTheConfiguredEmptyStatus(EmptyResponseStatusCode configured, int expectedStatusCode)
    {
        await using var app = await StartAppAsync(configured, static app =>
        {
            app.Get("inferred", static (ISender sender, CancellationToken ct) => sender.Send(new MappingNullResponseRequest(), ct));
            app.Get("typed", static (ISender sender, CancellationToken ct) => sender.Send<MappingNullResponseRequest, string?>(new MappingNullResponseRequest(), ct));
            app.Get("object", static (ISender sender, CancellationToken ct) => sender.Send((object)new MappingNullResponseRequest(), ct));
        });

        foreach (var path in new[] { "inferred", "typed", "object" })
            AssertEmpty(await GetAsync(app, path), expectedStatusCode);
    }

    // The default has to stay 204: apps that never touch MediatorOptions rely on it.
    [Fact]
    public async Task Endpoint_WithoutConfiguredOptions_AnswersAnEmptyResultWithNoContent()
    {
        await using var app = await StartAppAsync(null, static app =>
            app.Get("void", static (ISender sender, CancellationToken ct) => sender.Send(new MappingVoidRequest(), ct)));

        AssertEmpty(await GetAsync(app, "void"), StatusCodes.Status204NoContent);
    }

    // An empty Nullable<int> boxes to null on its way through the filter and maps to the empty response, instead of
    // reaching the serializer as a 200 with the literal "null"...
    [Fact]
    public async Task Endpoint_EmptyNullableResponse_AnswersWithTheConfiguredEmptyStatus()
    {
        await using var app = await StartAppAsync(EmptyResponseStatusCode.Ok, static app =>
            app.Get("empty", static (ISender sender, CancellationToken ct) => sender.Send(new MappingNullableIntRequest(), ct)));

        AssertEmpty(await GetAsync(app, "empty"), StatusCodes.Status200OK);
    }

    // ...and the mirror image, the one a "did the handler return anything?" check gets wrong: 0 is a value.
    [Fact]
    public async Task Endpoint_ZeroResponse_Answers200WithTheValue()
    {
        await using var app = await StartAppAsync(null, static app =>
            app.Get("zero", static (ISender sender, CancellationToken ct) => sender.Send(new MappingNullableIntRequest { Value = 0 }, ct)));

        var answer = await GetAsync(app, "zero");

        Assert.Equal(StatusCodes.Status200OK, answer.StatusCode);
        Assert.Equal("0", answer.Body);
    }

    [Fact]
    public async Task Endpoint_Payload_AnswersWithJsonThroughEveryOverload()
    {
        await using var app = await StartAppAsync(null, static app =>
        {
            app.Get("inferred", static (ISender sender, CancellationToken ct) => sender.Send(new MappingPayloadRequest { Value = "inferred" }, ct));
            app.Get("typed", static (ISender sender, CancellationToken ct) => sender.Send<MappingPayloadRequest, MappingPayload>(new MappingPayloadRequest { Value = "typed" }, ct));
            app.Get("object", static (ISender sender, CancellationToken ct) => sender.Send((object)new MappingPayloadRequest { Value = "object" }, ct));
        });

        foreach (var path in new[] { "inferred", "typed", "object" })
        {
            var answer = await GetAsync(app, path);

            Assert.Equal(StatusCodes.Status200OK, answer.StatusCode);
            Assert.Equal("application/json", answer.ContentType);
            Assert.Equal(path, answer.Json.GetProperty("name").GetString());
        }
    }

    // ASP.NET Core writes a returned string as text/plain; the documented responses are JSON.
    [Fact]
    public async Task Endpoint_StringResponse_AnswersWithAJsonString()
    {
        await using var app = await StartAppAsync(null, static app =>
            app.Get("text", static (ISender sender, CancellationToken ct) => sender.Send(new MappingTextRequest(), ct)));

        var answer = await GetAsync(app, "text");

        Assert.Equal(StatusCodes.Status200OK, answer.StatusCode);
        Assert.Equal("application/json", answer.ContentType);
        Assert.Equal("\"hello\"", answer.Body);
    }

    // A handler that needs a status the mapping does not produce (202, a redirect, a file) returns an IResult, which
    // reaches the framework untouched.
    [Fact]
    public async Task Endpoint_HandlerReturningAnIResult_PassesItThrough()
    {
        await using var app = await StartAppAsync(null, static app =>
            app.Get("result", static (ISender sender, CancellationToken ct) => sender.Send(new MappingResultRequest(), ct)));

        var answer = await GetAsync(app, "result");

        Assert.Equal(StatusCodes.Status202Accepted, answer.StatusCode);
        Assert.Equal("handler-chosen-result", answer.Json.GetProperty("name").GetString());
    }

    // Plain Send used to write a returned ErrorResponse as a 200 with the record as its body.
    [Fact]
    public async Task Endpoint_HandlerReturningAnErrorResponse_AnswersWithItsStatusAndTheErrorsBody()
    {
        await using var app = await StartAppAsync(null, static app =>
            app.Get("error", static (ISender sender, CancellationToken ct) => sender.Send(new MappingErrorResponseRequest(), ct)));

        var answer = await GetAsync(app, "error");

        Assert.Equal(StatusCodes.Status409Conflict, answer.StatusCode);
        Assert.Equal(new[] { "errors", "traceId" }, answer.Json.EnumerateObject().Select(static p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("already exists", answer.Json.GetProperty("errors")[0].GetString());
        Assert.False(string.IsNullOrEmpty(answer.Json.GetProperty("traceId").GetString()));
    }

    // A behavior may answer without calling the handler (authorization, a cache hit, any short circuit). What reaches
    // the client is what the PIPELINE produced, so an ErrorResponse from a behavior has to become a real error status —
    // not a 200 carrying an error-shaped body, and not the handler's value.
    [Fact]
    public async Task Endpoint_BehaviorShortCircuitingWithAnErrorResponse_AnswersWithTheBehaviorsError()
    {
        await using var app = await StartAppAsync(null, static app =>
            app.Get("short-circuit", static (ISender sender, CancellationToken ct) => sender.Send(new MappingShortCircuitRequest(), ct)));

        var answer = await GetAsync(app, "short-circuit");

        Assert.Equal(StatusCodes.Status403Forbidden, answer.StatusCode);
        Assert.Equal("not allowed", Assert.Single(answer.Json.GetProperty("errors").EnumerateArray()).GetString());
    }

    // Every list endpoint sends an IPagedRequest<T>, which reaches IRequest<MultiResponse<T>> only through an inherited
    // interface. Dispatch and the paging body have to survive that indirection together.
    [Fact]
    public async Task Endpoint_PagedRequest_AnswersWithTheMultiResponsePagingBody()
    {
        await using var app = await StartAppAsync(null, static app =>
            app.Get("paged", static (ISender sender, CancellationToken ct) => sender.Send(new MappingPagedRequest { PageNum = 2, PageSize = 2 }, ct)));

        var answer = await GetAsync(app, "paged");

        Assert.Equal(StatusCodes.Status200OK, answer.StatusCode);
        Assert.Equal(["c", "d"], answer.Json.GetProperty("data").EnumerateArray().Select(static item => item.GetString()));
        Assert.Equal(5, answer.Json.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, answer.Json.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, answer.Json.GetProperty("pagesCount").GetInt32());
    }

    // A request held as its interface (a List<IRequest>, a factory return value) is dispatched by its runtime type, and
    // its result maps like any other.
    [Fact]
    public async Task Endpoint_RequestsTypedAsTheRequestInterface_AreMappedLikeAnyOther()
    {
        await using var app = await StartAppAsync(EmptyResponseStatusCode.Ok, static app =>
        {
            app.Get("query", static (ISender sender, CancellationToken ct) =>
            {
                IRequest<MappingPayload> request = new MappingPayloadRequest { Value = "via-interface" };
                return sender.Send(request, ct);
            });
            app.Get("command", static (ISender sender, CancellationToken ct) =>
            {
                IRequest command = new MappingVoidRequest();
                return sender.Send(command, ct);
            });
        });

        Assert.Equal("via-interface", (await GetAsync(app, "query")).Json.GetProperty("name").GetString());
        AssertEmpty(await GetAsync(app, "command"), StatusCodes.Status200OK);
    }

    // The empty status comes from the options, not from the sender, so an app that decorates ISender keeps it.
    [Fact]
    public async Task Endpoint_WithADecoratedSender_StillAnswersWithTheConfiguredEmptyStatus()
    {
        await using var app = await StartAppAsync(
            EmptyResponseStatusCode.Ok,
            static app => app.Get("void", static (ISender sender, CancellationToken ct) => sender.Send(new MappingVoidRequest(), ct)),
            static services => services.AddScoped<ISender>(static provider =>
                new MappingPlainSenderWrapper(provider.GetRequiredService<global::Phoenix.Mediator.Mediator.Mediator>())));

        AssertEmpty(await GetAsync(app, "void"), StatusCodes.Status200OK);
    }

    // The filter must not catch: a swallowed exception would turn every failure into a 200/204 and the exception-
    // handling middleware would never see it. A void request is the one most likely to come back as a 204.
    [Fact]
    public async Task Endpoint_HandlerExceptions_StillReachTheExceptionMiddleware()
    {
        await using var app = await StartAppAsync(null, static app =>
        {
            app.Get("throwing-void", static (ISender sender, CancellationToken ct) => sender.Send(new MappingThrowingVoidRequest(), ct));
            app.Get("throwing", static (ISender sender, CancellationToken ct) => sender.Send(new MappingThrowingRequest(), ct));
        });

        Assert.Equal(StatusCodes.Status404NotFound, (await GetAsync(app, "throwing-void")).StatusCode);
        Assert.Equal(StatusCodes.Status500InternalServerError, (await GetAsync(app, "throwing")).StatusCode);
    }

    // A delegate that returns an IResult has chosen its response; the helpers leave it alone. Results.Empty is the
    // proof: mapped, it would become the empty status (204); left alone, it is a 200 with no body.
    [Fact]
    public async Task Endpoint_ReturningAnIResultItself_IsLeftAlone()
    {
        await using var app = await StartAppAsync(null, static app => app.Get("own", static () => Results.Empty));

        AssertEmpty(await GetAsync(app, "own"), StatusCodes.Status200OK);
    }

    // Only the endpoint helpers map results. An endpoint mapped with ASP.NET Core's own MapGet answers as ASP.NET Core
    // does, which is the documented boundary.
    [Fact]
    public async Task Endpoint_MappedWithoutTheHelpers_IsNotMapped()
    {
        await using var app = await StartAppAsync(null, static app =>
            app.MapGet("raw", static (ISender sender, CancellationToken ct) => sender.Send(new MappingVoidRequest(), ct)));

        AssertEmpty(await GetAsync(app, "raw"), StatusCodes.Status200OK);
    }

    // ---------------------------------------------------------------------------------------------
    // HttpResponseException
    // ---------------------------------------------------------------------------------------------

    // The message is what ends up in the log and in Sentry, so every error has to be in it — a message
    // built from only the first error hides the rest of a validation failure.
    [Theory]
    [InlineData("only", "only")]
    [InlineData("first,second", "first; second")]
    [InlineData("a,b,c", "a; b; c")]
    public void HttpResponseException_Message_JoinsEveryErrorWithSemicolons(string commaSeparatedErrors, string expectedMessage)
    {
        var errors = commaSeparatedErrors.Split(',');

        var exception = new HttpResponseException(new ErrorResponse(HttpStatusCode.BadRequest, errors));

        Assert.Equal(expectedMessage, exception.Message);
    }

    // Without the fallback an error-less HttpResponseException would log an empty message and the entry
    // would say nothing at all.
    [Theory]
    [InlineData(HttpStatusCode.NotFound, "NotFound")]
    [InlineData(HttpStatusCode.BadRequest, "BadRequest")]
    [InlineData(HttpStatusCode.InternalServerError, "InternalServerError")]
    public void HttpResponseException_Message_FallsBackToTheStatusNameWhenThereAreNoErrors(HttpStatusCode httpStatusCode, string expectedMessage)
    {
        var exception = new HttpResponseException(new ErrorResponse(httpStatusCode, []));

        Assert.Equal(expectedMessage, exception.Message);
    }

    // The middleware reads these two properties to write the response, so they must be the response's own
    // values and not a copy that can drift.
    [Fact]
    public void HttpResponseException_ExposesTheStatusCodeAndErrorsOfItsErrorResponse()
    {
        var errorResponse = new ErrorResponse(HttpStatusCode.Conflict, ["duplicate"]);

        var exception = new HttpResponseException(errorResponse);

        Assert.Same(errorResponse, exception.ErrorResponse);
        Assert.Same(errorResponse.Errors, exception.Errors);
        Assert.Equal(HttpStatusCode.Conflict, exception.HttpStatusCode);
    }

    [Fact]
    public void HttpResponseException_NullErrorResponse_ThrowsArgumentNullException()
    {
        var exception = Record.Exception(() => new HttpResponseException(null!));

        Assert.Equal("errorResponse", Assert.IsType<ArgumentNullException>(exception).ParamName);
    }

    // ---------------------------------------------------------------------------------------------
    // SingleResponse<T>
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void SingleResponse_Result_ReturnsTheInstanceItWasGiven()
    {
        var payload = new MappingPayload("wrapped", 1);

        Assert.Same(payload, new SingleResponse<MappingPayload>(payload).Result);
    }

    // A handler that found nothing wraps null; the wrapper must not turn that into anything else.
    [Fact]
    public void SingleResponse_NullReference_ReturnsNull()
    {
        Assert.Null(new SingleResponse<string>(null!).Result);
    }

    // T? on the property is an annotation, not Nullable<T>: a wrapped value type comes back as the value
    // itself even when that value is the default, and goes on the wire as that value. A wrapper that
    // "helpfully" normalized default(T) to null would blank every zero, false and DateTime.MinValue an
    // API returns, and clients reading result as a number would start seeing null.
    [Fact]
    public void SingleResponse_DefaultValueType_KeepsTheValueInsteadOfNull()
    {
        Assert.Equal(0, new SingleResponse<int>(0).Result);
        Assert.Equal(default(DateTime), new SingleResponse<DateTime>(default).Result);
        Assert.Equal(
            "{\"result\":0}",
            JsonSerializer.Serialize(new SingleResponse<int>(0), CreateJsonOptions(webDefaults: true)));
    }

    // Clients (and integration tests) deserialize this wrapper, which only works while the constructor
    // parameter keeps matching the property by name.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SingleResponse_RoundTripsThroughSystemTextJson(bool webDefaults)
    {
        var options = CreateJsonOptions(webDefaults);

        var json = JsonSerializer.Serialize(new SingleResponse<string>("wrapped"), options);
        var roundTripped = JsonSerializer.Deserialize<SingleResponse<string>>(json, options)!;

        Assert.Equal("wrapped", roundTripped.Result);
    }

    [Fact]
    public void SingleResponse_ValueType_RoundTripsThroughSystemTextJson()
    {
        var options = CreateJsonOptions(webDefaults: true);

        var json = JsonSerializer.Serialize(new SingleResponse<int>(42), options);
        var roundTripped = JsonSerializer.Deserialize<SingleResponse<int>>(json, options)!;

        Assert.Equal(42, roundTripped.Result);
    }

    [Fact]
    public void SingleResponse_NullResult_RoundTripsAsNull()
    {
        var options = CreateJsonOptions(webDefaults: true);

        var json = JsonSerializer.Serialize(new SingleResponse<string>(null!), options);
        var roundTripped = JsonSerializer.Deserialize<SingleResponse<string>>(json, options)!;

        Assert.Equal("{\"result\":null}", json);
        Assert.Null(roundTripped.Result);
    }

    // ---------------------------------------------------------------------------------------------
    // MultiResponse<T>
    // ---------------------------------------------------------------------------------------------

    // Data is dereferenced by every caller; failing in the constructor names the caller that passed null
    // instead of producing a NullReferenceException somewhere in the serializer.
    [Fact]
    public void MultiResponse_NullData_ThrowsArgumentNullExceptionNamingTheParameter()
    {
        var exception = Record.Exception(() => new MultiResponse<string>(null!, totalCount: 0, pageSize: 10));

        Assert.Equal("data", Assert.IsType<ArgumentNullException>(exception).ParamName);
    }

    [Fact]
    public void MultiResponse_ExposesTheArgumentsItWasConstructedWith()
    {
        IReadOnlyList<string> data = ["a", "b"];

        var response = new MultiResponse<string>(data, totalCount: 9, pageSize: 2);

        Assert.Same(data, response.Data);
        Assert.Equal(9, response.TotalCount);
        Assert.Equal(2, response.PageSize);
    }

    // PagesCount drives the client's pager. A partial last page must round up (or the last rows are
    // unreachable), and a non-positive page size must not divide by zero or return a negative count.
    // The int.MaxValue case guards the obvious "optimization" to integer arithmetic:
    // (totalCount + pageSize - 1) / pageSize overflows there and reports a negative page count.
    [Theory]
    [InlineData(int.MaxValue, 10, 214748365)]
    [InlineData(0, 10, 0)]
    [InlineData(1, 10, 1)]
    [InlineData(10, 10, 1)]
    [InlineData(11, 10, 2)]
    [InlineData(7, 2, 4)]
    [InlineData(10, 5, 2)]
    [InlineData(1000000, 3, 333334)]
    [InlineData(10, 0, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(10, -3, 0)]
    public void MultiResponse_PagesCount_RoundsUpAndGuardsNonPositivePageSizes(int totalCount, int pageSize, int expectedPagesCount)
    {
        var response = new MultiResponse<string>([], totalCount, pageSize);

        Assert.Equal(expectedPagesCount, response.PagesCount);
    }

    // Asking for a page past the end returns no rows but still has to report how many pages exist, or the
    // client's pager collapses to zero.
    [Fact]
    public void MultiResponse_EmptyPage_StillReportsTheTotalAndPageCount()
    {
        var response = new MultiResponse<string>([], totalCount: 25, pageSize: 10);

        Assert.Empty(response.Data);
        Assert.Equal(25, response.TotalCount);
        Assert.Equal(3, response.PagesCount);
    }

    // It must be deserializable, not only serializable: consumers read list endpoints back with
    // ReadFromJsonAsync<MultiResponse<T>>, under both the web defaults and the plain defaults.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MultiResponse_RoundTripsThroughSystemTextJson(bool webDefaults)
    {
        var options = CreateJsonOptions(webDefaults);

        var json = JsonSerializer.Serialize(new MultiResponse<string>(["a", "b"], totalCount: 5, pageSize: 2), options);
        var roundTripped = JsonSerializer.Deserialize<MultiResponse<string>>(json, options)!;

        Assert.Equal(["a", "b"], roundTripped.Data);
        Assert.Equal(5, roundTripped.TotalCount);
        Assert.Equal(2, roundTripped.PageSize);
        Assert.Equal(3, roundTripped.PagesCount);
    }

    // PagesCount is computed, not stored: a body that carries a different number must not be able to
    // override it.
    [Fact]
    public void MultiResponse_PagesCountInTheJson_IsRecomputedNotRead()
    {
        const string json = "{\"data\":[],\"totalCount\":7,\"pageSize\":2,\"pagesCount\":999}";

        var roundTripped = JsonSerializer.Deserialize<MultiResponse<string>>(json, CreateJsonOptions(webDefaults: true))!;

        Assert.Equal(4, roundTripped.PagesCount);
    }

    // ---------------------------------------------------------------------------------------------
    // ErrorsResponse / ErrorResponse
    // ---------------------------------------------------------------------------------------------

    // The trace id is optional so the body can also be written outside a request; the middleware always
    // fills it in.
    [Fact]
    public void ErrorsResponse_TraceId_IsOptionalAndDefaultsToNull()
    {
        var response = new ErrorsResponse(["boom"]);

        Assert.Null(response.TraceId);
        Assert.Equal("boom", Assert.Single(response.Errors));
    }

    // Equality is value-based for TraceId but REFERENCE-based for the error list, because
    // IReadOnlyList<string> brings no structural equality: two bodies carrying the same error text are
    // not equal. Worth pinning as one contract — a test that compares whole responses with Assert.Equal
    // fails for reasons that have nothing to do with the errors it is checking.
    [Fact]
    public void ErrorsResponse_Equality_ComparesTheTraceIdByValueAndTheErrorListByReference()
    {
        IReadOnlyList<string> errors = new[] { "boom" };
        IReadOnlyList<string> sameTextOtherList = new[] { "boom" };

        Assert.Equal(new ErrorsResponse(errors, "trace-1"), new ErrorsResponse(errors, "trace-1"));
        Assert.Equal(new ErrorsResponse(errors, "trace-1").GetHashCode(), new ErrorsResponse(errors, "trace-1").GetHashCode());
        Assert.NotEqual(new ErrorsResponse(errors, "trace-1"), new ErrorsResponse(errors, "trace-2"));
        Assert.NotEqual(new ErrorsResponse(errors), new ErrorsResponse(sameTextOtherList));
    }

    [Fact]
    public void ErrorResponse_ComparesByValue()
    {
        IReadOnlyList<string> errors = ["boom"];

        Assert.Equal(
            new ErrorResponse(HttpStatusCode.BadRequest, errors),
            new ErrorResponse(HttpStatusCode.BadRequest, errors));
        Assert.NotEqual(
            new ErrorResponse(HttpStatusCode.BadRequest, errors),
            new ErrorResponse(HttpStatusCode.NotFound, errors));
    }

    // This is the public error contract: clients deserialize it, so it has to read back.
    [Fact]
    public void ErrorsResponse_RoundTripsThroughSystemTextJson()
    {
        var options = CreateJsonOptions(webDefaults: true);

        var json = JsonSerializer.Serialize(new ErrorsResponse(["boom"], "trace-1"), options);
        var roundTripped = JsonSerializer.Deserialize<ErrorsResponse>(json, options)!;

        Assert.Contains("\"traceId\":\"trace-1\"", json);
        Assert.Equal(["boom"], roundTripped.Errors);
        Assert.Equal("trace-1", roundTripped.TraceId);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static JsonSerializerOptions CreateJsonOptions(bool webDefaults)
    {
        return webDefaults ? new JsonSerializerOptions(JsonSerializerDefaults.Web) : new JsonSerializerOptions();
    }

    /// <summary>
    /// An app with the mediator, the exception middleware and only this file's handlers, registered by hand rather than
    /// by scanning this assembly, which would also pull in every sibling test file's handlers.
    /// </summary>
    private static async Task<WebApplication> StartAppAsync(
        EmptyResponseStatusCode? emptyResponseStatusCode,
        Action<WebApplication> map,
        Action<IServiceCollection>? configure = null)
    {
        var builder = TestApps.CreateBuilder();
        builder.WebHost.UseTestServer();
        var services = builder.Services;

        if (emptyResponseStatusCode is { } configured)
            services.AddMediator(options => options.EmptyResponseStatusCode = configured);
        else
            services.AddMediator();

        services.AddTransient<IRequestHandler<MappingPayloadRequest, MappingPayload>, MappingPayloadRequestHandler>();
        services.AddTransient<IRequestHandler<MappingNullResponseRequest, string?>, MappingNullResponseRequestHandler>();
        services.AddTransient<IRequestHandler<MappingResultRequest, IResult>, MappingResultRequestHandler>();
        services.AddTransient<IRequestHandler<MappingErrorResponseRequest, ErrorResponse>, MappingErrorResponseRequestHandler>();
        services.AddTransient<IRequestHandler<MappingThrowingRequest, MappingPayload>, MappingThrowingRequestHandler>();
        services.AddTransient<IRequestHandler<MappingTextRequest, string>, MappingTextRequestHandler>();
        services.AddTransient<IRequestHandler<MappingNullableIntRequest, int?>, MappingNullableIntRequestHandler>();
        services.AddTransient<IRequestHandler<MappingPagedRequest, MultiResponse<string>>, MappingPagedRequestHandler>();
        services.AddTransient<IRequestHandler<MappingShortCircuitRequest, object>, MappingShortCircuitRequestHandler>();
        // Only ever runs for MappingShortCircuitRequest, so every other test here still sees a bare pipeline.
        services.AddTransient<IPipelineBehavior<MappingShortCircuitRequest, object>, MappingShortCircuitBehavior>();
        services.AddTransient<IRequestHandler<MappingVoidRequest>, MappingVoidRequestHandler>();
        services.AddTransient<IRequestHandler<MappingThrowingVoidRequest>, MappingThrowingVoidRequestHandler>();
        configure?.Invoke(services);

        var app = builder.Build();
        app.UsePhoenixExceptionHandling();
        map(app);
        await app.StartAsync();
        return app;
    }

    private static async Task<HttpAnswer> GetAsync(WebApplication app, string path)
    {
        using var client = app.GetTestClient();
        using var response = await client.GetAsync(path);

        return new HttpAnswer((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType, await response.Content.ReadAsStringAsync());
    }

    /// <summary>An empty response carries the status and nothing else — no body, no content type.</summary>
    private static void AssertEmpty(HttpAnswer answer, int expectedStatusCode)
    {
        Assert.Equal(expectedStatusCode, answer.StatusCode);
        Assert.Equal(string.Empty, answer.Body);
        Assert.Null(answer.ContentType);
    }

    private static string[] MemberNames(JsonElement json)
        => json.EnumerateObject().Select(static member => member.Name).Order(StringComparer.Ordinal).ToArray();

    private sealed record HttpAnswer(int StatusCode, string? ContentType, string Body)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement.Clone();
    }
}

// -------------------------------------------------------------------------------------------------
// Requests and handlers. Every handler is parameterless, so the assembly scans other test files run
// can construct them, and each request type has exactly one handler.
// -------------------------------------------------------------------------------------------------

public sealed record MappingPayload(string Name, int Count);

public sealed class MappingPayloadRequest : IRequest<MappingPayload>
{
    public string Value { get; init; } = "payload";
}

public sealed class MappingPayloadRequestHandler : IRequestHandler<MappingPayloadRequest, MappingPayload>
{
    public Task<MappingPayload> Handle(MappingPayloadRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new MappingPayload(request.Value, 7));
}

public sealed class MappingNullResponseRequest : IRequest<string?>
{
}

public sealed class MappingNullResponseRequestHandler : IRequestHandler<MappingNullResponseRequest, string?>
{
    public Task<string?> Handle(MappingNullResponseRequest request, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);
}

public sealed class MappingResultRequest : IRequest<IResult>
{
}

public sealed class MappingResultRequestHandler : IRequestHandler<MappingResultRequest, IResult>
{
    public Task<IResult> Handle(MappingResultRequest request, CancellationToken cancellationToken)
        => Task.FromResult(Results.Json(
            new MappingPayload("handler-chosen-result", 1),
            statusCode: StatusCodes.Status202Accepted));
}

public sealed class MappingErrorResponseRequest : IRequest<ErrorResponse>
{
}

public sealed class MappingErrorResponseRequestHandler : IRequestHandler<MappingErrorResponseRequest, ErrorResponse>
{
    public Task<ErrorResponse> Handle(MappingErrorResponseRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new ErrorResponse(HttpStatusCode.Conflict, ["already exists"]));
}

public sealed class MappingThrowingRequest : IRequest<MappingPayload>
{
}

public sealed class MappingThrowingRequestHandler : IRequestHandler<MappingThrowingRequest, MappingPayload>
{
    public const string Message = "mapping handler exploded";

    public Task<MappingPayload> Handle(MappingThrowingRequest request, CancellationToken cancellationToken)
        => throw new InvalidOperationException(Message);
}

public sealed class MappingTextRequest : IRequest<string>
{
}

public sealed class MappingTextRequestHandler : IRequestHandler<MappingTextRequest, string>
{
    public Task<string> Handle(MappingTextRequest request, CancellationToken cancellationToken) => Task.FromResult("hello");
}

public sealed class MappingVoidRequest : IRequest
{
}

public sealed class MappingVoidRequestHandler : IRequestHandler<MappingVoidRequest>
{
    public Task Handle(MappingVoidRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class MappingThrowingVoidRequest : IRequest
{
}

public sealed class MappingThrowingVoidRequestHandler : IRequestHandler<MappingThrowingVoidRequest>
{
    public Task Handle(MappingThrowingVoidRequest request, CancellationToken cancellationToken)
        => throw new HttpResponseException(new ErrorResponse(HttpStatusCode.NotFound, ["missing"]));
}

/// <summary>A request whose response is a nullable value type: null means "nothing", 0 does not.</summary>
public sealed class MappingNullableIntRequest : IRequest<int?>
{
    public int? Value { get; init; }
}

public sealed class MappingNullableIntRequestHandler : IRequestHandler<MappingNullableIntRequest, int?>
{
    public Task<int?> Handle(MappingNullableIntRequest request, CancellationToken cancellationToken)
        => Task.FromResult(request.Value);
}

/// <summary>A list request, reaching IRequest&lt;MultiResponse&lt;string&gt;&gt; only through IPagedRequest.</summary>
public sealed class MappingPagedRequest : IPagedRequest<string>
{
    public int PageNum { get; init; } = 1;
    public int PageSize { get; init; } = 2;
    public string? Query { get; init; }
}

public sealed class MappingPagedRequestHandler : IRequestHandler<MappingPagedRequest, MultiResponse<string>>
{
    private static readonly string[] Rows = ["a", "b", "c", "d", "e"];

    public Task<MultiResponse<string>> Handle(MappingPagedRequest request, CancellationToken cancellationToken)
    {
        var page = Rows.Skip((request.PageNum - 1) * request.PageSize).Take(request.PageSize).ToArray();

        return Task.FromResult(new MultiResponse<string>(page, Rows.Length, request.PageSize));
    }
}

/// <summary>
/// Responds with <see cref="object"/> so a behavior can answer with an <c>ErrorResponse</c> where the
/// handler would have answered with a payload.
/// </summary>
public sealed class MappingShortCircuitRequest : IRequest<object>
{
}

public sealed class MappingShortCircuitRequestHandler : IRequestHandler<MappingShortCircuitRequest, object>
{
    public Task<object> Handle(MappingShortCircuitRequest request, CancellationToken cancellationToken)
        => Task.FromResult<object>(new MappingPayload("handler-ran", 1));
}

/// <summary>Answers without calling the handler, the way an authorization or cache behavior does.</summary>
public sealed class MappingShortCircuitBehavior : IPipelineBehavior<MappingShortCircuitRequest, object>
{
    public Task<object> Handle(MappingShortCircuitRequest request, RequestHandlerDelegate<object> next, CancellationToken cancellationToken)
        => Task.FromResult<object>(new ErrorResponse(HttpStatusCode.Forbidden, ["not allowed"]));
}

/// <summary>An app-defined error response: the subtype an API adds a machine-readable code to.</summary>
public sealed record MappingDerivedErrorResponse(string Code)
    : ErrorResponse(HttpStatusCode.PaymentRequired, new[] { "card declined" });

// -------------------------------------------------------------------------------------------------
// Senders and results used to exercise the mapping helpers directly.
// -------------------------------------------------------------------------------------------------

/// <summary>A hand-written result, to prove the mapper returns it untouched and the framework runs it.</summary>
public sealed class MappingCustomResult : IResult
{
    public bool WasExecuted { get; private set; }

    public Task ExecuteAsync(HttpContext httpContext)
    {
        WasExecuted = true;
        httpContext.Response.StatusCode = StatusCodes.Status418ImATeapot;
        return Task.CompletedTask;
    }
}

/// <summary>An app's ISender decorator: it forwards every send and knows nothing about the mediator's options.</summary>
public sealed class MappingPlainSenderWrapper(ISender inner) : ISender
{
    public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        => inner.Send(request, cancellationToken);

    public Task<TResponse> Send<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest<TResponse>
        => inner.Send<TRequest, TResponse>(request, cancellationToken);

    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest
        => inner.Send<TRequest>(request, cancellationToken);
}

