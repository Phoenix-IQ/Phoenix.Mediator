using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Wrappers;
using Xunit;

namespace Phoenix.Mediator.Tests;

/// <summary>
/// The <see cref="ISender"/> surface, method by method and request shape by request shape.
/// <para>
/// Every type in this file is prefixed <c>Send</c>: the whole test assembly is scanned by other tests'
/// <c>AddMediator(assembly)</c> calls, so names must not collide and every namespace-scope handler must be
/// constructible with no dependencies (Development-mode <c>ValidateOnBuild</c> constructs all of them).
/// Handlers that need dependencies live in <see cref="SendHost{TMarker}"/>, whose open type parameter keeps
/// the assembly scan from seeing them; those are registered by hand.
/// </para>
/// </summary>
public sealed class SenderApiTests
{
    // ---------------------------------------------------------------------------------------------
    // Task<object?> Send(object request, CancellationToken cancellationToken = default)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SendObject_ThrowsArgumentNullException_WhenRequestIsNull()
    {
        using var scope = new SendScope();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => scope.Sender.Send((object)null!));

        Assert.Equal("request", exception.ParamName);
    }

    // A type implementing neither request interface has no wrapper to build. Failing loudly here is what stops
    // a mistyped DTO from being accepted and then "handled" by nothing at all.
    [Fact]
    public async Task SendObject_ThrowsArgumentException_WhenTypeImplementsNeitherRequestInterface()
    {
        using var scope = new SendScope();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => scope.Sender.Send(new SendNotARequest()));

        Assert.Contains(nameof(SendNotARequest), exception.Message);
        Assert.Contains("IRequest", exception.Message);
    }

    // The wrapper cache is a static ConcurrentDictionary keyed by request type. A cache that stored the failed
    // lookup would turn the second call into a NullReferenceException instead of the same diagnosable error.
    [Fact]
    public async Task SendObject_ThrowsTheSameArgumentException_OnEverySendOfAnUnsupportedType()
    {
        using var scope = new SendScope();
        var notARequest = new SendNotARequest();

        var first = await Assert.ThrowsAsync<ArgumentException>(() => scope.Sender.Send(notARequest));
        var second = await Assert.ThrowsAsync<ArgumentException>(() => scope.Sender.Send(notARequest));

        Assert.Equal(first.Message, second.Message);
        // Not just "it threw again": the second failure must still be the diagnosable one that names the type.
        Assert.Contains(nameof(SendNotARequest), second.Message);
        Assert.Contains("IRequest", second.Message);
    }

    // The boxed overload is what generic plumbing uses (outbox dispatchers, job runners, test harnesses), so it
    // has to cope with every declaration style a request can have, not just the sealed-class one.
    [Theory]
    [InlineData(SendShapes.Class)]
    [InlineData(SendShapes.PositionalRecord)]
    [InlineData(SendShapes.InitRecord)]
    [InlineData(SendShapes.ReadonlyRecordStruct)]
    [InlineData(SendShapes.PlainStruct)]
    [InlineData(SendShapes.Nested)]
    [InlineData(SendShapes.Annotated)]
    [InlineData(SendShapes.Unannotated)]
    public async Task SendObject_ReturnsTheHandlersResponse_ForEveryRequestShape(string shape)
    {
        var probe = new SendProbe();
        using var scope = new SendScope();

        var result = await scope.Sender.Send(SendFixtures.CreateResponseRequest(shape, probe));

        var response = Assert.IsType<SingleResponse<string>>(result);
        Assert.Equal(SendFixtures.Payload, response.Result);
        Assert.Equal(new[] { shape }, probe.Entries);
    }

    [Theory]
    [InlineData(SendShapes.Class)]
    [InlineData(SendShapes.PositionalRecord)]
    [InlineData(SendShapes.InitRecord)]
    [InlineData(SendShapes.ReadonlyRecordStruct)]
    [InlineData(SendShapes.PlainStruct)]
    [InlineData(SendShapes.Nested)]
    [InlineData(SendShapes.Annotated)]
    [InlineData(SendShapes.Unannotated)]
    public async Task SendObject_ReturnsNull_ForEveryVoidRequestShape(string shape)
    {
        var probe = new SendProbe();
        using var scope = new SendScope();

        var result = await scope.Sender.Send(SendFixtures.CreateVoidRequest(shape, probe));

        Assert.Null(result);
        Assert.Equal(new[] { shape }, probe.Entries);
    }

    [Fact]
    public async Task SendObject_ReturnsTheBoxedValue_WhenTheResponseIsAValueType()
    {
        using var scope = new SendScope();

        var result = await scope.Sender.Send(new SendValueResponseRequest());

        Assert.Equal(SendFixtures.ValueResponse, Assert.IsType<int>(result));
    }

    // A handler may legitimately answer null. Through this overload that is indistinguishable from a void
    // request - which is why the typed overload exists - but it must still complete rather than throw.
    [Fact]
    public async Task SendObject_ReturnsNull_WhenTheHandlerReturnsNull()
    {
        using var scope = new SendScope();

        var result = await scope.Sender.Send(new SendNullResponseRequest());

        Assert.Null(result);
    }

    // CreateWrapper looks for IRequest<TResponse> before IRequest. A request implementing both must still
    // produce its response; picking the void wrapper would silently hand the caller null.
    [Fact]
    public async Task SendObject_PrefersTheResponseInterface_WhenTheRequestImplementsBoth()
    {
        var probe = new SendProbe();
        using var scope = new SendScope();

        var result = await scope.Sender.Send((object)new SendDualRequest { Probe = probe });

        Assert.Equal(SendFixtures.DualResponseEntry, Assert.IsType<SingleResponse<string>>(result).Result);
        Assert.Equal(new[] { SendFixtures.DualResponseEntry }, probe.Entries);
    }

    // No cast here on purpose. ISender documents that C# cannot infer TResponse, so a call with no type
    // arguments on an IRequest<TResponse>-typed variable lands on Send(object) and dispatches by runtime type.
    // Adding an inferring overload to ISender would either break this call site or silently re-route it.
    [Fact]
    public async Task SendUntyped_BindsToTheObjectOverload_WhenTheVariableIsTypedAsIRequestOfResponse()
    {
        var probe = new SendProbe();
        using var scope = new SendScope();
        IRequest<SingleResponse<string>> query = new SendClassRequest { Value = SendFixtures.Payload, Probe = probe };

        object? result = await scope.Sender.Send(query);

        Assert.Equal(SendFixtures.Payload, Assert.IsType<SingleResponse<string>>(result).Result);
        Assert.Equal(new[] { SendShapes.Class }, probe.Entries);
    }

    // A handler can only honor cancellation if the token it receives is the caller's own.
    [Fact]
    public async Task SendObject_PassesTheCallersCancellationTokenToTheHandler()
    {
        var probe = new SendProbe();
        using var scope = new SendScope();
        using var cts = new CancellationTokenSource();

        await scope.Sender.Send(new SendClassRequest { Value = SendFixtures.Payload, Probe = probe }, cts.Token);

        Assert.Equal(cts.Token, probe.Token);
    }

    [Fact]
    public async Task SendObject_DefaultsTheCancellationTokenToNone()
    {
        var probe = new SendProbe();
        using var scope = new SendScope();

        await scope.Sender.Send(new SendClassRequest { Value = SendFixtures.Payload, Probe = probe });

        Assert.Equal(CancellationToken.None, probe.Token);
        Assert.False(probe.Token.CanBeCanceled);
    }

    // Forgetting to register a handler is the most common wiring mistake. The container's error must name the
    // handler interface and the request type, or the developer is left guessing which request is unwired.
    [Fact]
    public async Task SendObject_ThrowsInvalidOperationException_WhenNoHandlerIsRegistered()
    {
        using var scope = new SendScope();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.Sender.Send(new SendUnregisteredRequest()));

        Assert.Contains("IRequestHandler", exception.Message);
        Assert.Contains(nameof(SendUnregisteredRequest), exception.Message);
    }

    [Fact]
    public async Task SendObject_ThrowsInvalidOperationException_WhenNoVoidHandlerIsRegistered()
    {
        using var scope = new SendScope();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.Sender.Send((object)new SendUnregisteredCommand()));

        Assert.Contains("IRequestHandler", exception.Message);
        Assert.Contains(nameof(SendUnregisteredCommand), exception.Message);
    }

    // The counterpart of the two tests above: the same send succeeds once the handler is registered, which
    // proves the failure was about registration and not about the request type.
    [Fact]
    public async Task SendObject_Succeeds_WhenTheHandlerIsRegisteredByHand()
    {
        var probe = new SendProbe();
        using var scope = new SendScope(services => services
            .AddTransient<IRequestHandler<SendUnregisteredRequest, SingleResponse<string>>, SendHost<object>.UnregisteredRequestHandler>());

        var result = await scope.Sender.Send(new SendUnregisteredRequest { Probe = probe });

        Assert.Equal(SendFixtures.Payload, Assert.IsType<SingleResponse<string>>(result).Result);
        Assert.Equal(new[] { SendFixtures.UnregisteredEntry }, probe.Entries);
    }

    // Handler failures must reach the caller as themselves: the exception-handling middleware maps by exception
    // type, so an AggregateException wrapper would turn a 404 into a 500.
    [Fact]
    public async Task SendObject_PropagatesTheHandlerExceptionUnwrapped()
    {
        using var scope = new SendScope();

        var exception = await Assert.ThrowsAsync<SendHandlerFailure>(
            () => scope.Sender.Send(new SendThrowingRequest()));

        Assert.Equal(SendFixtures.FailureMessage, exception.Message);
        Assert.Contains(nameof(SendThrowingHandler), exception.StackTrace ?? string.Empty);
    }

    [Fact]
    public async Task SendObject_PropagatesTheVoidHandlerExceptionUnwrapped()
    {
        using var scope = new SendScope();

        var exception = await Assert.ThrowsAsync<SendHandlerFailure>(
            () => scope.Sender.Send((object)new SendThrowingCommand()));

        Assert.Equal(SendFixtures.FailureMessage, exception.Message);
        Assert.Contains(nameof(SendThrowingCommandHandler), exception.StackTrace ?? string.Empty);
    }

    // The boxed overload reuses the same internal pipeline as the typed one, so behaviors must still run.
    // If it bypassed them, validation and Sentry would silently stop applying to runtime-typed dispatch.
    [Fact]
    public async Task SendObject_RunsPipelineBehaviorsAroundTheHandler()
    {
        var probe = new SendProbe();
        using var scope = new SendScope(services => services
            .AddTransient<IPipelineBehavior<SendClassRequest, SingleResponse<string>>, SendResponseBehavior>());

        await scope.Sender.Send(new SendClassRequest { Value = SendFixtures.Payload, Probe = probe });

        Assert.Equal(new[] { "behavior:before", SendShapes.Class, "behavior:after" }, probe.Entries);
    }

    // The wrapper cache is static and shared by every scope in the process. Building wrappers for several
    // request types at once is exactly the race a cold-start traffic burst produces in production: a torn read
    // would hand one request type's wrapper to another, so every send must come back with its own answer.
    [Fact]
    public async Task SendObject_DispatchesManyRequestTypesConcurrently()
    {
        const int Sends = 240;

        var probe = new SendProbe();
        using var scope = new SendScope();
        var sender = scope.Sender;

        var sends = Enumerable.Range(0, Sends)
            .Select(index => Task.Run(() => sender.Send(
                SendFixtures.CreateResponseRequest(SendShapes.All[index % SendShapes.All.Count], probe))))
            .ToArray();

        var results = await Task.WhenAll(sends);

        Assert.All(results, result => Assert.Equal(SendFixtures.Payload, Assert.IsType<SingleResponse<string>>(result).Result));
        Assert.Equal(Sends, probe.Entries.Count);
        // Every shape was sent the same number of times, so every shape must have been handled that many times.
        foreach (var shape in SendShapes.All)
            Assert.Equal(Sends / SendShapes.All.Count, probe.Entries.Count(entry => entry == shape));
    }

    // The boxed overload keys everything off request.GetType(), so a subclass is a DIFFERENT request type with
    // its own handler requirement - inheriting from a handled request does not inherit its handler. Anyone who
    // subclasses a request to add a field needs this to fail loudly at the send rather than dispatch elsewhere.
    [Fact]
    public async Task SendObject_ThrowsInvalidOperationException_WhenOnlyTheBaseTypesHandlerIsRegistered()
    {
        using var scope = new SendScope();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.Sender.Send(new SendDerivedRequest()));

        Assert.Contains("IRequestHandler", exception.Message);
        Assert.Contains(nameof(SendDerivedRequest), exception.Message);
    }

    // The mirror image of the test above, and the reason the asymmetry is worth pinning: the typed overload
    // resolves the handler from TRequest - the STATIC type - so the base handler runs and is handed the
    // subclass instance. Same object, two overloads, two outcomes.
    [Fact]
    public async Task SendTyped_RunsTheBaseTypesHandler_WhenASubclassIsSentAsItsBaseType()
    {
        var probe = new SendProbe();
        using var scope = new SendScope();
        SendBaseRequest request = new SendDerivedRequest { Probe = probe };

        var response = await scope.Sender.Send<SendBaseRequest, SingleResponse<string>>(request);

        Assert.Equal(nameof(SendDerivedRequest), response.Result);
        Assert.Equal(new[] { nameof(SendDerivedRequest) }, probe.Entries);
    }

    // CreateWrapper closes RequestResponseWrapper<,> over the request type, which for a generic request means
    // MakeGenericType on an already-closed generic. Each closing is its own cache key and its own handler, so a
    // wrapper keyed by the open definition would make Request<int> and Request<string> collide.
    [Fact]
    public async Task SendObject_DispatchesEachClosedGenericRequestTypeToItsOwnHandler()
    {
        var probe = new SendProbe();
        using var scope = new SendScope();

        var fromInt = await scope.Sender.Send(new SendGenericRequest<int>(7, probe));
        var fromString = await scope.Sender.Send(new SendGenericRequest<string>("seven", probe));

        Assert.Equal("int:7", Assert.IsType<SingleResponse<string>>(fromInt).Result);
        Assert.Equal("string:seven", Assert.IsType<SingleResponse<string>>(fromString).Result);
        Assert.Equal(new[] { SendFixtures.GenericIntEntry, SendFixtures.GenericStringEntry }, probe.Entries);
    }

    // ---------------------------------------------------------------------------------------------
    // Task<TResponse> Send<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken = default)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SendTyped_ReturnsTheResponse_ForASealedClassRequest()
    {
        var probe = new SendProbe();

        await AssertTypedSendEchoes(new SendClassRequest { Value = SendFixtures.Payload, Probe = probe }, probe, SendShapes.Class);
    }

    [Fact]
    public async Task SendTyped_ReturnsTheResponse_ForAPositionalRecordRequest()
    {
        var probe = new SendProbe();

        await AssertTypedSendEchoes(new SendPositionalRecordRequest(SendFixtures.Payload, probe), probe, SendShapes.PositionalRecord);
    }

    [Fact]
    public async Task SendTyped_ReturnsTheResponse_ForAnInitOnlyRecordRequest()
    {
        var probe = new SendProbe();

        await AssertTypedSendEchoes(new SendInitRecordRequest { Value = SendFixtures.Payload, Probe = probe }, probe, SendShapes.InitRecord);
    }

    // Struct requests are boxed the moment they hit the non-generic path, and unboxed again by the wrapper.
    // The typed overload must avoid that round trip and still hand the handler the caller's values.
    [Fact]
    public async Task SendTyped_ReturnsTheResponse_ForAReadonlyRecordStructRequest()
    {
        var probe = new SendProbe();

        await AssertTypedSendEchoes(new SendReadonlyRecordStructRequest(SendFixtures.Payload, probe), probe, SendShapes.ReadonlyRecordStruct);
    }

    [Fact]
    public async Task SendTyped_ReturnsTheResponse_ForAPlainStructRequest()
    {
        var probe = new SendProbe();

        await AssertTypedSendEchoes(new SendPlainStructRequest(SendFixtures.Payload, probe), probe, SendShapes.PlainStruct);
    }

    [Fact]
    public async Task SendTyped_ReturnsTheResponse_ForANestedRequestType()
    {
        var probe = new SendProbe();

        await AssertTypedSendEchoes(new SendFixtures.NestedRequest { Value = SendFixtures.Payload, Probe = probe }, probe, SendShapes.Nested);
    }

    [Fact]
    public async Task SendTyped_ReturnsTheResponse_ForAnAnnotatedRequest()
    {
        var probe = new SendProbe();
        var request = new SendAnnotatedRequest { Id = 7, Force = true, Tenant = "acme", Value = SendFixtures.Payload, Probe = probe };

        await AssertTypedSendEchoes(request, probe, SendShapes.Annotated);
    }

    [Fact]
    public async Task SendTyped_ReturnsTheResponse_ForAnUnannotatedRequest()
    {
        var probe = new SendProbe();
        var request = new SendUnannotatedRequest { Id = 7, Force = true, Tenant = "acme", Value = SendFixtures.Payload, Probe = probe };

        await AssertTypedSendEchoes(request, probe, SendShapes.Unannotated);
    }

    // A request variable typed as the interface binds TRequest to the interface itself, and no handler is
    // registered for an interface. Dispatch must fall back to the runtime type instead of failing to resolve.
    [Fact]
    public async Task SendTyped_DispatchesByRuntimeType_WhenTheVariableIsTypedAsIRequestOfResponse()
    {
        var probe = new SendProbe();
        using var scope = new SendScope();
        IRequest<SingleResponse<string>> query = new SendReadonlyRecordStructRequest(SendFixtures.Payload, probe);

        var response = await scope.Sender.Send<IRequest<SingleResponse<string>>, SingleResponse<string>>(query);

        Assert.Equal(SendFixtures.Payload, response.Result);
        Assert.Equal(new[] { SendShapes.ReadonlyRecordStruct }, probe.Entries);
    }

    // The runtime-type fallback casts object? back to TResponse. For a value-type response that is an unboxing
    // cast, which fails loudly if the wrapper ever returned something else.
    [Fact]
    public async Task SendTyped_UnboxesTheResponse_WhenDispatchedByRuntimeTypeWithAValueTypeResponse()
    {
        using var scope = new SendScope();
        IRequest<int> query = new SendValueResponseRequest();

        var response = await scope.Sender.Send<IRequest<int>, int>(query);

        Assert.Equal(SendFixtures.ValueResponse, response);
    }

    // The same cast is null-suppressed, so a handler answering null must not become a NullReferenceException.
    [Fact]
    public async Task SendTyped_ReturnsNull_WhenDispatchedByRuntimeTypeAndTheHandlerReturnsNull()
    {
        using var scope = new SendScope();
        IRequest<string?> query = new SendNullResponseRequest();

        var response = await scope.Sender.Send<IRequest<string?>, string?>(query);

        Assert.Null(response);
    }

    [Fact]
    public async Task SendTyped_ReturnsNull_WhenTheHandlerReturnsNull()
    {
        using var scope = new SendScope();

        var response = await scope.Sender.Send<SendNullResponseRequest, string?>(new SendNullResponseRequest());

        Assert.Null(response);
    }

    [Fact]
    public async Task SendTyped_ReturnsTheValue_WhenTheResponseIsAValueType()
    {
        using var scope = new SendScope();

        var response = await scope.Sender.Send<SendValueResponseRequest, int>(new SendValueResponseRequest());

        Assert.Equal(SendFixtures.ValueResponse, response);
    }

    [Fact]
    public async Task SendTyped_ReturnsTheResponse_WhenTheRequestImplementsBothRequestInterfaces()
    {
        var probe = new SendProbe();
        using var scope = new SendScope();

        var response = await scope.Sender.Send<SendDualRequest, SingleResponse<string>>(new SendDualRequest { Probe = probe });

        Assert.Equal(SendFixtures.DualResponseEntry, response.Result);
        Assert.Equal(new[] { SendFixtures.DualResponseEntry }, probe.Entries);
    }

    [Fact]
    public async Task SendTyped_PassesTheCallersCancellationTokenToTheHandler()
    {
        var probe = new SendProbe();
        using var scope = new SendScope();
        using var cts = new CancellationTokenSource();

        await scope.Sender.Send<SendClassRequest, SingleResponse<string>>(
            new SendClassRequest { Value = SendFixtures.Payload, Probe = probe }, cts.Token);

        Assert.Equal(cts.Token, probe.Token);
    }

    [Fact]
    public async Task SendTyped_DefaultsTheCancellationTokenToNone()
    {
        var probe = new SendProbe();
        using var scope = new SendScope();

        await scope.Sender.Send<SendClassRequest, SingleResponse<string>>(
            new SendClassRequest { Value = SendFixtures.Payload, Probe = probe });

        Assert.Equal(CancellationToken.None, probe.Token);
        Assert.False(probe.Token.CanBeCanceled);
    }

    // The mediator does not pre-check the token; it hands it to the handler. A cancellation-aware handler must
    // therefore see an already-cancelled token and be able to abandon the work.
    [Fact]
    public async Task SendTyped_SurfacesOperationCanceledException_WhenTheTokenIsAlreadyCancelled()
    {
        var probe = new SendProbe();
        using var scope = new SendScope();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scope.Sender.Send<SendCancellationAwareRequest, SingleResponse<string>>(
                new SendCancellationAwareRequest { Probe = probe }, cts.Token));

        Assert.True(probe.Token.IsCancellationRequested);
    }

    // The flip side: cancellation is cooperative. A handler that ignores the token still runs to completion,
    // so callers cannot assume a cancelled token alone prevents side effects.
    [Fact]
    public async Task SendTyped_StillInvokesTheHandler_WhenTheTokenIsCancelledAndTheHandlerIgnoresIt()
    {
        var probe = new SendProbe();
        using var scope = new SendScope();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var response = await scope.Sender.Send<SendClassRequest, SingleResponse<string>>(
            new SendClassRequest { Value = SendFixtures.Payload, Probe = probe }, cts.Token);

        Assert.Equal(SendFixtures.Payload, response.Result);
        Assert.Equal(new[] { SendShapes.Class }, probe.Entries);
    }

    [Fact]
    public async Task SendTyped_PropagatesTheHandlerExceptionUnwrapped()
    {
        using var scope = new SendScope();

        var exception = await Assert.ThrowsAsync<SendHandlerFailure>(
            () => scope.Sender.Send<SendThrowingRequest, SingleResponse<string>>(new SendThrowingRequest()));

        Assert.Equal(SendFixtures.FailureMessage, exception.Message);
        Assert.Contains(nameof(SendThrowingHandler), exception.StackTrace ?? string.Empty);
    }

    [Fact]
    public async Task SendTyped_ThrowsInvalidOperationException_WhenNoHandlerIsRegistered()
    {
        using var scope = new SendScope();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.Sender.Send<SendUnregisteredRequest, SingleResponse<string>>(new SendUnregisteredRequest()));

        Assert.Contains("IRequestHandler", exception.Message);
        Assert.Contains(nameof(SendUnregisteredRequest), exception.Message);
    }

    // A handler whose dependency is missing must fail with the container's own message naming that dependency;
    // that message is the only clue the developer gets about which registration is missing.
    [Fact]
    public async Task SendTyped_SurfacesTheContainerError_WhenAHandlerDependencyIsNotRegistered()
    {
        using var scope = new SendScope(services => services
            .AddTransient<IRequestHandler<SendMissingDependencyRequest, SingleResponse<string>>, SendHost<object>.MissingDependencyHandler>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.Sender.Send<SendMissingDependencyRequest, SingleResponse<string>>(new SendMissingDependencyRequest()));

        Assert.Contains(nameof(SendMissingDependency), exception.Message);
    }

    // Handlers are registered transient, so each send activates a fresh instance. A handler accidentally
    // registered as a singleton would keep per-request state (and scoped dependencies) alive across requests.
    [Fact]
    public async Task SendTyped_ActivatesAFreshHandlerForEverySend()
    {
        var counter = new SendActivationCounter();
        using var scope = new SendScope(services => services
            .AddSingleton(counter)
            .AddTransient<IRequestHandler<SendCountedRequest, SingleResponse<string>>, SendHost<object>.CountingHandler>());

        await scope.Sender.Send<SendCountedRequest, SingleResponse<string>>(new SendCountedRequest());
        await scope.Sender.Send<SendCountedRequest, SingleResponse<string>>(new SendCountedRequest());

        Assert.Equal(2, counter.Count);
    }

    // The mediator is scoped precisely so handlers can take scoped dependencies (DbContext, current user).
    // Two sends in one scope must share that dependency, and a second scope must get its own.
    [Fact]
    public async Task SendTyped_ResolvesTheHandlerFromTheScopeThatSends()
    {
        var services = new ServiceCollection();
        services.AddMediator(typeof(SenderApiTests).Assembly);
        services.AddScoped<SendScopedMarker>();
        services.AddTransient<IRequestHandler<SendScopedRequest, SingleResponse<string>>, SendHost<object>.ScopedDependencyHandler>();

        using var provider = services.BuildServiceProvider(validateScopes: true);

        string first;
        string repeated;
        using (var firstScope = provider.CreateScope())
        {
            var sender = firstScope.ServiceProvider.GetRequiredService<ISender>();
            first = (await sender.Send<SendScopedRequest, SingleResponse<string>>(new SendScopedRequest())).Result!;
            repeated = (await sender.Send<SendScopedRequest, SingleResponse<string>>(new SendScopedRequest())).Result!;
        }

        string second;
        using (var secondScope = provider.CreateScope())
        {
            var sender = secondScope.ServiceProvider.GetRequiredService<ISender>();
            second = (await sender.Send<SendScopedRequest, SingleResponse<string>>(new SendScopedRequest())).Result!;
        }

        Assert.Equal(first, repeated);
        Assert.NotEqual(first, second);
    }

    // Composite handlers (one use case orchestrating others) send from inside a handler. That must reuse the
    // same scope rather than deadlocking or resolving a second mediator with different scoped state.
    [Fact]
    public async Task SendTyped_AllowsAHandlerToResolveTheSenderAndSendAnotherRequest()
    {
        var probe = new SendProbe();
        using var scope = new SendScope(services => services
            .AddTransient<IRequestHandler<SendOuterRequest, SingleResponse<string>>, SendHost<object>.ReentrantHandler>());

        var response = await scope.Sender.Send<SendOuterRequest, SingleResponse<string>>(new SendOuterRequest { Probe = probe });

        Assert.Equal("outer(inner)", response.Result);
        Assert.Equal(new[] { SendFixtures.InnerEntry, SendFixtures.OuterEntry }, probe.Entries);
    }

    // The typed overloads do not null-check the request: null reaches the handler, where it becomes whatever
    // the handler does with it. Documented here so a future guard is a deliberate change, not a surprise.
    [Fact]
    public async Task SendTyped_PassesNullStraightToTheHandler_WhenTheRequestIsNull()
    {
        var probe = new SendProbe();
        using var scope = new SendScope(services => services
            .AddSingleton(probe)
            .AddTransient<IRequestHandler<SendNullTolerantRequest, SingleResponse<string>>, SendHost<object>.NullTolerantHandler>());

        var response = await scope.Sender.Send<SendNullTolerantRequest, SingleResponse<string>>(null!);

        Assert.Equal(SendFixtures.NullRequestEntry, response.Result);
        Assert.Equal(new[] { SendFixtures.NullRequestEntry }, probe.Entries);
    }

    // ---------------------------------------------------------------------------------------------
    // Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SendVoid_ReachesTheHandler_ForASealedClassCommand()
    {
        var probe = new SendProbe();

        await AssertVoidSendReaches(new SendClassCommand { Probe = probe }, probe, SendShapes.Class);
    }

    [Fact]
    public async Task SendVoid_ReachesTheHandler_ForAPositionalRecordCommand()
    {
        var probe = new SendProbe();

        await AssertVoidSendReaches(new SendPositionalRecordCommand(probe), probe, SendShapes.PositionalRecord);
    }

    [Fact]
    public async Task SendVoid_ReachesTheHandler_ForAnInitOnlyRecordCommand()
    {
        var probe = new SendProbe();

        await AssertVoidSendReaches(new SendInitRecordCommand { Probe = probe }, probe, SendShapes.InitRecord);
    }

    [Fact]
    public async Task SendVoid_ReachesTheHandler_ForAReadonlyRecordStructCommand()
    {
        var probe = new SendProbe();

        await AssertVoidSendReaches(new SendReadonlyRecordStructCommand(probe), probe, SendShapes.ReadonlyRecordStruct);
    }

    [Fact]
    public async Task SendVoid_ReachesTheHandler_ForAPlainStructCommand()
    {
        var probe = new SendProbe();

        await AssertVoidSendReaches(new SendPlainStructCommand(probe), probe, SendShapes.PlainStruct);
    }

    [Fact]
    public async Task SendVoid_ReachesTheHandler_ForANestedCommandType()
    {
        var probe = new SendProbe();

        await AssertVoidSendReaches(new SendFixtures.NestedCommand { Probe = probe }, probe, SendShapes.Nested);
    }

    [Fact]
    public async Task SendVoid_ReachesTheHandler_ForAnAnnotatedCommand()
    {
        var probe = new SendProbe();

        await AssertVoidSendReaches(new SendAnnotatedCommand { Id = 7, Force = true, Tenant = "acme", Probe = probe }, probe, SendShapes.Annotated);
    }

    [Fact]
    public async Task SendVoid_ReachesTheHandler_ForAnUnannotatedCommand()
    {
        var probe = new SendProbe();

        await AssertVoidSendReaches(new SendUnannotatedCommand { Id = 7, Force = true, Tenant = "acme", Probe = probe }, probe, SendShapes.Unannotated);
    }

    // Commands are often dispatched polymorphically (a List<IRequest>, a factory return value). Binding
    // TRequest to the interface must not stop the concrete handler from running.
    [Fact]
    public async Task SendVoid_DispatchesByRuntimeType_WhenTheVariableIsTypedAsIRequest()
    {
        var probe = new SendProbe();
        using var scope = new SendScope();
        IRequest command = new SendPositionalRecordCommand(probe);

        await scope.Sender.Send(command);

        Assert.Equal(new[] { SendShapes.PositionalRecord }, probe.Entries);
    }

    // The interface-typed path routes through Send(object), which does guard against null - unlike the
    // concrete-typed path above, which hands null to the handler.
    [Fact]
    public async Task SendVoid_ThrowsArgumentNullException_WhenAnInterfaceTypedRequestIsNull()
    {
        using var scope = new SendScope();
        IRequest? command = null;

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => scope.Sender.Send(command!));

        Assert.Equal("request", exception.ParamName);
    }

    [Fact]
    public async Task SendVoid_PassesTheCallersCancellationTokenToTheHandler()
    {
        var probe = new SendProbe();
        using var scope = new SendScope();
        using var cts = new CancellationTokenSource();

        await scope.Sender.Send(new SendClassCommand { Probe = probe }, cts.Token);

        Assert.Equal(cts.Token, probe.Token);
    }

    [Fact]
    public async Task SendVoid_DefaultsTheCancellationTokenToNone()
    {
        var probe = new SendProbe();
        using var scope = new SendScope();

        await scope.Sender.Send(new SendClassCommand { Probe = probe });

        Assert.Equal(CancellationToken.None, probe.Token);
        Assert.False(probe.Token.CanBeCanceled);
    }

    [Fact]
    public async Task SendVoid_PropagatesTheHandlerExceptionUnwrapped()
    {
        using var scope = new SendScope();

        var exception = await Assert.ThrowsAsync<SendHandlerFailure>(
            () => scope.Sender.Send(new SendThrowingCommand()));

        Assert.Equal(SendFixtures.FailureMessage, exception.Message);
        Assert.Contains(nameof(SendThrowingCommandHandler), exception.StackTrace ?? string.Empty);
    }

    [Fact]
    public async Task SendVoid_ThrowsInvalidOperationException_WhenNoHandlerIsRegistered()
    {
        using var scope = new SendScope();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.Sender.Send(new SendUnregisteredCommand()));

        Assert.Contains("IRequestHandler", exception.Message);
        Assert.Contains(nameof(SendUnregisteredCommand), exception.Message);
    }

    // Void requests have their own behavior interface and their own composition loop. First registered must run
    // outermost here too, otherwise a logging or transaction behavior nests the wrong way round for commands.
    [Fact]
    public async Task SendVoid_ExecutesPipelineBehaviorsOutermostFirst()
    {
        var probe = new SendProbe();
        using var scope = new SendScope(services => services
            .AddTransient<IPipelineBehavior<SendClassCommand>, SendFirstVoidBehavior>()
            .AddTransient<IPipelineBehavior<SendClassCommand>, SendSecondVoidBehavior>());

        await scope.Sender.Send(new SendClassCommand { Probe = probe });

        Assert.Equal(
            new[] { "first:before", "second:before", SendShapes.Class, "second:after", "first:after" },
            probe.Entries);
    }

    // Overload resolution, not the mediator, decides this one: with no type arguments and no cast, C# prefers
    // Send<TRequest>(TRequest) over Send(object), so a dual-interface request runs its VOID handler. Adding or
    // reordering an ISender overload would silently flip which handler a caller's existing code reaches.
    [Fact]
    public async Task SendUntyped_BindsToTheVoidOverload_WhenTheRequestImplementsBothInterfaces()
    {
        var probe = new SendProbe();
        using var scope = new SendScope();

        await scope.Sender.Send(new SendDualRequest { Probe = probe });

        Assert.Equal(new[] { SendFixtures.DualVoidEntry }, probe.Entries);
    }

    // ...and the same call on an IRequest-typed variable runs the RESPONSE handler instead, because the void
    // overload forwards interface-typed requests to Send(object), which prefers IRequest<TResponse>. A dual
    // request pulled out of a List<IRequest> therefore takes a different handler than the same request sent
    // through its concrete type - surprising enough that it must not change unnoticed.
    [Fact]
    public async Task SendVoid_RunsTheResponseHandler_WhenADualRequestIsTypedAsIRequest()
    {
        var probe = new SendProbe();
        using var scope = new SendScope();
        IRequest command = new SendDualRequest { Probe = probe };

        await scope.Sender.Send(command);

        Assert.Equal(new[] { SendFixtures.DualResponseEntry }, probe.Entries);
    }

    // ---------------------------------------------------------------------------------------------
    // IPagedRequest<TItem>
    // ---------------------------------------------------------------------------------------------

    // IPagedRequest<T> only derives from IRequest<MultiResponse<T>>; the wrapper has to find that interface
    // through the derived one, or every paged query in an app fails to dispatch.
    [Fact]
    public async Task SendTyped_PagedClassRequest_ReturnsTheMultiResponse()
    {
        var probe = new SendProbe();
        using var scope = new SendScope();

        var response = await scope.Sender.Send<SendPagedClassRequest, MultiResponse<string>>(
            new SendPagedClassRequest { PageNum = 2, PageSize = 10, Query = "abc", Probe = probe });

        Assert.Equal(SendFixtures.PageItems, response.Data);
        Assert.Equal(SendFixtures.PagedTotalCount, response.TotalCount);
        Assert.Equal(10, response.PageSize);
        Assert.Equal(3, response.PagesCount);
        Assert.Equal(new[] { SendFixtures.PagedClassEntry }, probe.Entries);
    }

    [Fact]
    public async Task SendTyped_PagedRecordRequest_ReturnsTheMultiResponse()
    {
        var probe = new SendProbe();
        using var scope = new SendScope();

        var response = await scope.Sender.Send<SendPagedRecordRequest, MultiResponse<string>>(
            new SendPagedRecordRequest(1, 5, null, probe));

        Assert.Equal(SendFixtures.PageItems, response.Data);
        Assert.Equal(5, response.PagesCount);
        Assert.Equal(new[] { SendFixtures.PagedRecordEntry }, probe.Entries);
    }

    [Fact]
    public async Task SendObject_PagedRequest_ReturnsTheMultiResponse()
    {
        var probe = new SendProbe();
        using var scope = new SendScope();

        var result = await scope.Sender.Send(new SendPagedRecordRequest(1, 10, null, probe));

        var response = Assert.IsType<MultiResponse<string>>(result);
        Assert.Equal(SendFixtures.PagedTotalCount, response.TotalCount);
        Assert.Equal(new[] { SendFixtures.PagedRecordEntry }, probe.Entries);
    }

    // The three members ARE the IPagedRequest<T> contract, and nothing else asserts they reach the handler: the
    // other paged tests only observe PageSize, and only because MultiResponse echoes it back. A handler handed a
    // request whose PageNum or Query did not survive dispatch returns the wrong slice with a 200 and no clue.
    [Fact]
    public async Task SendObject_PagedRequest_PassesThePagingMembersToTheHandler()
    {
        var probe = new SendProbe();
        using var scope = new SendScope();

        var result = await scope.Sender.Send(new SendPagedEchoRequest(3, 7, "term", probe));

        var response = Assert.IsType<MultiResponse<string>>(result);
        Assert.Equal(new[] { "page:3", "size:7", "query:term" }, response.Data);
        Assert.Equal(7, response.PageSize);
        Assert.Equal(new[] { SendFixtures.PagedEchoEntry }, probe.Entries);
    }

    // Repository-style code often passes the request around as IPagedRequest<T>; that must dispatch by runtime
    // type like any other interface-typed variable.
    [Fact]
    public async Task SendTyped_PagedRequest_DispatchesByRuntimeType_WhenTypedAsIPagedRequest()
    {
        var probe = new SendProbe();
        using var scope = new SendScope();
        IPagedRequest<string> paged = new SendPagedClassRequest { PageNum = 1, PageSize = 10, Probe = probe };

        var response = await scope.Sender.Send<IPagedRequest<string>, MultiResponse<string>>(paged);

        Assert.Equal(SendFixtures.PagedTotalCount, response.TotalCount);
        Assert.Equal(new[] { SendFixtures.PagedClassEntry }, probe.Entries);
    }

    // ---------------------------------------------------------------------------------------------
    // Registration
    // ---------------------------------------------------------------------------------------------

    // The mediator is registered scoped so handlers may depend on scoped services. Resolving the sender from the
    // root provider has to fail: silently succeeding would leak a request-scoped DbContext into a singleton.
    [Fact]
    public void Send_ResolvingTheSenderFromTheRootProvider_FailsBecauseTheMediatorIsScoped()
    {
        var services = new ServiceCollection();
        services.AddMediator();

        using var provider = services.BuildServiceProvider(validateScopes: true);

        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ISender>());

        // Name the service too: without it the test passes for any scoped service the container happened to reject.
        Assert.Contains(nameof(ISender), exception.Message, StringComparison.Ordinal);
        Assert.Contains("scoped", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // AddMediator() with no assemblies registers the sender but no handlers at all: sending anything then has
    // to fail on the handler lookup rather than on the sender itself.
    [Fact]
    public async Task Send_WithoutAnyScannedAssemblies_ResolvesTheSenderButFindsNoHandler()
    {
        var services = new ServiceCollection();
        services.AddMediator();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        // GetRequiredService already fails if the sender itself is unregistered, so reaching the send proves it.
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.Send<SendClassRequest, SingleResponse<string>>(new SendClassRequest()));

        Assert.Contains(nameof(SendClassRequest), exception.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static async Task AssertTypedSendEchoes<TRequest>(TRequest request, SendProbe probe, string expectedEntry)
        where TRequest : IRequest<SingleResponse<string>>
    {
        using var scope = new SendScope();

        var response = await scope.Sender.Send<TRequest, SingleResponse<string>>(request);

        Assert.Equal(SendFixtures.Payload, response.Result);
        Assert.Equal(new[] { expectedEntry }, probe.Entries);
    }

    private static async Task AssertVoidSendReaches<TRequest>(TRequest request, SendProbe probe, string expectedEntry)
        where TRequest : IRequest
    {
        using var scope = new SendScope();

        await scope.Sender.Send(request);

        Assert.Equal(new[] { expectedEntry }, probe.Entries);
    }
}

/// <summary>
/// A provider plus a scope, wired the way an app wires the mediator: scan this assembly for handlers, then
/// resolve <see cref="ISender"/> from a scope (the mediator is scoped and cannot come from the root).
/// </summary>
internal sealed class SendScope : IDisposable
{
    private readonly ServiceProvider provider;
    private readonly IServiceScope scope;

    public SendScope(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddMediator(typeof(SendScope).Assembly);
        configure?.Invoke(services);

        provider = services.BuildServiceProvider(validateScopes: true);
        scope = provider.CreateScope();
    }

    public ISender Sender => scope.ServiceProvider.GetRequiredService<ISender>();

    public void Dispose()
    {
        scope.Dispose();
        provider.Dispose();
    }
}

/// <summary>Names of the request declaration styles every overload is exercised against.</summary>
public static class SendShapes
{
    public const string Class = "class";
    public const string PositionalRecord = "positional-record";
    public const string InitRecord = "init-record";
    public const string ReadonlyRecordStruct = "readonly-record-struct";
    public const string PlainStruct = "plain-struct";
    public const string Nested = "nested";
    public const string Annotated = "annotated";
    public const string Unannotated = "unannotated";

    public static readonly IReadOnlyList<string> All =
    [
        Class, PositionalRecord, InitRecord, ReadonlyRecordStruct, PlainStruct, Nested, Annotated, Unannotated
    ];
}

/// <summary>
/// What a handler saw. Carried on the request itself so namespace-scope handlers stay dependency-free and no
/// test needs static state; struct requests reach it through the reference they hold.
/// </summary>
public sealed class SendProbe
{
    private readonly List<string> entries = [];
    private readonly List<CancellationToken> tokens = [];

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (entries)
                return entries.ToArray();
        }
    }

    /// <summary>The token the most recent handler was given.</summary>
    public CancellationToken Token
    {
        get
        {
            lock (entries)
                return tokens.Count > 0
                    ? tokens[tokens.Count - 1]
                    : throw new InvalidOperationException("No handler ran, so no cancellation token was recorded.");
        }
    }

    public void Record(string entry, CancellationToken cancellationToken)
    {
        lock (entries)
        {
            entries.Add(entry);
            tokens.Add(cancellationToken);
        }
    }
}

