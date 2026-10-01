using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Tests.Infrastructure;
using Phoenix.Mediator.Web;
using Phoenix.Mediator.Web.Dtos;
using Phoenix.Mediator.Wrappers;
using Xunit;
using FromRouteAttribute = Microsoft.AspNetCore.Mvc.FromRouteAttribute;
using FromServicesAttribute = Microsoft.AspNetCore.Mvc.FromServicesAttribute;

namespace Phoenix.Mediator.Tests;

/// <summary>
/// The <c>Get</c>/<c>Post</c>/<c>Put</c>/<c>Delete</c>/<c>Patch</c> helpers exist so every endpoint gets the
/// same OpenAPI responses without anyone repeating them: the four error responses, plus a success response
/// inferred from the mediator request the delegate takes. Everything these assert is what a published
/// OpenAPI document ends up saying, so a regression here misleads every client generated from it.
/// </summary>
public sealed class VerbEndpointTests
{
    // ------------------------------------------------------------------
    // Routing: each helper maps its own method and pattern
    // ------------------------------------------------------------------

    // A helper that mapped the wrong verb would silently send, say, every update to the delete route.
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task VerbHelpers_MapTheirOwnHttpMethodAndPattern_OnTheApplication(string httpMethod)
    {
        await using var app = CreateApp();

        MapVerb(app, httpMethod, "verb-direct/ping", () => Results.Ok());

        var endpoint = app.Endpoint("verb-direct/ping");

        Assert.Equal(httpMethod, Assert.Single(HttpMethodsOf(endpoint)));
        Assert.Equal("verb-direct/ping", endpoint.RoutePattern.RawText!.Trim('/'));
    }

    // Endpoint groups are the documented way to use these helpers, and a group contributes its own
    // prefix and its own service provider, so the helpers have to work through IEndpointRouteBuilder
    // rather than only on WebApplication.
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task VerbHelpers_MapTheirOwnHttpMethodAndPattern_OnARouteGroup(string httpMethod)
    {
        await using var app = CreateApp();

        MapVerb(app.MapGroup("verb-group"), httpMethod, "ping", () => Results.Ok());

        var endpoint = app.Endpoint("ping");

        Assert.Equal(httpMethod, Assert.Single(HttpMethodsOf(endpoint)));
        Assert.Equal("verb-group/ping", endpoint.RoutePattern.RawText!.Trim('/'));
    }

    // The helpers return the RouteHandlerBuilder so callers keep the whole Minimal API surface
    // (.WithName, .RequireAuthorization, .WithTags, ...). Returning anything else would force callers
    // back onto MapGet/MapPost and lose the default responses.
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task VerbHelpers_ReturnABuilderThatKeepsChaining(string httpMethod)
    {
        await using var app = CreateApp();

        MapVerb(app, httpMethod, "verb-chained/ping", () => Results.Ok())
            .WithName("verb-chained-" + httpMethod)
            .RequireAuthorization();

        var endpoint = app.Endpoint("verb-chained/ping");

        Assert.Equal("verb-chained-" + httpMethod, endpoint.Metadata.GetMetadata<EndpointNameMetadata>()?.EndpointName);
        Assert.Contains(endpoint.Metadata, static metadata => metadata is IAuthorizeData);
        // The conventions the caller chains on run after the helper's, so the defaults have to survive them.
        Assert.Contains(StatusCodes.Status401Unauthorized, StatusCodesOf(endpoint));
    }

    // ------------------------------------------------------------------
    // Default OpenAPI responses
    // ------------------------------------------------------------------

    // Exactly one entry each: the helper declares them itself, so a second Produces(401) would publish a
    // duplicated response in the document instead of a louder promise.
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task VerbHelpers_AlwaysDeclareUnauthorizedAndForbidden(string httpMethod)
    {
        await using var app = CreateApp();

        MapVerb(app, httpMethod, "verb-defaults/ping", () => Results.Ok());

        var responses = ResponsesOf(app.Endpoint("verb-defaults/ping"));

        Assert.Single(responses, static metadata => metadata.StatusCode == StatusCodes.Status401Unauthorized);
        Assert.Single(responses, static metadata => metadata.StatusCode == StatusCodes.Status403Forbidden);
    }

    // The middleware answers both of these with an ErrorsResponse body, so the schema has to say so:
    // a client generated from a document that only lists the status code has nothing to deserialize
    // the errors/traceId into.
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task VerbHelpers_DeclareErrorsResponseAsJsonForBadRequestAndServerError(string httpMethod)
    {
        await using var app = CreateApp();

        MapVerb(app, httpMethod, "verb-errors/ping", () => Results.Ok());

        var endpoint = app.Endpoint("verb-errors/ping");

        AssertDeclaresErrorBody(endpoint, StatusCodes.Status400BadRequest);
        AssertDeclaresErrorBody(endpoint, StatusCodes.Status500InternalServerError);
    }

