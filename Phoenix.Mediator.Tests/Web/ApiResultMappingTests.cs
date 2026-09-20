using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
/// Result mapping (<c>ToApiResult</c>, <c>SendAsApiResult</c>) and the response wrappers
/// (<c>SingleResponse</c>, <c>MultiResponse</c>, <c>ErrorResponse</c>, <c>ErrorsResponse</c>,
/// <c>HttpResponseException</c>).
/// <para>
/// Mapping assertions run the produced <see cref="IResult"/> the way the framework would, so the status
/// code, content type and body are the ones a client actually receives — a result type alone says
/// nothing about what gets written.
/// </para>
/// </summary>
public sealed class MappingApiResultTests
{
    // The one documented error body is produced by two different code paths with two different
    // serializers. The exception middleware pins camelCase; ToApiResult's ErrorResponse arm goes through
    // Results.Json and so follows whatever naming policy the app configured. An app that turns the policy
    // off therefore serves {"Errors":[...]} from a handler that RETURNS an ErrorResponse and
    // {"errors":[...]} from a handler that THROWS on the very next endpoint, and only the second matches
    // the Produces<ErrorsResponse> schema every endpoint advertises. Pinned rather than asserted as
    // desirable: which serializer should win is an API-contract decision, and this test is here so the
    // divergence cannot be changed or shipped unnoticed.
    [Fact]
    public async Task ErrorBody_IsCasedByTheAppPolicyThroughToApiResultButAlwaysCamelCaseFromTheMiddleware()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMediator();
        services.ConfigureHttpJsonOptions(static options => options.SerializerOptions.PropertyNamingPolicy = null);
        using var provider = services.BuildServiceProvider();

        var returned = await ResultExecution.ExecuteAsync(
            ((object?)new ErrorResponse(HttpStatusCode.NotFound, ["gone"])).ToApiResult(),
            provider);

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = new MemoryStream();
        await new ExceptionHandlingMiddleware(
                _ => throw new NotFoundException("gone"),
                new RecordingLoggerProvider().CreateLogger<ExceptionHandlingMiddleware>(),
                new ConfigurationBuilder().Build())
            .InvokeAsync(context);

        context.Response.Body.Position = 0;
        var thrown = await new StreamReader(context.Response.Body).ReadToEndAsync();

        Assert.Equal(StatusCodes.Status404NotFound, returned.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);

        // Same status, same meaning, different field names.
        Assert.Contains("\"Errors\"", returned.Body, StringComparison.Ordinal);
        Assert.Contains("\"errors\"", thrown, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Errors\"", thrown, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // ToApiResult: empty responses
    // ---------------------------------------------------------------------------------------------

    // The documented default: this overload never consults MediatorOptions, so a null response is 204
    // even in an app that configured 200. Endpoints that want the configured status must use SendAsApiResult.
    [Fact]
    public async Task ToApiResult_NullValue_WritesNoContent()
    {
        await AssertEmptyResponseAsync(((object?)null).ToApiResult(), StatusCodes.Status204NoContent);
    }

    [Theory]
    [InlineData(EmptyResponseStatusCode.Ok, StatusCodes.Status200OK)]
    [InlineData(EmptyResponseStatusCode.NoContent, StatusCodes.Status204NoContent)]
    public async Task ToApiResult_NullValue_UsesTheGivenEmptyResponseStatusCode(EmptyResponseStatusCode emptyResponseStatusCode, int expectedStatusCode)
    {
        await AssertEmptyResponseAsync(((object?)null).ToApiResult(emptyResponseStatusCode), expectedStatusCode);
    }

    // EmptyResponseStatusCode is an enum, so any int can be cast into it. The mapper has to reject the
    // ones it cannot write rather than silently returning some other status.
    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    [InlineData(404)]
    [InlineData(-1)]
    public void ToApiResult_UndefinedEmptyResponseStatusCode_ThrowsAndNamesTheAllowedCodes(int rawStatusCode)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => ((object?)null).ToApiResult((EmptyResponseStatusCode)rawStatusCode));