public static class SendFixtures
{
    public const string Payload = "payload";
    public const string FailureMessage = "handler exploded";
    public const string UnregisteredEntry = "unregistered";
    public const string NullRequestEntry = "null-request";
    public const string InnerEntry = "inner";
    public const string OuterEntry = "outer";
    public const string DualResponseEntry = "dual:response";
    public const string DualVoidEntry = "dual:void";
    public const string PagedClassEntry = "paged-class";
    public const string PagedRecordEntry = "paged-record";
    public const string PagedEchoEntry = "paged-echo";
    public const string GenericIntEntry = "generic:int";
    public const string GenericStringEntry = "generic:string";
    public const int ValueResponse = 42;
    public const int PagedTotalCount = 25;

    public static readonly IReadOnlyList<string> PageItems = ["a", "b", "c"];

    /// <summary>The <see cref="IRequest{TResponse}"/> of the given shape, as <see cref="object"/> so the call site binds to Send(object).</summary>
    public static object CreateResponseRequest(string shape, SendProbe probe) => shape switch
    {
        SendShapes.Class => new SendClassRequest { Value = Payload, Probe = probe },
        SendShapes.PositionalRecord => new SendPositionalRecordRequest(Payload, probe),
        SendShapes.InitRecord => new SendInitRecordRequest { Value = Payload, Probe = probe },
        SendShapes.ReadonlyRecordStruct => new SendReadonlyRecordStructRequest(Payload, probe),
        SendShapes.PlainStruct => new SendPlainStructRequest(Payload, probe),
        SendShapes.Nested => new NestedRequest { Value = Payload, Probe = probe },
        SendShapes.Annotated => new SendAnnotatedRequest { Id = 7, Force = true, Tenant = "acme", Value = Payload, Probe = probe },
        SendShapes.Unannotated => new SendUnannotatedRequest { Id = 7, Force = true, Tenant = "acme", Value = Payload, Probe = probe },
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown request shape.")
    };

