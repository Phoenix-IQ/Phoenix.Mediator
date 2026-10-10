using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Tests.Infrastructure;
using Phoenix.Mediator.Web;
using Phoenix.Mediator.Wrappers;
using Xunit;

namespace Phoenix.Mediator.Tests;

/// <summary>
/// Requests ASP.NET Core can't bind: no body, or a JSON <c>null</c>, for a body parameter; a route or query value that
/// doesn't parse; malformed JSON; a body that isn't JSON. They are answered with the framework's 4xx and the documented
/// error body, and the handler never runs.
/// <para>
/// Every app here runs in Production, where 3.0.0 to 3.0.2 got this wrong. ASP.NET Core throws for these requests only in
/// Development (<c>RouteHandlerOptions.ThrowOnBadRequest</c>). Elsewhere it sets the status, still runs the endpoint's
/// filters and skips only the delegate, and the helpers' result filter answered the skipped delegate with the empty
/// status: a 200 or 204 with no body, for a request nobody handled. <c>AddMediator</c> now turns the option on everywhere,
/// and the filter no longer replaces an error status with the empty one when an app turns it back off.
/// </para>
/// </summary>
public sealed class BadRequestTests
{
    private static readonly Guid PaymentId = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");

    // ---------------------------------------------------------------------------------------------
    // Production, as AddMediator configures it: the framework's status and the error body
    // ---------------------------------------------------------------------------------------------

    // Each request answers with its 4xx and {"errors":[...],"traceId":"..."}, whatever the empty status. The first four
    // used to get that empty status, a 200 or 204 with no body; for the first two, sent to the void command, that is
    // exactly what the command answers when it succeeds. The last two never reach the helpers' filter and used to get a
    // bare 400 or 415. The message is ASP.NET Core's own, naming what it could not bind, rather than the generic
    // unknown-error text.
    [Theory]
    [InlineData("empty body", EmptyResponseStatusCode.Ok, StatusCodes.Status400BadRequest, "\"command\"")]
    [InlineData("empty body", EmptyResponseStatusCode.NoContent, StatusCodes.Status400BadRequest, "\"command\"")]
    [InlineData("JSON null", EmptyResponseStatusCode.Ok, StatusCodes.Status400BadRequest, "\"command\"")]
    [InlineData("JSON null", EmptyResponseStatusCode.NoContent, StatusCodes.Status400BadRequest, "\"command\"")]
    [InlineData("non-GUID route value", EmptyResponseStatusCode.Ok, StatusCodes.Status400BadRequest, "\"Guid id\" from \"not-a-guid\"")]
    [InlineData("non-GUID route value", EmptyResponseStatusCode.NoContent, StatusCodes.Status400BadRequest, "\"Guid id\" from \"not-a-guid\"")]
    [InlineData("unparseable query int", EmptyResponseStatusCode.Ok, StatusCodes.Status400BadRequest, "\"int PageNum\" from \"abc\"")]
    [InlineData("unparseable query int", EmptyResponseStatusCode.NoContent, StatusCodes.Status400BadRequest, "\"int PageNum\" from \"abc\"")]
    [InlineData("malformed JSON", EmptyResponseStatusCode.Ok, StatusCodes.Status400BadRequest, "\"BrCreatePaymentCommand command\"")]
    [InlineData("malformed JSON", EmptyResponseStatusCode.NoContent, StatusCodes.Status400BadRequest, "\"BrCreatePaymentCommand command\"")]
    [InlineData("missing Content-Type", EmptyResponseStatusCode.Ok, StatusCodes.Status415UnsupportedMediaType, "JSON media type")]
    [InlineData("missing Content-Type", EmptyResponseStatusCode.NoContent, StatusCodes.Status415UnsupportedMediaType, "JSON media type")]
    public async Task BadRequest_AnswersWithItsStatusAndTheErrorBody(string request, EmptyResponseStatusCode configured, int expectedStatusCode, string expectedInMessage)
    {
        var recorder = new BrRecorder();
        await using var app = await StartAppAsync(recorder, configured);

        var answer = await SendAsync(app, BadRequest(request));

        AssertErrorBody(answer, expectedStatusCode);
        Assert.Contains(expectedInMessage, Assert.Single(answer.Json.GetProperty("errors").EnumerateArray()).GetString(), StringComparison.Ordinal);
        Assert.Empty(recorder.Handled);
    }