        Assert.Contains("200 OK", exception.Message);
        Assert.Contains("204 No Content", exception.Message);
    }

    // The empty-response status is only consulted when there is no body, so a misconfigured option must
    // not throw away a perfectly good 200 payload.
    [Fact]
    public async Task ToApiResult_NonNullValue_IgnoresAnUndefinedEmptyResponseStatusCode()
    {
        var executed = await ResultExecution.ExecuteAsync(
            new MappingPayload("kept", 1).ToApiResult((EmptyResponseStatusCode)999));

        Assert.Equal(StatusCodes.Status200OK, executed.StatusCode);
        Assert.Equal("kept", executed.Json.GetProperty("name").GetString());
    }

    // ---------------------------------------------------------------------------------------------
    // ToApiResult: IResult passthrough
    // ---------------------------------------------------------------------------------------------

    // Handlers and pipeline behaviors are allowed to return Results.* directly; wrapping such a result
    // in another one would lose its status, headers and body.
    [Fact]
    public void ToApiResult_IResult_ReturnsTheVerySameInstance()
    {
        var accepted = Results.Accepted("/mapping/queued");
        var noContent = Results.NoContent();
        var custom = new MappingCustomResult();

        Assert.Same(accepted, ((object?)accepted).ToApiResult());
        Assert.Same(noContent, ((object?)noContent).ToApiResult());
        Assert.Same(custom, ((object?)custom).ToApiResult());
    }

    // An explicitly requested empty-response status must not override a result the handler chose itself.
    [Theory]
    [InlineData(EmptyResponseStatusCode.Ok)]
    [InlineData(EmptyResponseStatusCode.NoContent)]
    public void ToApiResult_IResult_IsPassedThroughEvenWhenAnEmptyStatusIsGiven(EmptyResponseStatusCode emptyResponseStatusCode)
    {
        var chosen = Results.Accepted("/mapping/queued");

        Assert.Same(chosen, ((object?)chosen).ToApiResult(emptyResponseStatusCode));
    }

    [Fact]
    public async Task ToApiResult_IResult_KeepsTheStatusTheResultWritesItself()
    {
        var custom = new MappingCustomResult();

        var executed = await ResultExecution.ExecuteAsync(((object?)custom).ToApiResult());

        Assert.True(custom.WasExecuted);
        Assert.Equal(StatusCodes.Status418ImATeapot, executed.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // ToApiResult: ErrorResponse
    // ---------------------------------------------------------------------------------------------

    // A handler that returns an ErrorResponse instead of throwing still has to produce the real HTTP
    // status; returning 200 with an error body is the classic way clients end up ignoring failures.
    // 599 is in here because the mapper casts the status through unchecked: an app using a code outside
    // the HttpStatusCode enum must get that code, not a normalized 500.
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, StatusCodes.Status400BadRequest)]
    [InlineData(HttpStatusCode.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(HttpStatusCode.Conflict, StatusCodes.Status409Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity, StatusCodes.Status422UnprocessableEntity)]
    [InlineData(HttpStatusCode.InternalServerError, StatusCodes.Status500InternalServerError)]
    [InlineData((HttpStatusCode)599, 599)]
    public async Task ToApiResult_ErrorResponse_UsesItsHttpStatusCode(HttpStatusCode httpStatusCode, int expectedStatusCode)
    {
        var executed = await ResultExecution.ExecuteAsync(
            new ErrorResponse(httpStatusCode, ["boom"]).ToApiResult());

        Assert.Equal(expectedStatusCode, executed.StatusCode);
        Assert.Equal("boom", executed.Json.GetProperty("errors")[0].GetString());
    }

    // Clients parse one error shape. The middleware writes the public ErrorsResponse record with the web
    // JSON defaults; mapping a RETURNED ErrorResponse has to produce that byte for byte — trace id
    // included — or half the failures in an app come back in a shape the client cannot read, which half
    // depending on whether the handler threw or returned.
    [Fact]
    public async Task ToApiResult_ErrorResponse_WritesTheSameBodyAsTheMiddleware()
    {
        using var activity = new Activity("mapping-error-shape").Start();

        var executed = await ResultExecution.ExecuteAsync(
            new ErrorResponse(HttpStatusCode.BadRequest, ["first", "second"]).ToApiResult());

        var middlewareBody = JsonSerializer.Serialize(
            new ErrorsResponse(["first", "second"], activity.TraceId.ToString()),
            CreateJsonOptions(webDefaults: true));

        Assert.Equal(StatusCodes.Status400BadRequest, executed.StatusCode);
        Assert.StartsWith("application/json", executed.ContentType);
        Assert.Equal(middlewareBody, executed.Body);
    }

    // The other half of the trace id contract: outside a request there is no ambient Activity, and the
    // null-conditional in the mapper is the only thing keeping that from throwing. A background job that
    // maps an ErrorResponse must still get the standard body, with traceId simply null.
    [Fact]
    public async Task ToApiResult_ErrorResponse_WithoutACurrentActivity_WritesANullTraceId()
    {
        var previous = Activity.Current;
        Activity.Current = null;

        try
        {
            var executed = await ResultExecution.ExecuteAsync(
                new ErrorResponse(HttpStatusCode.BadRequest, ["boom"]).ToApiResult());

            Assert.Equal(StatusCodes.Status400BadRequest, executed.StatusCode);
            Assert.Equal(JsonValueKind.Null, executed.Json.GetProperty("traceId").ValueKind);
            Assert.Equal("boom", executed.Json.GetProperty("errors")[0].GetString());
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    // ErrorResponse is an unsealed record, so apps subclass it to carry an error code or a field name
    // next to the messages. The mapper matches on the type pattern, so a subclass still maps to its own
    // status and to the standard body — the extra members are not part of the wire contract.
    [Fact]
    public async Task ToApiResult_DerivedErrorResponse_IsMappedLikeTheBaseErrorResponse()
    {
        var executed = await ResultExecution.ExecuteAsync(
            new MappingDerivedErrorResponse("CARD_DECLINED").ToApiResult());

        var propertyNames = executed.Json.EnumerateObject().Select(static property => property.Name).ToArray();

        Assert.Equal(StatusCodes.Status402PaymentRequired, executed.StatusCode);
        Assert.Equal("card declined", executed.Json.GetProperty("errors")[0].GetString());
        Assert.Equal(2, propertyNames.Length);
        Assert.DoesNotContain("code", propertyNames);
    }

    // An error with no messages is still an error: the status has to survive even when there is nothing
    // to say, instead of collapsing into an empty 200.
    [Fact]
    public async Task ToApiResult_ErrorResponseWithoutErrors_WritesAnEmptyErrorsArray()
    {
        var executed = await ResultExecution.ExecuteAsync(
            new ErrorResponse(HttpStatusCode.Forbidden, []).ToApiResult());

        Assert.Equal(StatusCodes.Status403Forbidden, executed.StatusCode);
        Assert.Equal(JsonValueKind.Array, executed.Json.GetProperty("errors").ValueKind);
        Assert.Empty(executed.Json.GetProperty("errors").EnumerateArray());
    }

    // ErrorsResponse is the BODY type, ErrorResponse is the ERROR type. Returning the body type from a
    // handler produces an error-looking payload with a 200 status — worth pinning so the trap is visible.
    [Fact]
    public async Task ToApiResult_ErrorsResponse_IsMappedAsAPlainValueWith200()
    {
        var executed = await ResultExecution.ExecuteAsync(
            new ErrorsResponse(["boom"], "trace-1").ToApiResult());

        Assert.Equal(StatusCodes.Status200OK, executed.StatusCode);
        Assert.Equal("boom", executed.Json.GetProperty("errors")[0].GetString());
    }

    // ---------------------------------------------------------------------------------------------
    // ToApiResult: values
    // ---------------------------------------------------------------------------------------------

    // The value arrives typed as object (that is what ISender.Send returns), so serialization has to use
    // the runtime type. Serializing the declared type would write "{}" for every response in the app.
    [Fact]
    public async Task ToApiResult_ReferenceType_WritesTheRuntimeTypesPropertiesWith200()
    {
        object? value = new MappingPayload("echoed", 7);

        var executed = await ResultExecution.ExecuteAsync(value.ToApiResult());

        Assert.Equal(StatusCodes.Status200OK, executed.StatusCode);
        Assert.StartsWith("application/json", executed.ContentType);
        Assert.Equal("echoed", executed.Json.GetProperty("name").GetString());
        Assert.Equal(7, executed.Json.GetProperty("count").GetInt32());
    }

    // Documented: "always return JSON so Swagger/clients consistently get the documented
    // content-type/schema". A string must come back quoted as JSON, not as text/plain.
    [Theory]
    [InlineData(42, "42")]
    [InlineData(true, "true")]
    [InlineData(1.5, "1.5")]
    [InlineData("hello", "\"hello\"")]
    public async Task ToApiResult_ScalarValue_IsAlwaysWrittenAsJson(object value, string expectedBody)
    {
        var executed = await ResultExecution.ExecuteAsync(value.ToApiResult());

        Assert.Equal(StatusCodes.Status200OK, executed.StatusCode);
        Assert.StartsWith("application/json", executed.ContentType);
        Assert.Equal(expectedBody, executed.Body);
    }

    [Fact]
    public async Task ToApiResult_Collection_WritesAJsonArray()
    {
        var executed = await ResultExecution.ExecuteAsync(new[] { 1, 2, 3 }.ToApiResult());

        Assert.Equal(StatusCodes.Status200OK, executed.StatusCode);
        Assert.Equal("[1,2,3]", executed.Body);
    }

    // "No rows" is a successful 200 with an empty array, not an empty response: a client that switches on
    // the status must not treat an empty page as "nothing was returned".
    [Fact]
    public async Task ToApiResult_EmptyCollection_WritesAnEmptyArrayNotAnEmptyResponse()
    {
        var executed = await ResultExecution.ExecuteAsync(Array.Empty<int>().ToApiResult());

        Assert.Equal(StatusCodes.Status200OK, executed.StatusCode);
        Assert.Equal("[]", executed.Body);
    }

    [Fact]
    public async Task ToApiResult_SingleResponse_WritesTheWrappedResult()
    {
        var executed = await ResultExecution.ExecuteAsync(new SingleResponse<string>("wrapped").ToApiResult());

        Assert.Equal(StatusCodes.Status200OK, executed.StatusCode);
        Assert.Equal("wrapped", executed.Json.GetProperty("result").GetString());
    }

    // "Found nothing" is often written as new SingleResponse<T>(null). The WRAPPER is not null, so this
    // is a 200 with {"result":null} and never the empty response — an endpoint that wants 204 has to
    // return null itself. Pinned because the two spellings look identical at the call site.
    [Fact]
    public async Task ToApiResult_SingleResponseWrappingNull_Writes200WithANullResult()
    {
        var executed = await ResultExecution.ExecuteAsync(new SingleResponse<string>(null!).ToApiResult());

        Assert.Equal(StatusCodes.Status200OK, executed.StatusCode);
        Assert.Equal("{\"result\":null}", executed.Body);
    }

    // The paging wrapper is what every list endpoint returns; its computed page count has to reach the
    // client, not just the raw constructor arguments.
    [Fact]
    public async Task ToApiResult_MultiResponse_WritesTheDataAndThePagingNumbers()
    {
        var executed = await ResultExecution.ExecuteAsync(
            new MultiResponse<string>(["a", "b"], totalCount: 5, pageSize: 2).ToApiResult());

        Assert.Equal(StatusCodes.Status200OK, executed.StatusCode);
        Assert.Equal(2, executed.Json.GetProperty("data").GetArrayLength());
        Assert.Equal(5, executed.Json.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, executed.Json.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, executed.Json.GetProperty("pagesCount").GetInt32());
    }

    // ---------------------------------------------------------------------------------------------
    // SendAsApiResult: empty responses through each overload
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(EmptyResponseStatusCode.Ok, StatusCodes.Status200OK)]
    [InlineData(EmptyResponseStatusCode.NoContent, StatusCodes.Status204NoContent)]
    public async Task SendAsApiResult_ObjectOverloadWithVoidRequest_UsesTheConfiguredEmptyResponseStatus(
        EmptyResponseStatusCode configured, int expectedStatusCode)
    {
        using var provider = CreateProvider(configured);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        // Cast to object so this binds to SendAsApiResult(ISender, object, ...) and not to the generic
        // void overload, which is a different code path.
        var result = await sender.SendAsApiResult((object)new MappingVoidRequest());

        await AssertEmptyResponseAsync(result, expectedStatusCode);
    }

    // A handler that legitimately has nothing to return (a lookup that found nothing to send back) must
    // map the same way a void request does, so the endpoint matches what OpenAPI advertises.
    [Theory]
    [InlineData(EmptyResponseStatusCode.Ok, StatusCodes.Status200OK)]
    [InlineData(EmptyResponseStatusCode.NoContent, StatusCodes.Status204NoContent)]
    public async Task SendAsApiResult_ObjectOverloadWithNullHandlerResult_UsesTheConfiguredEmptyResponseStatus(
        EmptyResponseStatusCode configured, int expectedStatusCode)
    {
        using var provider = CreateProvider(configured);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var result = await sender.SendAsApiResult(new MappingNullResponseRequest());

        await AssertEmptyResponseAsync(result, expectedStatusCode);
    }

    [Theory]
    [InlineData(EmptyResponseStatusCode.Ok, StatusCodes.Status200OK)]
    [InlineData(EmptyResponseStatusCode.NoContent, StatusCodes.Status204NoContent)]
    public async Task SendAsApiResult_TypedOverload_UsesTheConfiguredEmptyResponseStatus(
        EmptyResponseStatusCode configured, int expectedStatusCode)
    {
        using var provider = CreateProvider(configured);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var result = await sender.SendAsApiResult<MappingNullResponseRequest, string?>(new MappingNullResponseRequest());

        await AssertEmptyResponseAsync(result, expectedStatusCode);
    }

    [Theory]
    [InlineData(EmptyResponseStatusCode.Ok, StatusCodes.Status200OK)]
    [InlineData(EmptyResponseStatusCode.NoContent, StatusCodes.Status204NoContent)]
    public async Task SendAsApiResult_VoidOverload_UsesTheConfiguredEmptyResponseStatus(
        EmptyResponseStatusCode configured, int expectedStatusCode)
    {
        using var provider = CreateProvider(configured);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var result = await sender.SendAsApiResult<MappingVoidRequest>(new MappingVoidRequest());

        await AssertEmptyResponseAsync(result, expectedStatusCode);
    }

    // The default has to stay 204: apps that never touch MediatorOptions rely on it.
    [Fact]
    public async Task SendAsApiResult_WithoutConfiguredOptions_DefaultsToNoContent()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await AssertEmptyResponseAsync(
            await sender.SendAsApiResult((object)new MappingVoidRequest()),
            StatusCodes.Status204NoContent);
        await AssertEmptyResponseAsync(
            await sender.SendAsApiResult<MappingVoidRequest>(new MappingVoidRequest()),
            StatusCodes.Status204NoContent);
    }

    // A handler declared IRequest<int?> with nothing to return goes through the generic overload, where
    // the response is boxed before the mapper sees it. An empty Nullable<int> has to box to null and map
    // to the empty response, instead of reaching the serializer as a 200 with the literal "null".
    [Fact]
    public async Task SendAsApiResult_TypedOverloadWithAnEmptyNullableResponse_UsesTheConfiguredEmptyResponseStatus()
    {
        using var provider = CreateProvider(EmptyResponseStatusCode.Ok);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await AssertEmptyResponseAsync(
            await sender.SendAsApiResult<MappingNullableIntRequest, int?>(new MappingNullableIntRequest()),
            StatusCodes.Status200OK);
    }

    // The mirror image, and the one a "did the handler return anything?" check gets wrong: 0 is a value.
    // Treating default(T) as empty would turn every legitimate zero into a 204 with no body.
    [Fact]
    public async Task SendAsApiResult_TypedOverloadWithAZeroResponse_Writes200WithTheValue()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var executed = await ResultExecution.ExecuteAsync(
            await sender.SendAsApiResult<MappingNullableIntRequest, int?>(new MappingNullableIntRequest { Value = 0 }));

        Assert.Equal(StatusCodes.Status200OK, executed.StatusCode);
        Assert.Equal("0", executed.Body);
    }

    // ---------------------------------------------------------------------------------------------
    // SendAsApiResult: non-empty responses
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SendAsApiResult_ObjectOverload_WritesTheHandlerResponseAsJson()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var executed = await ResultExecution.ExecuteAsync(
            await sender.SendAsApiResult(new MappingPayloadRequest { Value = "from-object-overload" }));

        Assert.Equal(StatusCodes.Status200OK, executed.StatusCode);
        Assert.StartsWith("application/json", executed.ContentType);
        Assert.Equal("from-object-overload", executed.Json.GetProperty("name").GetString());
    }

    // The strongly-typed overload skips the reflection dispatch, so it is a genuinely separate path that
    // has to end up at the same mapping.
    [Fact]
    public async Task SendAsApiResult_TypedOverload_WritesTheHandlerResponseAsJson()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var executed = await ResultExecution.ExecuteAsync(
            await sender.SendAsApiResult<MappingPayloadRequest, MappingPayload>(
                new MappingPayloadRequest { Value = "from-typed-overload" }));

        Assert.Equal(StatusCodes.Status200OK, executed.StatusCode);
        Assert.Equal("from-typed-overload", executed.Json.GetProperty("name").GetString());
    }

    // A handler that needs a status the mapper does not produce (202, a redirect, a file) returns an
    // IResult; SendAsApiResult must hand it to the framework untouched.
    [Fact]
    public async Task SendAsApiResult_HandlerReturningAnIResult_PassesItThrough()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var executed = await ResultExecution.ExecuteAsync(
            await sender.SendAsApiResult(new MappingResultRequest()));

        Assert.Equal(StatusCodes.Status202Accepted, executed.StatusCode);
        Assert.Equal("handler-chosen-result", executed.Json.GetProperty("name").GetString());
    }

    [Fact]
    public async Task SendAsApiResult_HandlerReturningAnErrorResponse_MapsToItsStatusAndErrorsBody()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var executed = await ResultExecution.ExecuteAsync(
            await sender.SendAsApiResult<MappingErrorResponseRequest, ErrorResponse>(new MappingErrorResponseRequest()));

        Assert.Equal(StatusCodes.Status409Conflict, executed.StatusCode);
        Assert.Equal("already exists", executed.Json.GetProperty("errors")[0].GetString());
    }

    // A behavior may answer without calling the handler (authorization, a cache hit, any short circuit).
    // What reaches the client is what the PIPELINE produced, so an ErrorResponse from a behavior has to
    // become a real error status — not a 200 carrying an error-shaped body, and not the handler's value.
    [Fact]
    public async Task SendAsApiResult_BehaviorShortCircuitingWithAnErrorResponse_MapsTheBehaviorsValue()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var executed = await ResultExecution.ExecuteAsync(
            await sender.SendAsApiResult(new MappingShortCircuitRequest()));

        var errors = executed.Json.GetProperty("errors");

        Assert.Equal(StatusCodes.Status403Forbidden, executed.StatusCode);
        Assert.Equal(1, errors.GetArrayLength());
        Assert.Equal("not allowed", errors[0].GetString());
    }

    // Every list endpoint sends an IPagedRequest<T>, which reaches IRequest<MultiResponse<T>> only
    // through an inherited interface. Dispatch and the paging body have to survive that indirection
    // together: a pager whose pagesCount is missing or wrong makes the last rows unreachable.
    [Fact]
    public async Task SendAsApiResult_PagedRequest_WritesTheMultiResponsePagingBody()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var executed = await ResultExecution.ExecuteAsync(
            await sender.SendAsApiResult(new MappingPagedRequest { PageNum = 2, PageSize = 2 }));

        Assert.Equal(StatusCodes.Status200OK, executed.StatusCode);
        Assert.Equal(["c", "d"], executed.Json.GetProperty("data").EnumerateArray().Select(static item => item.GetString()));
        Assert.Equal(5, executed.Json.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, executed.Json.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, executed.Json.GetProperty("pagesCount").GetInt32());
    }

    // An endpoint delegate may declare its parameter as the interface (IRequest<TResponse>), and the
    // endpoint helpers advertise "200 with TResponse" for exactly that shape. SendAsApiResult binds with
    // TRequest = the interface, for which no handler is registered, so it has to dispatch by the runtime
    // type — otherwise the advertised response is one the endpoint can never produce.
    [Fact]
    public async Task SendAsApiResult_RequestTypedAsTheRequestInterface_StillMapsTheHandlerResponse()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        IRequest<MappingPayload> request = new MappingPayloadRequest { Value = "via-interface" };

        var executed = await ResultExecution.ExecuteAsync(
            await sender.SendAsApiResult<IRequest<MappingPayload>, MappingPayload>(request));

        Assert.Equal(StatusCodes.Status200OK, executed.StatusCode);
        Assert.Equal("via-interface", executed.Json.GetProperty("name").GetString());
    }

    // Same for commands dispatched polymorphically (a List<IRequest>, a factory return value): the void
    // overload binds with TRequest = IRequest, and the configured empty status still has to be honored.
    [Fact]
    public async Task SendAsApiResult_VoidRequestTypedAsTheRequestInterface_UsesTheConfiguredEmptyResponseStatus()
    {
        using var provider = CreateProvider(EmptyResponseStatusCode.Ok);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        IRequest command = new MappingVoidRequest();

        await AssertEmptyResponseAsync(await sender.SendAsApiResult<IRequest>(command), StatusCodes.Status200OK);
    }

    // ---------------------------------------------------------------------------------------------
    // SendAsApiResult: exceptions and cancellation
    // ---------------------------------------------------------------------------------------------

    // Documented contract: SendAsApiResult does not catch. Swallowing here would turn every unhandled
    // failure into a 200/204 and the exception-handling middleware would never see it.
    [Fact]
    public async Task SendAsApiResult_ObjectOverload_DoesNotCatchHandlerExceptions()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.SendAsApiResult(new MappingThrowingRequest()));

        Assert.Equal(MappingThrowingRequestHandler.Message, exception.Message);
    }

    [Fact]
    public async Task SendAsApiResult_TypedOverload_DoesNotCatchHandlerExceptions()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.SendAsApiResult<MappingThrowingRequest, MappingPayload>(new MappingThrowingRequest()));

        Assert.Equal(MappingThrowingRequestHandler.Message, exception.Message);
    }

    // The void overload awaits Send and then builds the empty result itself, so it is the overload most
    // likely to accidentally turn a failure into a 204.
    [Fact]
    public async Task SendAsApiResult_VoidOverload_DoesNotCatchHandlerExceptions()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<HttpResponseException>(
            () => sender.SendAsApiResult<MappingThrowingVoidRequest>(new MappingThrowingVoidRequest()));

        Assert.Equal(HttpStatusCode.NotFound, exception.HttpStatusCode);
    }

    // The endpoint's CancellationToken has to reach the handler, otherwise a client disconnect keeps the
    // database work running to completion.
    [Fact]
    public async Task SendAsApiResult_ForwardsTheCancellationTokenToTheHandler()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sender.SendAsApiResult(new MappingCancellationRequest(), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sender.SendAsApiResult<MappingCancellationRequest, string>(new MappingCancellationRequest(), cancelled.Token));
    }

    [Fact]
    public async Task SendAsApiResult_VoidOverload_ForwardsTheCancellationTokenToTheHandler()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sender.SendAsApiResult<MappingCancellationVoidRequest>(new MappingCancellationVoidRequest(), cancelled.Token));
    }

    // ---------------------------------------------------------------------------------------------
    // SendAsApiResult: the IMediatorOptionsAccessor contract
    // ---------------------------------------------------------------------------------------------

    // This is exactly what the IMediatorOptionsAccessor doc comment promises: decorate ISender without
    // implementing it and empty responses silently fall back to 204, even though the app configured 200
    // and the endpoint helpers advertise 200 in OpenAPI.
    [Fact]
    public async Task SendAsApiResult_SenderWithoutOptionsAccessor_FallsBackToNoContent()
    {
        using var provider = CreateProvider(EmptyResponseStatusCode.Ok);
        using var scope = provider.CreateScope();
        var wrapper = new MappingPlainSenderWrapper(scope.ServiceProvider.GetRequiredService<ISender>());

        await AssertEmptyResponseAsync(
            await wrapper.SendAsApiResult((object)new MappingVoidRequest()),
            StatusCodes.Status204NoContent);
        await AssertEmptyResponseAsync(
            await wrapper.SendAsApiResult<MappingNullResponseRequest, string?>(new MappingNullResponseRequest()),
            StatusCodes.Status204NoContent);
        await AssertEmptyResponseAsync(
            await wrapper.SendAsApiResult<MappingVoidRequest>(new MappingVoidRequest()),
            StatusCodes.Status204NoContent);
    }

    // The fallback is about the empty-response status only — a decorator must not change how a real
    // payload is mapped.
    [Fact]
    public async Task SendAsApiResult_SenderWithoutOptionsAccessor_StillMapsNonEmptyResponsesNormally()
    {
        using var provider = CreateProvider(EmptyResponseStatusCode.Ok);
        using var scope = provider.CreateScope();
        var wrapper = new MappingPlainSenderWrapper(scope.ServiceProvider.GetRequiredService<ISender>());

        var executed = await ResultExecution.ExecuteAsync(
            await wrapper.SendAsApiResult(new MappingPayloadRequest { Value = "through-wrapper" }));

        Assert.Equal(StatusCodes.Status200OK, executed.StatusCode);
        Assert.Equal("through-wrapper", executed.Json.GetProperty("name").GetString());
    }

    // ...and the documented fix: implement IMediatorOptionsAccessor on the decorator and forward, and the
    // configured status is honored again.
    [Fact]
    public async Task SendAsApiResult_SenderForwardingTheOptionsAccessor_HonorsTheConfiguredStatus()
    {
        using var provider = CreateProvider(EmptyResponseStatusCode.Ok);
        using var scope = provider.CreateScope();
        var wrapper = new MappingForwardingSenderWrapper(scope.ServiceProvider.GetRequiredService<ISender>());

        await AssertEmptyResponseAsync(
            await wrapper.SendAsApiResult((object)new MappingVoidRequest()),
            StatusCodes.Status200OK);
        await AssertEmptyResponseAsync(
            await wrapper.SendAsApiResult<MappingNullResponseRequest, string?>(new MappingNullResponseRequest()),
            StatusCodes.Status200OK);
        await AssertEmptyResponseAsync(
            await wrapper.SendAsApiResult<MappingVoidRequest>(new MappingVoidRequest()),
            StatusCodes.Status200OK);
    }

    // Options coming from a hand-written accessor never went through MediatorOptionsValidator, so this is
    // the one way an undefined status reaches the mapper at request time.
    [Fact]
    public async Task SendAsApiResult_ObjectOverloadWithAnUndefinedAccessorStatus_Throws()
    {
        var sender = new MappingUnvalidatedOptionsSender((EmptyResponseStatusCode)201);

        // Cast to object so this is the ISender/object overload and not the generic void one.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.SendAsApiResult((object)new MappingVoidRequest()));

        Assert.Contains("200 OK", exception.Message);
        Assert.Contains("204 No Content", exception.Message);
    }

    [Fact]
    public async Task SendAsApiResult_VoidOverloadWithAnUndefinedAccessorStatus_Throws()
    {
        var sender = new MappingUnvalidatedOptionsSender((EmptyResponseStatusCode)201);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.SendAsApiResult<MappingVoidRequest>(new MappingVoidRequest()));

        Assert.Contains("200 OK", exception.Message);
        Assert.Contains("204 No Content", exception.Message);
    }

    // The third overload reaches the same guard by a different route — through a null response rather than
    // through a void request — so it needs its own case or one of the three can silently stop checking.
    [Fact]
    public async Task SendAsApiResult_TypedOverloadWithAnUndefinedAccessorStatus_Throws()
    {
        var sender = new MappingUnvalidatedOptionsSender((EmptyResponseStatusCode)201);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.SendAsApiResult<MappingNullResponseRequest, string?>(new MappingNullResponseRequest()));

        Assert.Contains("200 OK", exception.Message);
        Assert.Contains("204 No Content", exception.Message);
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

    /// <summary>An empty response carries the status and nothing else — no body, no content type.</summary>
    private static async Task AssertEmptyResponseAsync(IResult result, int expectedStatusCode)
    {
        var executed = await ResultExecution.ExecuteAsync(result);

        Assert.Equal(expectedStatusCode, executed.StatusCode);
        Assert.Equal(string.Empty, executed.Body);
        Assert.Null(executed.ContentType);
    }

    /// <summary>
    /// A container with the mediator and only this file's handlers. Registered by hand rather than by
    /// scanning this assembly, which would also pull in every sibling test file's handlers.
    /// </summary>
    private static ServiceProvider CreateProvider(EmptyResponseStatusCode? emptyResponseStatusCode = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        if (emptyResponseStatusCode is { } configured)
            services.AddMediator(options => options.EmptyResponseStatusCode = configured);
        else
            services.AddMediator();

        services.AddTransient<IRequestHandler<MappingPayloadRequest, MappingPayload>, MappingPayloadRequestHandler>();
        services.AddTransient<IRequestHandler<MappingNullResponseRequest, string?>, MappingNullResponseRequestHandler>();
        services.AddTransient<IRequestHandler<MappingResultRequest, IResult>, MappingResultRequestHandler>();
        services.AddTransient<IRequestHandler<MappingErrorResponseRequest, ErrorResponse>, MappingErrorResponseRequestHandler>();
        services.AddTransient<IRequestHandler<MappingThrowingRequest, MappingPayload>, MappingThrowingRequestHandler>();
        services.AddTransient<IRequestHandler<MappingCancellationRequest, string>, MappingCancellationRequestHandler>();
        services.AddTransient<IRequestHandler<MappingNullableIntRequest, int?>, MappingNullableIntRequestHandler>();
        services.AddTransient<IRequestHandler<MappingPagedRequest, MultiResponse<string>>, MappingPagedRequestHandler>();
        services.AddTransient<IRequestHandler<MappingShortCircuitRequest, object>, MappingShortCircuitRequestHandler>();
        // Only ever runs for MappingShortCircuitRequest, so every other test here still sees a bare pipeline.
        services.AddTransient<IPipelineBehavior<MappingShortCircuitRequest, object>, MappingShortCircuitBehavior>();
        services.AddTransient<IRequestHandler<MappingVoidRequest>, MappingVoidRequestHandler>();
        services.AddTransient<IRequestHandler<MappingThrowingVoidRequest>, MappingThrowingVoidRequestHandler>();
        services.AddTransient<IRequestHandler<MappingCancellationVoidRequest>, MappingCancellationVoidRequestHandler>();

        return services.BuildServiceProvider(validateScopes: true);
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

public sealed class MappingCancellationRequest : IRequest<string>
{
}

public sealed class MappingCancellationRequestHandler : IRequestHandler<MappingCancellationRequest, string>
{
    public Task<string> Handle(MappingCancellationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult("was not cancelled");
    }
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

public sealed class MappingCancellationVoidRequest : IRequest
{
}

public sealed class MappingCancellationVoidRequestHandler : IRequestHandler<MappingCancellationVoidRequest>
{
    public Task Handle(MappingCancellationVoidRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
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

/// <summary>An ISender decorator that forgets to implement IMediatorOptionsAccessor.</summary>
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

/// <summary>The decorator the IMediatorOptionsAccessor doc comment asks for: it forwards the options.</summary>
public sealed class MappingForwardingSenderWrapper(ISender inner) : ISender, IMediatorOptionsAccessor
{
    public MediatorOptions Options => inner is IMediatorOptionsAccessor accessor
        ? accessor.Options
        : throw new InvalidOperationException("The inner sender does not expose mediator options.");

    public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        => inner.Send(request, cancellationToken);

    public Task<TResponse> Send<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest<TResponse>
        => inner.Send<TRequest, TResponse>(request, cancellationToken);

    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest
        => inner.Send<TRequest>(request, cancellationToken);
}

/// <summary>
/// An ISender that reports options MediatorOptionsValidator never saw — what a hand-rolled accessor can
/// do. It never reaches a handler; only the options it exposes matter here.
/// </summary>
public sealed class MappingUnvalidatedOptionsSender(EmptyResponseStatusCode emptyResponseStatusCode) : ISender, IMediatorOptionsAccessor
{
    public MediatorOptions Options { get; } = new MediatorOptions { EmptyResponseStatusCode = emptyResponseStatusCode };

    public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        => Task.FromResult<object?>(null);

    public Task<TResponse> Send<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest<TResponse>
        => Task.FromResult<TResponse>(default!);

    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest
        => Task.CompletedTask;
}