    /// <summary>The <see cref="IRequest"/> of the given shape, as <see cref="object"/> so the call site binds to Send(object).</summary>
    public static object CreateVoidRequest(string shape, SendProbe probe) => shape switch
    {
        SendShapes.Class => new SendClassCommand { Probe = probe },
        SendShapes.PositionalRecord => new SendPositionalRecordCommand(probe),
        SendShapes.InitRecord => new SendInitRecordCommand { Probe = probe },
        SendShapes.ReadonlyRecordStruct => new SendReadonlyRecordStructCommand(probe),
        SendShapes.PlainStruct => new SendPlainStructCommand(probe),
        SendShapes.Nested => new NestedCommand { Probe = probe },
        SendShapes.Annotated => new SendAnnotatedCommand { Id = 7, Force = true, Tenant = "acme", Probe = probe },
        SendShapes.Unannotated => new SendUnannotatedCommand { Id = 7, Force = true, Tenant = "acme", Probe = probe },
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown request shape.")
    };

    // A request (and handler) declared as a nested type rather than at namespace scope.
    public sealed class NestedRequest : IRequest<SingleResponse<string>>
    {
        public string Value { get; set; } = string.Empty;
        public SendProbe? Probe { get; set; }
    }

    public sealed class NestedRequestHandler : IRequestHandler<NestedRequest, SingleResponse<string>>
    {
        public Task<SingleResponse<string>> Handle(NestedRequest request, CancellationToken cancellationToken)
        {
            request.Probe?.Record(SendShapes.Nested, cancellationToken);
            return Task.FromResult(new SingleResponse<string>(request.Value));
        }
    }