    // The other half of every assertion above: the same endpoints handle well-formed requests, so a 4xx there means the
    // request was bad, not the endpoint. The void command that succeeds still gets the configured empty status.
    [Theory]
    [InlineData(EmptyResponseStatusCode.Ok, StatusCodes.Status200OK)]
    [InlineData(EmptyResponseStatusCode.NoContent, StatusCodes.Status204NoContent)]
    public async Task WellFormedRequests_AreHandled_AndTheVoidCommandGetsTheConfiguredEmptyStatus(EmptyResponseStatusCode configured, int expectedEmptyStatusCode)
    {
        var recorder = new BrRecorder();
        await using var app = await StartAppAsync(recorder, configured);

        var created = await SendAsync(app, Post("br/payments", JsonContent("""{"amount":5}""")));
        var found = await SendAsync(app, new HttpRequestMessage(HttpMethod.Get, $"br/payments/{PaymentId}"));
        var listed = await SendAsync(app, new HttpRequestMessage(HttpMethod.Get, "br/payments?pageNum=2&pageSize=1"));

        Assert.Equal((expectedEmptyStatusCode, string.Empty, (string?)null), (created.StatusCode, created.Body, created.ContentType));
        Assert.Equal(StatusCodes.Status200OK, found.StatusCode);
        Assert.Equal(PaymentId, found.Json.GetProperty("id").GetGuid());
        Assert.Equal(StatusCodes.Status200OK, listed.StatusCode);
        Assert.Equal(2, listed.Json.GetProperty("data")[0].GetProperty("page").GetInt32());
        Assert.Equal([nameof(BrCreatePaymentCommand), nameof(BrPaymentQuery), nameof(BrPaymentsQuery)], recorder.Handled);
    }

    // The setup the bug was found in (Phoenix.PGW.Backend): AddMediator with EmptyResponseStatusCode.Ok, an endpoint group
    // mapped by MapEndpoints, which also registers the exception middleware. Both requests answered 200, with no body.
    [Fact]
    public async Task MapEndpoints_AnswersBadRequestsWithTheErrorBody()
    {
        var recorder = new BrRecorder();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // Matches no assembly, so discovery only sees the group passed in.
            ApplicationName = "Phoenix.Mediator.Tests.BadRequest",
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddMediator(static options => options.EmptyResponseStatusCode = EmptyResponseStatusCode.Ok);
        AddHandlers(builder.Services, recorder);

        await using var app = builder.Build();
        app.MapEndpoints(new FakeAssembly("Phoenix.Mediator.Tests.BadRequest.Groups", typeof(BrHost<object>.PaymentEndpoints)));
        await app.StartAsync();

        var created = await SendAsync(app, Post("payment/create", EmptyContent()));
        var status = await SendAsync(app, new HttpRequestMessage(HttpMethod.Get, "payment/statuses/not-a-guid"));

        AssertErrorBody(created, StatusCodes.Status400BadRequest);
        AssertErrorBody(status, StatusCodes.Status400BadRequest);
        Assert.Empty(recorder.Handled);
    }

    // ---------------------------------------------------------------------------------------------
    // ThrowOnBadRequest turned back off by the app: ASP.NET Core's bare status, never a success
    // ---------------------------------------------------------------------------------------------