    // Explicit success responses replace the inferred ones only; the error contract is not the caller's
    // to opt out of, because the middleware still answers with it.
    [Fact]
    public async Task VerbHelpers_KeepTheDefaultErrorResponses_AlongsideExplicitResponses()
    {
        await using var app = CreateApp();

        app.Post("verb-errors/explicit", () => Results.Ok(), new ResponseDto(StatusCodes.Status201Created, typeof(VerbPayload)));

        var endpoint = app.Endpoint("verb-errors/explicit");

        AssertDeclaresErrorBody(endpoint, StatusCodes.Status400BadRequest);
        AssertDeclaresErrorBody(endpoint, StatusCodes.Status500InternalServerError);
        Assert.Contains(StatusCodes.Status401Unauthorized, StatusCodesOf(endpoint));
        Assert.Contains(StatusCodes.Status403Forbidden, StatusCodesOf(endpoint));
    }

    // ------------------------------------------------------------------
    // Success inference: IRequest<TResponse> => 200 declaring TResponse
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task VerbHelpers_InferOkWithTheResponseType_FromAnIRequestOfTResponseParameter(string httpMethod)
    {
        await using var app = CreateApp();

        MapVerb(app, httpMethod, "verb-inferred/ping", ([AsParameters] VerbClassRequest request) => Results.Ok(request.Value));

        var endpoint = app.Endpoint("verb-inferred/ping");

        // Exactly one 200: a second, schema-less one would let a generator pick the empty body.
        AssertInfersOk(endpoint, typeof(VerbPayload));
        // Deliberate: a request with a response must not also advertise 204, or Swagger shows both for an
        // endpoint that always returns a body.
        Assert.DoesNotContain(StatusCodes.Status204NoContent, StatusCodesOf(endpoint));
    }

    // The inference reads the interface list of the declared parameter type, so the shape the request is
    // written in must not change the document: a class, a record, a positional record (whose members live on
    // constructor parameters rather than declared properties, which other reflection in this package has to
    // special-case), one whose members carry binding attributes, or a record with no response at all.
    [Fact]
    public async Task VerbHelpers_InferTheSuccessResponse_WhateverShapeTheRequestTypeHas()
    {
        await using var app = CreateApp();
        var group = app.MapGroup("verb-shapes");

        group.Post("record", (VerbRecordRequest request) => Results.Ok());
        group.Post("positional", (VerbPositionalRequest request) => Results.Ok());
        group.Patch("annotated/{Id}", ([AsParameters] VerbAnnotatedRequest request) => Results.Ok(request.Id));
        group.Post("void-record", (VerbVoidRecordRequest request) => Results.NoContent());

        AssertInfersOk(app.Endpoint("verb-shapes/record"), typeof(VerbPayload));
        AssertInfersOk(app.Endpoint("verb-shapes/positional"), typeof(VerbPayload));
        AssertInfersOk(app.Endpoint("verb-shapes/annotated/{Id}"), typeof(VerbPayload));
        Assert.Equal(StatusCodes.Status204NoContent, Assert.Single(SuccessStatusCodesOf(app.Endpoint("verb-shapes/void-record"))));
    }

    // The interface can arrive through another interface; GetInterfaces() is transitive, so a request that
    // only names a domain-specific contract still documents its response type.
    [Fact]
    public async Task VerbHelpers_InferOkWithTheResponseType_WhenIRequestOfTComesFromAnInheritedInterface()
    {
        await using var app = CreateApp();

        app.Post("verb-shapes/inherited", (VerbInheritedInterfaceRequest request) => Results.Ok());

        AssertInfersOk(app.Endpoint("verb-shapes/inherited"), typeof(VerbPayload));
    }

    // IPagedRequest<TItem> is the package's own IRequest<MultiResponse<TItem>>, and paged queries are the
    // main reason inheritance through an interface has to work: the document must describe the envelope
    // (data/totalCount/pagesCount) clients actually receive, not the item type.
    [Fact]
    public async Task VerbHelpers_InferOkWithTheMultiResponseEnvelope_ForAPagedRequest()
    {
        await using var app = CreateApp();

        app.Get("verb-paged/list", ([AsParameters] VerbPagedRequest request) => Results.Ok());

        AssertInfersOk(app.Endpoint("verb-paged/list"), typeof(MultiResponse<VerbPayload>));
    }