    public sealed class NestedCommand : IRequest
    {
        public SendProbe? Probe { get; set; }
    }

    public sealed class NestedCommandHandler : IRequestHandler<NestedCommand>
    {
        public Task Handle(NestedCommand request, CancellationToken cancellationToken)
        {
            request.Probe?.Record(SendShapes.Nested, cancellationToken);
            return Task.CompletedTask;
        }
    }
}

// -------------------------------------------------------------------------------------------------
// Requests with a response, one per declaration style
// -------------------------------------------------------------------------------------------------

public sealed class SendClassRequest : IRequest<SingleResponse<string>>
{
    public string Value { get; set; } = string.Empty;
    public SendProbe? Probe { get; set; }
}

public sealed class SendClassRequestHandler : IRequestHandler<SendClassRequest, SingleResponse<string>>
{
    public Task<SingleResponse<string>> Handle(SendClassRequest request, CancellationToken cancellationToken)
    {
        request.Probe?.Record(SendShapes.Class, cancellationToken);
        return Task.FromResult(new SingleResponse<string>(request.Value));
    }
}

public sealed record SendPositionalRecordRequest(string Value, SendProbe? Probe) : IRequest<SingleResponse<string>>;

public sealed class SendPositionalRecordRequestHandler : IRequestHandler<SendPositionalRecordRequest, SingleResponse<string>>
{
    public Task<SingleResponse<string>> Handle(SendPositionalRecordRequest request, CancellationToken cancellationToken)
    {
        request.Probe?.Record(SendShapes.PositionalRecord, cancellationToken);
        return Task.FromResult(new SingleResponse<string>(request.Value));
    }
}