    // Configured after AddMediator, the app's own setting wins, and the answer is ASP.NET Core's production one: the status
    // and no body. The first four are the requests the helpers' filter turned into a 200 or 204. ASP.NET Core still runs
    // filters for them and hands them EmptyHttpResult in place of the skipped delegate's result, and an empty result must
    // not replace the 400 it already set.
    [Theory]
    [InlineData("empty body", EmptyResponseStatusCode.Ok, StatusCodes.Status400BadRequest)]
    [InlineData("empty body", EmptyResponseStatusCode.NoContent, StatusCodes.Status400BadRequest)]
    [InlineData("JSON null", EmptyResponseStatusCode.Ok, StatusCodes.Status400BadRequest)]
    [InlineData("JSON null", EmptyResponseStatusCode.NoContent, StatusCodes.Status400BadRequest)]
    [InlineData("non-GUID route value", EmptyResponseStatusCode.Ok, StatusCodes.Status400BadRequest)]
    [InlineData("non-GUID route value", EmptyResponseStatusCode.NoContent, StatusCodes.Status400BadRequest)]
    [InlineData("unparseable query int", EmptyResponseStatusCode.Ok, StatusCodes.Status400BadRequest)]
    [InlineData("unparseable query int", EmptyResponseStatusCode.NoContent, StatusCodes.Status400BadRequest)]
    [InlineData("malformed JSON", EmptyResponseStatusCode.Ok, StatusCodes.Status400BadRequest)]
    [InlineData("malformed JSON", EmptyResponseStatusCode.NoContent, StatusCodes.Status400BadRequest)]
    [InlineData("missing Content-Type", EmptyResponseStatusCode.Ok, StatusCodes.Status415UnsupportedMediaType)]
    [InlineData("missing Content-Type", EmptyResponseStatusCode.NoContent, StatusCodes.Status415UnsupportedMediaType)]
    public async Task BadRequest_WithThrowOnBadRequestOff_AnswersWithTheBareStatusNeverASuccess(string request, EmptyResponseStatusCode configured, int expectedStatusCode)
    {
        var recorder = new BrRecorder();
        await using var app = await StartAppAsync(recorder, configured, ThrowOnBadRequestOff);

        var answer = await SendAsync(app, BadRequest(request));

        Assert.Equal((expectedStatusCode, string.Empty), (answer.StatusCode, answer.Body));
        Assert.Empty(recorder.Handled);
    }

    // Only an empty result leaves the status alone. A filter of the app's own that answers the binding failure itself runs
    // inside the helpers' filter, and its answer is mapped like any endpoint result: an ErrorResponse gets its own status
    // and the error body.
    [Fact]
    public async Task BindingFailure_AnsweredByTheAppsOwnFilter_IsMappedLikeAnyResult()
    {
        var recorder = new BrRecorder();
        await using var app = await StartAppAsync(recorder, EmptyResponseStatusCode.Ok, ThrowOnBadRequestOff, static app =>
            app.Post("br/filtered", static (ISender sender, BrCreatePaymentCommand command, CancellationToken ct) => sender.Send(command, ct))
                .AddEndpointFilter(static async (context, next) =>
                    context.HttpContext.Response.StatusCode == StatusCodes.Status400BadRequest
                        ? new ErrorResponse(HttpStatusCode.UnprocessableEntity, ["The payment details are missing."])
                        : await next(context)));

        var answer = await SendAsync(app, Post("br/filtered", EmptyContent()));

        AssertErrorBody(answer, StatusCodes.Status422UnprocessableEntity);
        Assert.Equal("The payment details are missing.", answer.Json.GetProperty("errors")[0].GetString());
        Assert.Empty(recorder.Handled);
    }

    // The same rule outside binding: an endpoint that sets an error status itself and has nothing to return keeps that
    // status, whether it returns nothing at all or a null result. Answering it with the empty status would report the
    // failure as a success, just as for a request that failed to bind.
    [Theory]
    [InlineData(EmptyResponseStatusCode.Ok)]
    [InlineData(EmptyResponseStatusCode.NoContent)]
    public async Task Endpoint_SettingAnErrorStatusAndReturningNothing_KeepsItsStatus(EmptyResponseStatusCode configured)
    {
        var recorder = new BrRecorder();
        await using var app = await StartAppAsync(recorder, configured, configure: null, static app =>
        {
            app.Post("br/void-conflict", static async (HttpContext http, ISender sender, CancellationToken ct) =>
            {
                await sender.Send(new BrCreatePaymentCommand(5), ct);
                http.Response.StatusCode = StatusCodes.Status409Conflict;
            });
            app.Get("br/null-conflict", static async (HttpContext http, ISender sender, CancellationToken ct) =>
            {
                var nothing = await sender.Send((object)new BrCreatePaymentCommand(5), ct);
                http.Response.StatusCode = StatusCodes.Status409Conflict;
                return nothing;
            });
        });

        var voidAnswer = await SendAsync(app, new HttpRequestMessage(HttpMethod.Post, "br/void-conflict"));
        var nullAnswer = await SendAsync(app, new HttpRequestMessage(HttpMethod.Get, "br/null-conflict"));

        Assert.Equal((StatusCodes.Status409Conflict, string.Empty), (voidAnswer.StatusCode, voidAnswer.Body));
        Assert.Equal((StatusCodes.Status409Conflict, string.Empty), (nullAnswer.StatusCode, nullAnswer.Body));
        Assert.Equal([nameof(BrCreatePaymentCommand), nameof(BrCreatePaymentCommand)], recorder.Handled);
    }