    // A generic request closes over its own type argument, which has nothing to do with TResponse. Reading
    // the response type from the parameter's generic arguments (rather than from the IRequest<> it
    // implements) would document VerbOtherPayload here - a body the endpoint never returns.
    [Fact]
    public async Task VerbHelpers_InferOkWithTheResponseType_ForAClosedGenericRequestType()
    {
        await using var app = CreateApp();

        app.Post("verb-generic/closed", (VerbGenericRequest<VerbOtherPayload> request) => Results.Ok());

        AssertInfersOk(app.Endpoint("verb-generic/closed"), typeof(VerbPayload));
    }

    // A request implementing both interfaces is dispatched through IRequest<TResponse>, so the document has
    // to show the body, not an empty 204.
    [Fact]
    public async Task VerbHelpers_PreferTheResponseType_WhenARequestImplementsBothInterfaces()
    {
        await using var app = CreateApp();

        app.Post("verb-shapes/dual", (VerbDualRequest request) => Results.Ok());

        var endpoint = app.Endpoint("verb-shapes/dual");

        AssertInfersOk(endpoint, typeof(VerbPayload));
        Assert.DoesNotContain(StatusCodes.Status204NoContent, StatusCodesOf(endpoint));
    }

    // The canonical delegate the README shows puts ISender first and the request second, so the scan has to
    // walk past parameters that are not requests. Taking the first parameter instead would leave every
    // documented endpoint in a real app without a success response.
    [Fact]
    public async Task VerbHelpers_InferOkWithTheResponseType_WhenTheRequestIsNotTheFirstParameter()
    {
        await using var app = CreateApp();

        app.Get("verb-order/ping", (ISender sender, [AsParameters] VerbClassRequest request, CancellationToken cancellationToken) => Results.Ok());

        AssertInfersOk(app.Endpoint("verb-order/ping"), typeof(VerbPayload));
    }

    // ------------------------------------------------------------------
    // Success inference: IRequest => the configured empty status
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task VerbHelpers_InferNoContent_FromAnIRequestParameter(string httpMethod)
    {
        await using var app = CreateApp();

        MapVerb(app, httpMethod, "verb-empty/ping", ([AsParameters] VerbVoidRequest request) => Results.NoContent());

        // The single success response is the whole contract here: advertising 200 as well would document a
        // body for an endpoint that never writes one.
        Assert.Equal(StatusCodes.Status204NoContent, Assert.Single(SuccessStatusCodesOf(app.Endpoint("verb-empty/ping"))));
    }

    // The empty status is a per-application setting; the helpers must read the same MediatorOptions the
    // runtime mapping (the helpers' result filter) uses, or the document promises a status the app never returns.
    [Theory]
    [InlineData(EmptyResponseStatusCode.NoContent, StatusCodes.Status204NoContent)]
    [InlineData(EmptyResponseStatusCode.Ok, StatusCodes.Status200OK)]
    public async Task VerbHelpers_InferTheConfiguredEmptyStatus_FromAnIRequestParameter(EmptyResponseStatusCode configured, int expectedStatusCode)
    {
        await using var app = CreateApp(options => options.EmptyResponseStatusCode = configured);

        app.Post("verb-empty/configured", ([AsParameters] VerbVoidRequest request) => Results.NoContent());

        Assert.Equal(expectedStatusCode, Assert.Single(SuccessStatusCodesOf(app.Endpoint("verb-empty/configured"))));
    }

    // The helpers are usable on an app that never registered the mediator (endpoints that only map routes,
    // or a partially migrated app). Resolving the options with GetRequiredService, or defaulting to anything
    // but 204, would break startup or the document for those apps.
    [Fact]
    public async Task VerbHelpers_InferNoContent_WhenAddMediatorWasNeverCalled()
    {
        await using var app = CreateAppWithoutMediator();

        app.Post("verb-empty/unregistered", ([AsParameters] VerbVoidRequest request) => Results.NoContent());

        Assert.Equal(StatusCodes.Status204NoContent, Assert.Single(SuccessStatusCodesOf(app.Endpoint("verb-empty/unregistered"))));
    }

    // Configuring 200 for empty responses must not make a request that *does* have a response advertise two
    // 200 entries, one of them schema-less: the empty status is only ever a fallback.
    [Fact]
    public async Task VerbHelpers_InferOnlyTheTypedOk_WhenTheEmptyStatusIsConfiguredToOk()
    {
        await using var app = CreateApp(options => options.EmptyResponseStatusCode = EmptyResponseStatusCode.Ok);

        app.Post("verb-empty/typed", ([AsParameters] VerbClassRequest request) => Results.Ok());

        AssertInfersOk(app.Endpoint("verb-empty/typed"), typeof(VerbPayload));
    }