public sealed record SendInitRecordRequest : IRequest<SingleResponse<string>>
{
    public string Value { get; init; } = string.Empty;
    public SendProbe? Probe { get; init; }
}

public sealed class SendInitRecordRequestHandler : IRequestHandler<SendInitRecordRequest, SingleResponse<string>>
{
    public Task<SingleResponse<string>> Handle(SendInitRecordRequest request, CancellationToken cancellationToken)
    {
        request.Probe?.Record(SendShapes.InitRecord, cancellationToken);
        return Task.FromResult(new SingleResponse<string>(request.Value));
    }
}

public readonly record struct SendReadonlyRecordStructRequest(string Value, SendProbe? Probe) : IRequest<SingleResponse<string>>;

public sealed class SendReadonlyRecordStructRequestHandler : IRequestHandler<SendReadonlyRecordStructRequest, SingleResponse<string>>
{
    public Task<SingleResponse<string>> Handle(SendReadonlyRecordStructRequest request, CancellationToken cancellationToken)
    {
        request.Probe?.Record(SendShapes.ReadonlyRecordStruct, cancellationToken);
        return Task.FromResult(new SingleResponse<string>(request.Value));
    }
}

public struct SendPlainStructRequest : IRequest<SingleResponse<string>>
{
    public SendPlainStructRequest(string value, SendProbe? probe)
    {
        Value = value;
        Probe = probe;
    }