    // ---------------------------------------------------------------------------------------------
    // The option itself
    // ---------------------------------------------------------------------------------------------

    // Off in Production until AddMediator turns it on. That is a default the app overrides the usual way, configuring
    // RouteHandlerOptions after AddMediator, because the later configuration wins. Configured before, the app's setting
    // is the one overridden, which is why the README says "after".
    [Fact]
    public void ThrowOnBadRequest_IsTurnedOnByAddMediator_AndTheAppsLaterConfigurationWins()
    {
        Assert.False(ThrowOnBadRequestIn(static _ => { }));
        Assert.True(ThrowOnBadRequestIn(static services => services.AddMediator()));
        Assert.False(ThrowOnBadRequestIn(static services =>
        {
            services.AddMediator();
            ThrowOnBadRequestOff(services);
        }));
        Assert.True(ThrowOnBadRequestIn(static services =>
        {
            ThrowOnBadRequestOff(services);
            services.AddMediator();
        }));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static void ThrowOnBadRequestOff(IServiceCollection services)
        => services.Configure<RouteHandlerOptions>(static options => options.ThrowOnBadRequest = false);

    /// <summary>The option as a Production app with these registrations resolves it.</summary>
    private static bool ThrowOnBadRequestIn(Action<IServiceCollection> configure)
    {
        var builder = TestApps.CreateBuilder();
        configure(builder.Services);

        using var app = builder.Build();
        return app.Services.GetRequiredService<IOptions<RouteHandlerOptions>>().Value.ThrowOnBadRequest;
    }

    /// <summary>
    /// A Production app with the mediator, the exception middleware and this file's endpoints: a void command bound from
    /// the body, a query taking a <see cref="Guid"/> from the route, and a paged query bound with <c>[AsParameters]</c>.
    /// </summary>
    private static async Task<WebApplication> StartAppAsync(
        BrRecorder recorder,
        EmptyResponseStatusCode configured,
        Action<IServiceCollection>? configure = null,
        Action<WebApplication>? map = null)
    {
        var builder = TestApps.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMediator(options => options.EmptyResponseStatusCode = configured);
        AddHandlers(builder.Services, recorder);
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        app.UsePhoenixExceptionHandling();

        var payments = app.MapGroup("br/payments");
        payments.Post("", static (ISender sender, BrCreatePaymentCommand command, CancellationToken ct) => sender.Send(command, ct));
        payments.Get("{id}", static (ISender sender, Guid id, CancellationToken ct) => sender.Send(new BrPaymentQuery(id), ct));
        payments.Get("", static (ISender sender, [AsParameters] BrPaymentsQuery query, CancellationToken ct) => sender.Send(query, ct));
        map?.Invoke(app);

        await app.StartAsync();
        return app;
    }

    /// <summary>
    /// Registers this file's handlers by hand: they live in <see cref="BrHost{TMarker}"/>, out of reach of the assembly
    /// scans other test files run, because they take the recorder.
    /// </summary>
    private static void AddHandlers(IServiceCollection services, BrRecorder recorder)
    {
        services.AddSingleton(recorder);
        services.AddTransient<IRequestHandler<BrCreatePaymentCommand>, BrHost<object>.CreatePaymentHandler>();
        services.AddTransient<IRequestHandler<BrPaymentQuery, BrPayment>, BrHost<object>.PaymentHandler>();
        services.AddTransient<IRequestHandler<BrPaymentsQuery, MultiResponse<BrPayment>>, BrHost<object>.PaymentsHandler>();
    }

    private static HttpRequestMessage BadRequest(string request) => request switch
    {
        // What HttpClient sends for a POST without a body: Content-Length: 0, which ASP.NET Core reads as no body at all.
        "empty body" => Post("br/payments", EmptyContent()),
        "JSON null" => Post("br/payments", JsonContent("null")),
        "non-GUID route value" => new HttpRequestMessage(HttpMethod.Get, "br/payments/not-a-guid"),
        // pageSize is valid, so pageNum is the only thing wrong: an [AsParameters] int with no default is required.
        "unparseable query int" => new HttpRequestMessage(HttpMethod.Get, "br/payments?pageNum=abc&pageSize=10"),
        "malformed JSON" => Post("br/payments", JsonContent("""{"amount":""")),
        // ByteArrayContent carries no Content-Type header at all.
        "missing Content-Type" => Post("br/payments", new ByteArrayContent(Encoding.UTF8.GetBytes("""{"amount":5}"""))),
        _ => throw new ArgumentOutOfRangeException(nameof(request), request, "Unknown bad request.")
    };

    private static HttpRequestMessage Post(string path, HttpContent content) => new(HttpMethod.Post, path) { Content = content };

    private static ByteArrayContent EmptyContent() => new([]);

    private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<HttpAnswer> SendAsync(WebApplication app, HttpRequestMessage request)
    {
        using var client = app.GetTestClient();
        using (request)
        using (var response = await client.SendAsync(request))
        {
            return new HttpAnswer((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType, await response.Content.ReadAsStringAsync());
        }
    }

    /// <summary>The documented error body: the status, JSON, exactly <c>errors</c> and <c>traceId</c>, and a message.</summary>
    private static void AssertErrorBody(HttpAnswer answer, int expectedStatusCode)
    {
        Assert.True(
            answer.StatusCode == expectedStatusCode && answer.ContentType == "application/json",
            $"Expected {expectedStatusCode} with a JSON error body but got {answer.StatusCode} [{answer.ContentType}]: '{answer.Body}'");

        var json = answer.Json;
        Assert.Equal(["errors", "traceId"], json.EnumerateObject().Select(static member => member.Name).Order(StringComparer.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(json.GetProperty("errors").EnumerateArray()).GetString()));
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("traceId").GetString()));
    }