    // A route group resolves services through the application's provider. If the helper read options from
    // anywhere else, group-mapped endpoints would document 204 while the app returns 200.
    [Fact]
    public async Task VerbHelpers_ReadTheConfiguredEmptyStatus_ThroughARouteGroupsServiceProvider()
    {
        await using var app = CreateApp(options => options.EmptyResponseStatusCode = EmptyResponseStatusCode.Ok);

        app.MapGroup("verb-empty-group").Post("complete", ([AsParameters] VerbVoidRequest request) => Results.Ok());

        var statusCodes = StatusCodesOf(app.Endpoint("verb-empty-group/complete"));

        Assert.Contains(StatusCodes.Status200OK, statusCodes);
        Assert.DoesNotContain(StatusCodes.Status204NoContent, statusCodes);
    }

    // Two endpoints mapped through the same group must not share success responses: the conventions belong
    // to each route, not to the group.
    [Fact]
    public async Task VerbHelpers_GiveEachEndpointItsOwnSuccessResponse()
    {
        await using var app = CreateApp();
        var group = app.MapGroup("verb-mixed");

        group.Post("create", (VerbClassRequest request) => Results.Ok());
        group.Delete("remove", ([AsParameters] VerbVoidRequest request) => Results.NoContent());

        var create = StatusCodesOf(app.Endpoint("verb-mixed/create"));
        var remove = StatusCodesOf(app.Endpoint("verb-mixed/remove"));

        Assert.Contains(StatusCodes.Status200OK, create);
        Assert.DoesNotContain(StatusCodes.Status204NoContent, create);
        Assert.Contains(StatusCodes.Status204NoContent, remove);
        Assert.DoesNotContain(StatusCodes.Status200OK, remove);
    }

    // ------------------------------------------------------------------
    // Success inference: parameters that are not a concrete request
    // ------------------------------------------------------------------

    // Delegates that build their own response (health probes, redirects, endpoints that take only an
    // ISender) get the error contract but nothing invented for success.
    [Fact]
    public async Task VerbHelpers_InferNoSuccessResponse_WhenTheDelegateTakesNoMediatorRequest()
    {
        await using var app = CreateApp();

        app.Get("verb-none/ping", (ISender sender, CancellationToken cancellationToken) => Results.Ok());

        Assert.Empty(SuccessStatusCodesOf(app.Endpoint("verb-none/ping")));
    }

    // A plain DTO parameter is not a mediator request; treating one as such would document a response type
    // taken from an arbitrary body parameter.
    [Fact]
    public async Task VerbHelpers_InferNoSuccessResponse_ForABodyParameterThatIsNotAMediatorRequest()
    {
        await using var app = CreateApp();

        app.Post("verb-none/plain", (VerbPayload payload) => Results.Ok());

        Assert.Empty(SuccessStatusCodesOf(app.Endpoint("verb-none/plain")));
    }

    // The scan takes the first mediator-typed parameter in declaration order, so the delegate's signature
    // decides which request is documented when more than one is bound.
    [Fact]
    public async Task VerbHelpers_InferFromTheFirstMediatorParameter_WhenTheDelegateDeclaresSeveral()
    {
        await using var app = CreateApp();

        app.Get("verb-none/two", ([AsParameters] VerbVoidRequest first, [AsParameters] VerbClassRequest second) => Results.Ok());

        var statusCodes = StatusCodesOf(app.Endpoint("verb-none/two"));

        Assert.Contains(StatusCodes.Status204NoContent, statusCodes);
        Assert.DoesNotContain(StatusCodes.Status200OK, statusCodes);
    }

    // The non-generic interface is accepted as the declared parameter type itself: IRequest is assignable
    // from IRequest, so the empty-response status is still inferred.
    [Fact]
    public async Task VerbHelpers_InferTheEmptyStatus_ForAParameterDeclaredAsIRequest()
    {
        await using var app = CreateApp(configureServices: static services => services.AddSingleton<IRequest>(new VerbVoidRequest()));

        // Bound from services because an interface cannot be bound from the JSON body; the inference only
        // ever looks at the declared parameter type.
        app.Post("verb-interface/void", ([FromServices] IRequest request) => Results.Ok());

        Assert.Contains(StatusCodes.Status204NoContent, StatusCodesOf(app.Endpoint("verb-interface/void")));
    }