    public string Value { get; set; }
    public SendProbe? Probe { get; set; }
}

public sealed class SendPlainStructRequestHandler : IRequestHandler<SendPlainStructRequest, SingleResponse<string>>
{
    public Task<SingleResponse<string>> Handle(SendPlainStructRequest request, CancellationToken cancellationToken)
    {
        request.Probe?.Record(SendShapes.PlainStruct, cancellationToken);
        return Task.FromResult(new SingleResponse<string>(request.Value));
    }
}

// The annotated twin: identical members, but bound from the route, query string and headers by Minimal APIs.
public sealed record SendAnnotatedRequest : IRequest<SingleResponse<string>>
{
    [FromRoute]
    public int Id { get; init; }

    [FromQuery]
    public bool Force { get; init; }

    [FromHeader(Name = "X-Send-Tenant")]
    public string? Tenant { get; init; }

    public string Value { get; init; } = string.Empty;

    public SendProbe? Probe { get; init; }
}

public sealed class SendAnnotatedRequestHandler : IRequestHandler<SendAnnotatedRequest, SingleResponse<string>>
{
    public Task<SingleResponse<string>> Handle(SendAnnotatedRequest request, CancellationToken cancellationToken)
    {
        request.Probe?.Record(SendShapes.Annotated, cancellationToken);
        return Task.FromResult(new SingleResponse<string>(request.Value));
    }
}

public sealed record SendUnannotatedRequest : IRequest<SingleResponse<string>>
{
    public int Id { get; init; }
    public bool Force { get; init; }
    public string? Tenant { get; init; }
    public string Value { get; init; } = string.Empty;
    public SendProbe? Probe { get; init; }
}

public sealed class SendUnannotatedRequestHandler : IRequestHandler<SendUnannotatedRequest, SingleResponse<string>>
{
    public Task<SingleResponse<string>> Handle(SendUnannotatedRequest request, CancellationToken cancellationToken)
    {
        request.Probe?.Record(SendShapes.Unannotated, cancellationToken);
        return Task.FromResult(new SingleResponse<string>(request.Value));
    }
}