    private sealed record HttpAnswer(int StatusCode, string? ContentType, string Body)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement.Clone();
    }
}

// -------------------------------------------------------------------------------------------------
// Types used only by this file, prefixed "Br". The handlers and the endpoint group live in the open
// generic BrHost<TMarker>, so the assembly scans and endpoint discovery other test files run skip them.
// -------------------------------------------------------------------------------------------------

/// <summary>Which requests reached a handler, in order.</summary>
public sealed class BrRecorder
{
    private readonly List<string> handled = [];

    public IReadOnlyList<string> Handled
    {
        get
        {
            lock (handled)
                return handled.ToArray();
        }
    }

    public void Record(object request)
    {
        lock (handled)
            handled.Add(request.GetType().Name);
    }
}

public sealed record BrPayment(Guid Id, int Page);

/// <summary>A void command bound from the JSON body, like the PGW's <c>POST /payment/create</c>.</summary>
public sealed record BrCreatePaymentCommand(decimal Amount) : IRequest;

public sealed record BrPaymentQuery(Guid Id) : IRequest<BrPayment>;

public sealed class BrPaymentsQuery : IPagedRequest<BrPayment>
{
    public int PageNum { get; set; }

    public int PageSize { get; set; }

    public string? Query { get; set; }
}

public static class BrHost<TMarker>
{
    public sealed class CreatePaymentHandler(BrRecorder recorder) : IRequestHandler<BrCreatePaymentCommand>
    {
        public Task Handle(BrCreatePaymentCommand request, CancellationToken cancellationToken)
        {
            recorder.Record(request);
            return Task.CompletedTask;
        }
    }

    public sealed class PaymentHandler(BrRecorder recorder) : IRequestHandler<BrPaymentQuery, BrPayment>
    {
        public Task<BrPayment> Handle(BrPaymentQuery request, CancellationToken cancellationToken)
        {
            recorder.Record(request);
            return Task.FromResult(new BrPayment(request.Id, 1));
        }
    }

    public sealed class PaymentsHandler(BrRecorder recorder) : IRequestHandler<BrPaymentsQuery, MultiResponse<BrPayment>>
    {
        public Task<MultiResponse<BrPayment>> Handle(BrPaymentsQuery request, CancellationToken cancellationToken)
        {
            recorder.Record(request);
            return Task.FromResult(new MultiResponse<BrPayment>([new BrPayment(Guid.Empty, request.PageNum)], 1, request.PageSize));
        }
    }

    /// <summary>The PGW's routes, mapped the way an endpoint group maps them.</summary>
    public sealed class PaymentEndpoints : BaseEndpointGroup
    {
        public override void Map(IEndpointRouteBuilder app)
        {
            var payment = app.MapGroup(GroupName);
            payment.Post("create", static (ISender sender, BrCreatePaymentCommand command, CancellationToken ct) => sender.Send(command, ct));
            payment.Get("statuses/{id}", static (ISender sender, Guid id, CancellationToken ct) => sender.Send(new BrPaymentQuery(id), ct));
        }
    }
}