    // A parameter can be declared as the interface rather than the concrete request. The two probes used
    // to disagree about it: the "is this a request" check accepted IRequest<TResponse> itself, while the
    // response-type lookup searched the type's *implemented* interfaces — and an interface does not
    // implement itself — so the endpoint declared no success response at all, against the README's
    // "IRequest<T> => 200". Both now read the response type from the same place.
    [Fact]
    public async Task VerbHelpers_InferOkWithTheResponseType_ForAParameterDeclaredAsIRequestOfTResponse()
    {
        await using var app = CreateApp(configureServices: static services =>
            services.AddSingleton<IRequest<VerbPayload>>(new VerbClassRequest()));

        app.Post("verb-interface/typed", ([FromServices] IRequest<VerbPayload> request) => Results.Ok());

        var endpoint = app.Endpoint("verb-interface/typed");

        AssertInfersOk(endpoint, typeof(VerbPayload));
        AssertDeclaresErrorBody(endpoint, StatusCodes.Status400BadRequest);
    }

    // The same case one step removed: a parameter declared as a domain contract that *extends*
    // IRequest<TResponse>. This one never needed the self-reference fix — an interface does list the
    // interfaces it inherits — and asserting it keeps that special case scoped to IRequest<T> itself.
    [Fact]
    public async Task VerbHelpers_InferOkWithTheResponseType_ForAParameterDeclaredAsADerivedRequestInterface()
    {
        await using var app = CreateApp(configureServices: static services =>
            services.AddSingleton<VerbContracts.IQuery>(new VerbInheritedInterfaceRequest()));

        app.Post("verb-interface/derived", ([FromServices] VerbContracts.IQuery request) => Results.Ok());

        AssertInfersOk(app.Endpoint("verb-interface/derived"), typeof(VerbPayload));
    }

    // ------------------------------------------------------------------
    // Removing the 200 Minimal APIs infers from the return type
    // ------------------------------------------------------------------

    // Precondition for the removal tests below, and the reason the removal exists at all: Minimal APIs add a
    // 200 of their own for any delegate that does not return an IResult. If this ever stops being true, the
    // removal tests stop proving anything and this one says so.
    [Fact]
    public async Task MinimalApis_InferOk_FromANonResultReturnType()
    {
        await using var app = CreateApp();

        app.MapPost("verb-probe/inferred", ([AsParameters] VerbVoidRequest request) => new VerbPayload("done"));

        Assert.Contains(StatusCodes.Status200OK, StatusCodesOf(app.Endpoint("verb-probe/inferred")));
    }

    // Without the removal, a void request would be documented as returning both 200 and 204 while the app
    // only ever returns 204.
    [Fact]
    public async Task AddResponses_RemovesTheInferredOk_WhenTheSuccessStatusIsNotOk()
    {
        await using var app = CreateApp();

        app.Post("verb-removal/complete", ([AsParameters] VerbVoidRequest request) => new VerbPayload("done"));

        var statusCodes = StatusCodesOf(app.Endpoint("verb-removal/complete"));

        Assert.Contains(StatusCodes.Status204NoContent, statusCodes);
        Assert.DoesNotContain(StatusCodes.Status200OK, statusCodes);
    }

    // The mirror image: when 200 is the declared success status nothing is stripped, so a return type the
    // framework documented on its own survives next to the inferred response type.
    [Fact]
    public async Task AddResponses_KeepsTheInferredOk_WhenTheSuccessStatusIsOk()
    {
        await using var app = CreateApp();

        // The delegate returns a different type than the request's TResponse purely so the two 200 entries
        // can be told apart.
        app.Post("verb-removal/keep", ([AsParameters] VerbClassRequest request) => new VerbOtherPayload("done"));

        var okResponses = ResponsesOf(app.Endpoint("verb-removal/keep"))
            .Where(static metadata => metadata.StatusCode == StatusCodes.Status200OK)
            .ToArray();

        Assert.Contains(okResponses, static metadata => metadata.Type == typeof(VerbPayload));
        Assert.Contains(okResponses, static metadata => metadata.Type == typeof(VerbOtherPayload));
    }

    [Fact]
    public async Task AddResponses_RemovesTheInferredOk_WhenExplicitResponsesOmitIt()
    {
        await using var app = CreateApp();

        app.Post("verb-removal/explicit", ([AsParameters] VerbClassRequest request) => new VerbPayload("done"),
            new ResponseDto(StatusCodes.Status202Accepted, null));

        var statusCodes = StatusCodesOf(app.Endpoint("verb-removal/explicit"));

        Assert.Contains(StatusCodes.Status202Accepted, statusCodes);
        Assert.DoesNotContain(StatusCodes.Status200OK, statusCodes);
    }