// -------------------------------------------------------------------------------------------------
// Void requests, one per declaration style
// -------------------------------------------------------------------------------------------------

public sealed class SendClassCommand : IRequest
{
    public SendProbe? Probe { get; set; }
}

public sealed class SendClassCommandHandler : IRequestHandler<SendClassCommand>
{
    public Task Handle(SendClassCommand request, CancellationToken cancellationToken)
    {
        request.Probe?.Record(SendShapes.Class, cancellationToken);
        return Task.CompletedTask;
    }
}

public sealed record SendPositionalRecordCommand(SendProbe? Probe) : IRequest;

public sealed class SendPositionalRecordCommandHandler : IRequestHandler<SendPositionalRecordCommand>
{
    public Task Handle(SendPositionalRecordCommand request, CancellationToken cancellationToken)
    {
        request.Probe?.Record(SendShapes.PositionalRecord, cancellationToken);
        return Task.CompletedTask;
    }
}

public sealed record SendInitRecordCommand : IRequest
{
    public SendProbe? Probe { get; init; }
}

public sealed class SendInitRecordCommandHandler : IRequestHandler<SendInitRecordCommand>
{
    public Task Handle(SendInitRecordCommand request, CancellationToken cancellationToken)
    {
        request.Probe?.Record(SendShapes.InitRecord, cancellationToken);
        return Task.CompletedTask;
    }
}

public readonly record struct SendReadonlyRecordStructCommand(SendProbe? Probe) : IRequest;

public sealed class SendReadonlyRecordStructCommandHandler : IRequestHandler<SendReadonlyRecordStructCommand>
{
    public Task Handle(SendReadonlyRecordStructCommand request, CancellationToken cancellationToken)
    {
        request.Probe?.Record(SendShapes.ReadonlyRecordStruct, cancellationToken);
        return Task.CompletedTask;
    }
}

public struct SendPlainStructCommand : IRequest
{
    public SendPlainStructCommand(SendProbe? probe) => Probe = probe;

    public SendProbe? Probe { get; set; }
}

public sealed class SendPlainStructCommandHandler : IRequestHandler<SendPlainStructCommand>
{
    public Task Handle(SendPlainStructCommand request, CancellationToken cancellationToken)
    {
        request.Probe?.Record(SendShapes.PlainStruct, cancellationToken);
        return Task.CompletedTask;
    }
}

public sealed record SendAnnotatedCommand : IRequest
{
    [FromRoute]
    public int Id { get; init; }

    [FromQuery]
    public bool Force { get; init; }

    [FromHeader(Name = "X-Send-Tenant")]
    public string? Tenant { get; init; }

    public SendProbe? Probe { get; init; }
}

public sealed class SendAnnotatedCommandHandler : IRequestHandler<SendAnnotatedCommand>
{
    public Task Handle(SendAnnotatedCommand request, CancellationToken cancellationToken)
    {
        request.Probe?.Record(SendShapes.Annotated, cancellationToken);
        return Task.CompletedTask;
    }
}

public sealed record SendUnannotatedCommand : IRequest
{
    public int Id { get; init; }
    public bool Force { get; init; }
    public string? Tenant { get; init; }
    public SendProbe? Probe { get; init; }
}

public sealed class SendUnannotatedCommandHandler : IRequestHandler<SendUnannotatedCommand>
{
    public Task Handle(SendUnannotatedCommand request, CancellationToken cancellationToken)
    {
        request.Probe?.Record(SendShapes.Unannotated, cancellationToken);
        return Task.CompletedTask;
    }
}

// -------------------------------------------------------------------------------------------------
// Response edge cases
// -------------------------------------------------------------------------------------------------

public sealed class SendValueResponseRequest : IRequest<int>;

public sealed class SendValueResponseRequestHandler : IRequestHandler<SendValueResponseRequest, int>
{
    public Task<int> Handle(SendValueResponseRequest request, CancellationToken cancellationToken)
        => Task.FromResult(SendFixtures.ValueResponse);
}

public sealed class SendNullResponseRequest : IRequest<string?>;

public sealed class SendNullResponseRequestHandler : IRequestHandler<SendNullResponseRequest, string?>
{
    public Task<string?> Handle(SendNullResponseRequest request, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);
}

// Implements both request interfaces, so the wrapper has to choose between them.
public sealed class SendDualRequest : IRequest, IRequest<SingleResponse<string>>
{
    public SendProbe? Probe { get; set; }
}

public sealed class SendDualResponseHandler : IRequestHandler<SendDualRequest, SingleResponse<string>>
{
    public Task<SingleResponse<string>> Handle(SendDualRequest request, CancellationToken cancellationToken)
    {
        request.Probe?.Record(SendFixtures.DualResponseEntry, cancellationToken);
        return Task.FromResult(new SingleResponse<string>(SendFixtures.DualResponseEntry));
    }
}

public sealed class SendDualVoidHandler : IRequestHandler<SendDualRequest>
{
    public Task Handle(SendDualRequest request, CancellationToken cancellationToken)
    {
        request.Probe?.Record(SendFixtures.DualVoidEntry, cancellationToken);
        return Task.CompletedTask;
    }
}

// -------------------------------------------------------------------------------------------------
// Inheritance: only the base has a handler, so dispatch by runtime type and dispatch by static type part ways
// -------------------------------------------------------------------------------------------------

public class SendBaseRequest : IRequest<SingleResponse<string>>
{
    public SendProbe? Probe { get; set; }
}

// Echoes the runtime type it was handed, so a test can tell "the base handler ran on a derived instance"
// apart from "the base handler ran on a base instance".
public sealed class SendBaseRequestHandler : IRequestHandler<SendBaseRequest, SingleResponse<string>>
{
    public Task<SingleResponse<string>> Handle(SendBaseRequest request, CancellationToken cancellationToken)
    {
        var name = request.GetType().Name;
        request.Probe?.Record(name, cancellationToken);
        return Task.FromResult(new SingleResponse<string>(name));
    }
}

/// <summary>Deliberately left without a handler of its own.</summary>
public sealed class SendDerivedRequest : SendBaseRequest;

// -------------------------------------------------------------------------------------------------
// A generic request type, closed over two different arguments, with a handler per closing
// -------------------------------------------------------------------------------------------------

public sealed record SendGenericRequest<TPayload>(TPayload Payload, SendProbe? Probe) : IRequest<SingleResponse<string>>;

public sealed class SendGenericIntRequestHandler : IRequestHandler<SendGenericRequest<int>, SingleResponse<string>>
{
    public Task<SingleResponse<string>> Handle(SendGenericRequest<int> request, CancellationToken cancellationToken)
    {
        request.Probe?.Record(SendFixtures.GenericIntEntry, cancellationToken);
        return Task.FromResult(new SingleResponse<string>($"int:{request.Payload}"));
    }
}

public sealed class SendGenericStringRequestHandler : IRequestHandler<SendGenericRequest<string>, SingleResponse<string>>
{
    public Task<SingleResponse<string>> Handle(SendGenericRequest<string> request, CancellationToken cancellationToken)
    {
        request.Probe?.Record(SendFixtures.GenericStringEntry, cancellationToken);
        return Task.FromResult(new SingleResponse<string>($"string:{request.Payload}"));
    }
}

// -------------------------------------------------------------------------------------------------
// Paged requests
// -------------------------------------------------------------------------------------------------

public sealed class SendPagedClassRequest : IPagedRequest<string>
{
    public int PageNum { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public string? Query { get; set; }
    public SendProbe? Probe { get; set; }
}

public sealed class SendPagedClassRequestHandler : IRequestHandler<SendPagedClassRequest, MultiResponse<string>>
{
    public Task<MultiResponse<string>> Handle(SendPagedClassRequest request, CancellationToken cancellationToken)
    {
        request.Probe?.Record(SendFixtures.PagedClassEntry, cancellationToken);
        return Task.FromResult(new MultiResponse<string>(SendFixtures.PageItems, SendFixtures.PagedTotalCount, request.PageSize));
    }
}

public sealed record SendPagedRecordRequest(int PageNum, int PageSize, string? Query, SendProbe? Probe) : IPagedRequest<string>;

public sealed class SendPagedRecordRequestHandler : IRequestHandler<SendPagedRecordRequest, MultiResponse<string>>
{
    public Task<MultiResponse<string>> Handle(SendPagedRecordRequest request, CancellationToken cancellationToken)
    {
        request.Probe?.Record(SendFixtures.PagedRecordEntry, cancellationToken);
        return Task.FromResult(new MultiResponse<string>(SendFixtures.PageItems, SendFixtures.PagedTotalCount, request.PageSize));
    }
}

/// <summary>Reports the three <see cref="IPagedRequest{TItem}"/> members back as the page's data.</summary>
public sealed record SendPagedEchoRequest(int PageNum, int PageSize, string? Query, SendProbe? Probe) : IPagedRequest<string>;

public sealed class SendPagedEchoRequestHandler : IRequestHandler<SendPagedEchoRequest, MultiResponse<string>>
{
    public Task<MultiResponse<string>> Handle(SendPagedEchoRequest request, CancellationToken cancellationToken)
    {
        request.Probe?.Record(SendFixtures.PagedEchoEntry, cancellationToken);

        string[] members =
        [
            $"page:{request.PageNum}",
            $"size:{request.PageSize}",
            $"query:{request.Query ?? "<null>"}"
        ];

        return Task.FromResult(new MultiResponse<string>(members, SendFixtures.PagedTotalCount, request.PageSize));
    }
}

// -------------------------------------------------------------------------------------------------
// Failure, cancellation and re-entrancy fixtures
// -------------------------------------------------------------------------------------------------

public sealed class SendHandlerFailure(string message) : Exception(message);

public sealed class SendThrowingRequest : IRequest<SingleResponse<string>>;

public sealed class SendThrowingHandler : IRequestHandler<SendThrowingRequest, SingleResponse<string>>
{
    public async Task<SingleResponse<string>> Handle(SendThrowingRequest request, CancellationToken cancellationToken)
    {
        await Task.Yield();
        throw new SendHandlerFailure(SendFixtures.FailureMessage);
    }
}

public sealed class SendThrowingCommand : IRequest;

public sealed class SendThrowingCommandHandler : IRequestHandler<SendThrowingCommand>
{
    public async Task Handle(SendThrowingCommand request, CancellationToken cancellationToken)
    {
        await Task.Yield();
        throw new SendHandlerFailure(SendFixtures.FailureMessage);
    }
}

public sealed class SendCancellationAwareRequest : IRequest<SingleResponse<string>>
{
    public SendProbe? Probe { get; set; }
}

public sealed class SendCancellationAwareHandler : IRequestHandler<SendCancellationAwareRequest, SingleResponse<string>>
{
    public Task<SingleResponse<string>> Handle(SendCancellationAwareRequest request, CancellationToken cancellationToken)
    {
        // Record first: the test asserts on the token the handler was handed, even though it then bails out.
        request.Probe?.Record("cancellation-aware", cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SingleResponse<string>(SendFixtures.Payload));
    }
}

public sealed class SendInnerRequest : IRequest<SingleResponse<string>>
{
    public SendProbe? Probe { get; set; }
}

public sealed class SendInnerRequestHandler : IRequestHandler<SendInnerRequest, SingleResponse<string>>
{
    public Task<SingleResponse<string>> Handle(SendInnerRequest request, CancellationToken cancellationToken)
    {
        request.Probe?.Record(SendFixtures.InnerEntry, cancellationToken);
        return Task.FromResult(new SingleResponse<string>(SendFixtures.InnerEntry));
    }
}

public sealed class SendOuterRequest : IRequest<SingleResponse<string>>
{
    public SendProbe? Probe { get; set; }
}

// -------------------------------------------------------------------------------------------------
// Requests deliberately left without a discoverable handler
// -------------------------------------------------------------------------------------------------

public sealed class SendUnregisteredRequest : IRequest<SingleResponse<string>>
{
    public SendProbe? Probe { get; set; }
}

public sealed class SendUnregisteredCommand : IRequest;

public sealed class SendCountedRequest : IRequest<SingleResponse<string>>;

public sealed class SendMissingDependencyRequest : IRequest<SingleResponse<string>>;

public sealed class SendScopedRequest : IRequest<SingleResponse<string>>;

public sealed class SendNullTolerantRequest : IRequest<SingleResponse<string>>;

/// <summary>A dependency no test ever registers, so activating its handler must fail.</summary>
public sealed class SendMissingDependency;

/// <summary>Counts how many times a handler was activated.</summary>
public sealed class SendActivationCounter
{
    private int count;

    public int Count => Volatile.Read(ref count);

    public void Increment() => Interlocked.Increment(ref count);
}

/// <summary>Scoped, so two scopes get two different ids.</summary>
public sealed class SendScopedMarker
{
    public string Id { get; } = Guid.NewGuid().ToString();
}

/// <summary>
/// Handlers with constructor dependencies. Nesting them in an open generic keeps
/// <c>ContainsGenericParameters</c> true, so the assembly scan skips them and each test registers only the
/// one it needs.
/// </summary>
public static class SendHost<TMarker>
{
    public sealed class UnregisteredRequestHandler : IRequestHandler<SendUnregisteredRequest, SingleResponse<string>>
    {
        public Task<SingleResponse<string>> Handle(SendUnregisteredRequest request, CancellationToken cancellationToken)
        {
            request.Probe?.Record(SendFixtures.UnregisteredEntry, cancellationToken);
            return Task.FromResult(new SingleResponse<string>(SendFixtures.Payload));
        }
    }

    public sealed class CountingHandler : IRequestHandler<SendCountedRequest, SingleResponse<string>>
    {
        public CountingHandler(SendActivationCounter counter) => counter.Increment();

        public Task<SingleResponse<string>> Handle(SendCountedRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new SingleResponse<string>(SendFixtures.Payload));
    }

    public sealed class MissingDependencyHandler(SendMissingDependency dependency)
        : IRequestHandler<SendMissingDependencyRequest, SingleResponse<string>>
    {
        public Task<SingleResponse<string>> Handle(SendMissingDependencyRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new SingleResponse<string>(dependency.ToString() ?? SendFixtures.Payload));
    }

    public sealed class ScopedDependencyHandler(SendScopedMarker marker)
        : IRequestHandler<SendScopedRequest, SingleResponse<string>>
    {
        public Task<SingleResponse<string>> Handle(SendScopedRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new SingleResponse<string>(marker.Id));
    }

    public sealed class NullTolerantHandler(SendProbe probe) : IRequestHandler<SendNullTolerantRequest, SingleResponse<string>>
    {
        public Task<SingleResponse<string>> Handle(SendNullTolerantRequest request, CancellationToken cancellationToken)
        {
            var entry = request is null ? SendFixtures.NullRequestEntry : "instance";
            probe.Record(entry, cancellationToken);
            return Task.FromResult(new SingleResponse<string>(entry));
        }
    }

    public sealed class ReentrantHandler(ISender sender) : IRequestHandler<SendOuterRequest, SingleResponse<string>>
    {
        public async Task<SingleResponse<string>> Handle(SendOuterRequest request, CancellationToken cancellationToken)
        {
            var inner = await sender.Send<SendInnerRequest, SingleResponse<string>>(
                new SendInnerRequest { Probe = request.Probe }, cancellationToken);

            request.Probe?.Record(SendFixtures.OuterEntry, cancellationToken);
            return new SingleResponse<string>($"{SendFixtures.OuterEntry}({inner.Result})");
        }
    }
}

// -------------------------------------------------------------------------------------------------
// Pipeline behaviors. Nothing registers these automatically, so declaring them here is safe; each test
// registers the ones it wants.
// -------------------------------------------------------------------------------------------------

public sealed class SendResponseBehavior : IPipelineBehavior<SendClassRequest, SingleResponse<string>>
{
    public async Task<SingleResponse<string>> Handle(SendClassRequest request, RequestHandlerDelegate<SingleResponse<string>> next, CancellationToken cancellationToken)
    {
        request.Probe?.Record("behavior:before", cancellationToken);
        var response = await next();
        request.Probe?.Record("behavior:after", cancellationToken);
        return response;
    }
}

public sealed class SendFirstVoidBehavior : IPipelineBehavior<SendClassCommand>
{
    public async Task Handle(SendClassCommand request, RequestHandlerDelegate next, CancellationToken cancellationToken)
    {
        request.Probe?.Record("first:before", cancellationToken);
        await next();
        request.Probe?.Record("first:after", cancellationToken);
    }
}

public sealed class SendSecondVoidBehavior : IPipelineBehavior<SendClassCommand>
{
    public async Task Handle(SendClassCommand request, RequestHandlerDelegate next, CancellationToken cancellationToken)
    {
        request.Probe?.Record("second:before", cancellationToken);
        await next();
        request.Probe?.Record("second:after", cancellationToken);
    }
}

/// <summary>Implements neither request interface, so the boxed overload has no wrapper to build for it.</summary>
public sealed class SendNotARequest;