    // The same decision taken from the explicit list rather than from inference: a caller who declares 200
    // themselves keeps whatever the framework documented from the return type. A removal that ran whenever
    // explicit responses were given would silently drop it.
    [Fact]
    public async Task AddResponses_KeepsTheInferredOk_WhenExplicitResponsesDeclareIt()
    {
        await using var app = CreateApp();

        app.Post("verb-removal/explicit-ok", ([AsParameters] VerbVoidRequest request) => new VerbOtherPayload("done"),
            new ResponseDto(StatusCodes.Status200OK, typeof(VerbPayload)));

        var okResponses = ResponsesOf(app.Endpoint("verb-removal/explicit-ok"))
            .Where(static metadata => metadata.StatusCode == StatusCodes.Status200OK)
            .ToArray();

        Assert.Contains(okResponses, static metadata => metadata.Type == typeof(VerbPayload));
        Assert.Contains(okResponses, static metadata => metadata.Type == typeof(VerbOtherPayload));
        // The empty status the request would otherwise have contributed is not added next to it.
        Assert.DoesNotContain(StatusCodes.Status204NoContent, StatusCodesOf(app.Endpoint("verb-removal/explicit-ok")));
    }

    // Nothing to infer means nothing to remove either: the framework's own 200 is left alone rather than
    // stripped from an endpoint the helper decided not to describe.
    [Fact]
    public async Task AddResponses_KeepsTheInferredOk_WhenTheDelegateTakesNoMediatorRequest()
    {
        await using var app = CreateApp();

        app.Get("verb-removal/untouched", () => new VerbPayload("done"));

        Assert.Contains(ResponsesOf(app.Endpoint("verb-removal/untouched")), static metadata =>
            metadata.StatusCode == StatusCodes.Status200OK && metadata.Type == typeof(VerbPayload));
    }

    // Conventions run in the order they are added, so a 200 the caller declares after the helper is not
    // swept away by the removal. An endpoint that really can answer 200 or 204 stays documentable.
    [Fact]
    public async Task AddResponses_KeepsAnOkTheCallerDeclaresAfterTheHelper()
    {
        await using var app = CreateApp();

        app.Post("verb-removal/after", ([AsParameters] VerbVoidRequest request) => new VerbPayload("done"))
            .Produces<VerbPayload>(StatusCodes.Status200OK);

        var statusCodes = StatusCodesOf(app.Endpoint("verb-removal/after"));

        Assert.Contains(StatusCodes.Status200OK, statusCodes);
        Assert.Contains(StatusCodes.Status204NoContent, statusCodes);
    }

    // ------------------------------------------------------------------
    // Explicit ResponseDto arrays
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task VerbHelpers_ExplicitResponsesReplaceTheInferredSuccessResponse(string httpMethod)
    {
        await using var app = CreateApp();

        MapVerb(app, httpMethod, "verb-explicit/ping", ([AsParameters] VerbClassRequest request) => Results.Ok(),
            new ResponseDto(StatusCodes.Status201Created, typeof(VerbPayload)));

        var endpoint = app.Endpoint("verb-explicit/ping");

        Assert.Equal(typeof(VerbPayload),
            Assert.Single(ResponsesOf(endpoint), static metadata => metadata.StatusCode == StatusCodes.Status201Created).Type);
        // 200 would have been inferred from IRequest<VerbPayload> had the explicit set not won.
        Assert.DoesNotContain(StatusCodes.Status200OK, StatusCodesOf(endpoint));
    }

    // Each entry lands exactly once and keeps its own schema: an explicit list is what a caller falls back on
    // when the inferred response is wrong, so losing or duplicating one of them is the whole feature failing.
    [Fact]
    public async Task AddResponses_AddsEveryExplicitResponse()
    {
        await using var app = CreateApp();

        app.Post("verb-explicit/many", () => Results.Ok(),
            new ResponseDto(StatusCodes.Status201Created, typeof(VerbPayload)),
            new ResponseDto(StatusCodes.Status202Accepted, null),
            new ResponseDto(StatusCodes.Status409Conflict, typeof(ErrorsResponse)));

        var responses = ResponsesOf(app.Endpoint("verb-explicit/many"));

        Assert.Equal(typeof(VerbPayload),
            Assert.Single(responses, static metadata => metadata.StatusCode == StatusCodes.Status201Created).Type);
        Assert.Single(responses, static metadata => metadata.StatusCode == StatusCodes.Status202Accepted);
        Assert.Equal(typeof(ErrorsResponse),
            Assert.Single(responses, static metadata => metadata.StatusCode == StatusCodes.Status409Conflict).Type);
    }

    // A typed explicit response has to end up as a JSON body, the same as the built-in error responses;
    // otherwise the schema is attached to no content type and generators drop it.
    [Fact]
    public async Task AddResponses_DeclaresExplicitTypedResponsesAsJson()
    {
        await using var app = CreateApp();

        app.Post("verb-explicit/json", () => Results.Ok(), new ResponseDto(StatusCodes.Status201Created, typeof(VerbPayload)));

        var created = Assert.Single(ResponsesOf(app.Endpoint("verb-explicit/json")),
            static metadata => metadata.StatusCode == StatusCodes.Status201Created);

        Assert.Contains("application/json", created.ContentTypes);
    }

    // ResponseDto(status, null) is how a caller says "this status carries no body" (202 Accepted, 204).
    [Fact]
    public async Task AddResponses_ExplicitResponseWithoutATypeDeclaresNoBody()
    {
        await using var app = CreateApp();

        app.Post("verb-explicit/empty", () => Results.Ok(), new ResponseDto(StatusCodes.Status202Accepted, null));

        var accepted = Assert.Single(ResponsesOf(app.Endpoint("verb-explicit/empty")),
            static metadata => metadata.StatusCode == StatusCodes.Status202Accepted);

        // Minimal APIs normalize "no declared type" to void rather than null.
        Assert.True(accepted.Type is null || accepted.Type == typeof(void),
            $"Expected no declared body type, found {accepted.Type}.");
    }

    // params gives an empty array when the caller passes nothing, so "empty" has to mean "infer", not
    // "declare no success response at all".
    [Fact]
    public async Task AddResponses_FallsBackToInference_ForAnEmptyExplicitArray()
    {
        await using var app = CreateApp();

        app.Post("verb-explicit/none", ([AsParameters] VerbClassRequest request) => Results.Ok(), Array.Empty<ResponseDto>());

        AssertInfersOk(app.Endpoint("verb-explicit/none"), typeof(VerbPayload));
    }

    // The parameter is declared nullable, and a caller forwarding an optional array must not lose the
    // inferred response (or hit a NullReferenceException).
    [Fact]
    public async Task AddResponses_FallsBackToInference_ForANullExplicitArray()
    {
        await using var app = CreateApp();

        app.Post("verb-explicit/null", ([AsParameters] VerbClassRequest request) => Results.Ok(), (ResponseDto[]?)null);

        AssertInfersOk(app.Endpoint("verb-explicit/null"), typeof(VerbPayload));
    }

    // ------------------------------------------------------------------
    // ResponseDto itself
    // ------------------------------------------------------------------

    // ResponseDto is part of the public API and is a record, so callers can build sets of them, compare
    // them and copy them with `with`. Turning it into a class would make these two instances unequal.
    [Theory]
    [InlineData(StatusCodes.Status200OK, typeof(VerbPayload))]
    [InlineData(StatusCodes.Status204NoContent, null)]
    public void ResponseDto_IsEqualWhenTheStatusCodeAndTypeMatch(int statusCode, Type? type)
    {
        var left = new ResponseDto(statusCode, type);
        var right = new ResponseDto(statusCode, type);

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.True(left == right);
    }

    // The other half of value equality: both members have to take part in it, or a set of responses would
    // collapse two different ones into one.
    [Theory]
    [InlineData(StatusCodes.Status200OK, typeof(VerbPayload), StatusCodes.Status201Created, typeof(VerbPayload))]
    [InlineData(StatusCodes.Status200OK, typeof(VerbPayload), StatusCodes.Status200OK, typeof(VerbOtherPayload))]
    [InlineData(StatusCodes.Status200OK, typeof(VerbPayload), StatusCodes.Status200OK, null)]
    public void ResponseDto_IsNotEqualWhenTheStatusCodeOrTypeDiffers(int leftStatusCode, Type? leftType, int rightStatusCode, Type? rightType)
    {
        var left = new ResponseDto(leftStatusCode, leftType);
        var right = new ResponseDto(rightStatusCode, rightType);

        Assert.NotEqual(left, right);
        Assert.False(left == right);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static WebApplication CreateApp(
        Action<MediatorOptions>? configureOptions = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = TestApps.CreateBuilder();

        builder.Services.AddAuthorization();

        if (configureOptions is null)
            builder.Services.AddMediator();
        else
            builder.Services.AddMediator(configureOptions);

        configureServices?.Invoke(builder.Services);

        return builder.Build();
    }

    /// <summary>
    /// An application that never called <c>AddMediator</c>, so nothing ever configured
    /// <see cref="MediatorOptions"/>. The host registers the open generic <c>IOptions&lt;&gt;</c> itself, so
    /// the helper still resolves a default-constructed instance; the branch that runs when the service is
    /// genuinely absent is not reachable through a real host.
    /// </summary>
    private static WebApplication CreateAppWithoutMediator()
    {
        var builder = TestApps.CreateBuilder();

        builder.Services.AddAuthorization();

        return builder.Build();
    }

    private static RouteHandlerBuilder MapVerb(
        IEndpointRouteBuilder builder,
        string httpMethod,
        string pattern,
        Delegate handler,
        params ResponseDto[]? responseDtos)
    {
        return httpMethod switch
        {
            "GET" => builder.Get(pattern, handler, responseDtos),
            "POST" => builder.Post(pattern, handler, responseDtos),
            "PUT" => builder.Put(pattern, handler, responseDtos),
            "DELETE" => builder.Delete(pattern, handler, responseDtos),
            "PATCH" => builder.Patch(pattern, handler, responseDtos),
            _ => throw new ArgumentOutOfRangeException(nameof(httpMethod), httpMethod, "Unsupported verb.")
        };
    }

    private static IProducesResponseTypeMetadata[] ResponsesOf(RouteEndpoint endpoint)
        => endpoint.Metadata.OfType<IProducesResponseTypeMetadata>().ToArray();

    private static int[] StatusCodesOf(RouteEndpoint endpoint)
        => ResponsesOf(endpoint).Select(static metadata => metadata.StatusCode).ToArray();

    private static int[] SuccessStatusCodesOf(RouteEndpoint endpoint)
        => StatusCodesOf(endpoint).Where(static statusCode => statusCode is >= 200 and < 300).ToArray();

    private static IReadOnlyList<string> HttpMethodsOf(RouteEndpoint endpoint)
        => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods;

    /// <summary>Asserts the endpoint declares one 200, carrying <paramref name="responseType"/> as its schema.</summary>
    private static void AssertInfersOk(RouteEndpoint endpoint, Type responseType)
    {
        var ok = Assert.Single(ResponsesOf(endpoint), static metadata => metadata.StatusCode == StatusCodes.Status200OK);

        Assert.Equal(responseType, ok.Type);
    }

    private static void AssertDeclaresErrorBody(RouteEndpoint endpoint, int statusCode)
    {
        Assert.Contains(ResponsesOf(endpoint), metadata => metadata.StatusCode == statusCode
            && metadata.Type == typeof(ErrorsResponse)
            && metadata.ContentTypes.Contains("application/json"));
    }
}

/// <summary>A response body, used only as the TResponse of the requests below.</summary>
public sealed record VerbPayload(string Value);

/// <summary>A second body type, so two 200 entries can be told apart.</summary>
public sealed record VerbOtherPayload(string Value);

/// <summary>Contracts nested so the interface name does not have to carry the file's type prefix.</summary>
public static class VerbContracts
{
    public interface IQuery : IRequest<VerbPayload>
    {
    }
}

// These requests deliberately have no handlers: the verb helpers only read the delegate's parameter types,
// and adding handlers would register types into an assembly every other test file scans.
public sealed class VerbClassRequest : IRequest<VerbPayload>
{
    public string? Value { get; set; }
}

public sealed record VerbRecordRequest : IRequest<VerbPayload>
{
    public string? Value { get; init; }
}

public sealed record VerbPositionalRequest(string? Value) : IRequest<VerbPayload>;

public sealed record VerbAnnotatedRequest([FromRoute] int Id, string? Name) : IRequest<VerbPayload>;

public sealed class VerbInheritedInterfaceRequest : VerbContracts.IQuery
{
    public string? Value { get; set; }
}

/// <summary>Its own type argument is deliberately not its TResponse.</summary>
public sealed class VerbGenericRequest<TMarker> : IRequest<VerbPayload>
{
    public TMarker? Marker { get; set; }
}

/// <summary>Reaches IRequest&lt;MultiResponse&lt;VerbPayload&gt;&gt; through the package's own paged contract.</summary>
public sealed class VerbPagedRequest : IPagedRequest<VerbPayload>
{
    public int PageNum { get; set; }

    public int PageSize { get; set; }

    public string? Query { get; set; }
}

public sealed class VerbDualRequest : IRequest, IRequest<VerbPayload>
{
    public string? Value { get; set; }
}

public sealed class VerbVoidRequest : IRequest
{
    public string? Note { get; set; }
}

public sealed record VerbVoidRecordRequest : IRequest
{
    public string? Note { get; init; }
}
