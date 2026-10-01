using Microsoft.Extensions.DependencyInjection;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Mediator;
using Xunit;

namespace Phoenix.Mediator.Tests;

/// <summary>
/// The pipeline-behavior contract: <see cref="IPipelineBehavior{TRequest, TResponse}"/>,
/// <see cref="IPipelineBehavior{TRequest}"/> and the two chain-building loops in
/// <c>Mediator.SendInternal</c> / <c>Mediator.SendInternalVoid</c>.
/// <para>
/// README: "Pipeline behaviors are opt-in and run in registration order (first registered =
/// OUTERMOST)." Everything below pins that down for both pipelines, including short-circuiting,
/// response transformation, cancellation flow, exception flow, lifetimes and the boxed
/// <c>Send(object)</c> entry point.
/// </para>
/// <para>
/// Every handler and behavior used here lives in <see cref="PipeHost{TMarker}"/> so the assembly
/// scan other test files run (<c>AddMediator(assembly)</c>) skips them — they carry the host's
/// type parameter — and each test registers exactly the ones it wants, by hand.
/// </para>
/// </summary>
public sealed class PipelineBehaviorTests
{
    private const string RequestValue = "v";
    private const string HandlerResponse = "handled:v";

    // ---------------------------------------------------------------------------------------
    // IPipelineBehavior<TRequest, TResponse> — ordering and nesting
    // ---------------------------------------------------------------------------------------

    // MediatorRegressionTests pins two behaviors; three is what distinguishes "outermost first,
    // unwound in reverse" from "runs in registration order on the way out too".
    [Fact]
    public async Task Send_WithThreeBehaviors_RunsThemOutermostFirstAndUnwindsInReverse()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.FirstBehavior>();
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.SecondBehavior>();
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.ThirdBehavior>();
        });
        using var scope = provider.CreateScope();

        var response = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });

        Assert.Equal(HandlerResponse, response);
        Assert.Equal(
            new[] { "first:before", "second:before", "third:before", "handler", "third:after", "second:after", "first:after" },
            log.Snapshot());
    }

    // Nesting must follow REGISTRATION order, not type name, assembly order or anything else a
    // reflection/DI implementation detail might leak. Registering the same three in reverse must
    // mirror the sequence exactly.
    [Fact]
    public async Task Send_WithBehaviorsRegisteredInReverseOrder_ReversesTheNesting()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.ThirdBehavior>();
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.SecondBehavior>();
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.FirstBehavior>();
        });
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });

        Assert.Equal(
            new[] { "third:before", "second:before", "first:before", "handler", "first:after", "second:after", "third:after" },
            log.Snapshot());
    }

    // Short-circuiting is how caching, idempotency and authorization behaviors work: they answer
    // without ever touching the handler.
    [Fact]
    public async Task Send_WhenABehaviorDoesNotCallNext_SkipsTheHandlerAndReturnsItsOwnResponse()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.ShortCircuitBehavior>();
        });
        using var scope = provider.CreateScope();

        var response = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });

        Assert.Equal("short-circuited", response);
        // The whole sequence, not just "the handler did not run": the behavior must run exactly once
        // and nothing else may be invoked on its behalf.
        Assert.Equal(new[] { "short-circuit" }, log.Snapshot());
    }

    // Only the behaviors nested INSIDE the short-circuiting one are skipped; the ones wrapping it
    // still see their "after" half, because they are already awaiting its result.
    [Fact]
    public async Task Send_WhenAnOuterBehaviorShortCircuits_InnerBehaviorsNeverRun()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.FirstBehavior>();
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.ShortCircuitBehavior>();
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.SecondBehavior>();
        });
        using var scope = provider.CreateScope();

        var response = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });

        Assert.Equal("short-circuited", response);
        Assert.Equal(new[] { "first:before", "short-circuit", "first:after" }, log.Snapshot());
    }

    [Fact]
    public async Task Send_WithATransformingBehavior_ReturnsTheTransformedResponse()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.TransformBehavior>();
        });
        using var scope = provider.CreateScope();

        var response = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });

        // The caller gets the behavior's value, not the handler's.
        Assert.Equal(HandlerResponse + "+transformed", response);
        Assert.Equal(new[] { "transform", "handler" }, log.Snapshot());
    }

    // Two transforms compose on the way OUT, so the innermost one runs first. Which of the two is
    // outermost has to change the final string, otherwise the ordering assertion proves nothing.
    [Theory]
    [InlineData(true, "HANDLED:V+transformed")]
    [InlineData(false, "HANDLED:V+TRANSFORMED")]
    public async Task Send_WithTwoTransformingBehaviors_AppliesThemInnermostFirst(bool transformOutermost, string expected)
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);

            if (transformOutermost)
            {
                services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.TransformBehavior>();
                services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.UpperCaseBehavior>();
            }
            else
            {
                services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.UpperCaseBehavior>();
                services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.TransformBehavior>();
            }
        });
        using var scope = provider.CreateScope();

        var response = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });

        Assert.Equal(expected, response);
    }

    // ---------------------------------------------------------------------------------------
    // Response values the chain has to carry through untouched
    // ---------------------------------------------------------------------------------------

    // Every previous test uses a string response, where "no value" and null look the same. A struct
    // response proves RequestHandlerDelegate<TResponse> composes for value types, and — through the
    // boxed overload — that CreateWrapper picked RequestResponseWrapper: the void wrapper returns
    // null, so a mis-ordered branch there would silently hand callers null instead of 11.
    [Fact]
    public async Task Send_WithAValueTypeResponse_RunsTheBehaviorsAndReturnsTheValueFromBothOverloads()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            services.AddTransient<IRequestHandler<PipeCountRequest, int>, PipeHost<object>.CountHandler>();
            services.AddTransient<IPipelineBehavior<PipeCountRequest, int>, PipeHost<object>.IncrementBehavior>();
        });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var typed = await sender.Send<PipeCountRequest, int>(new PipeCountRequest());
        var boxed = await sender.Send((object)new PipeCountRequest());

        Assert.Equal(11, typed);
        Assert.Equal(11, Assert.IsType<int>(boxed));
        Assert.Equal(new[] { "increment", "count-handler", "increment", "count-handler" }, log.Snapshot());
    }

    // A null response is a legitimate answer ("not found"), and the chain must not turn it into an
    // exception or a default. Note the boxed overload cannot distinguish it from a void request —
    // both report null — which is exactly why the typed overload exists.
    [Fact]
    public async Task Send_WhenTheHandlerReturnsNull_TheNullReachesTheBehaviorsAndTheCaller()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            services.AddTransient<IRequestHandler<PipeStringRequest, string>, PipeHost<object>.NullStringHandler>();
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.NullObservingBehavior>();
        });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var typed = await sender.Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });
        var boxed = await sender.Send((object)new PipeStringRequest { Value = RequestValue });

        Assert.Null(typed);
        Assert.Null(boxed);
        Assert.Equal(new[] { "handler", "saw:null", "handler", "saw:null" }, log.Snapshot());
    }

    [Fact]
    public async Task Send_WithNoBehaviorsRegistered_CallsTheHandlerExactlyOnce()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, AddStringHandler);
        using var scope = provider.CreateScope();

        var response = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });

        Assert.Equal(HandlerResponse, response);
        Assert.Equal(new[] { "handler" }, log.Snapshot());
    }

    // SendInternal resolves the handler BEFORE it composes the chain, so a missing handler is not
    // something a short-circuiting behavior can paper over: a cache/idempotency behavior that would
    // have answered on its own never gets the chance. Worth pinning — the failure is otherwise a
    // confusing "my cache behavior stopped working" once someone forgets to register a handler.
    [Fact]
    public async Task Send_WhenNoHandlerIsRegistered_ThrowsWithoutRunningAnyBehavior()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.ShortCircuitBehavior>());
        using var scope = provider.CreateScope();

        // The message is the container's, so only the type is asserted.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<ISender>()
                .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue }));

        Assert.Empty(log.Snapshot());
    }

    [Fact]
    public async Task SendVoid_WhenNoHandlerIsRegistered_ThrowsWithoutRunningAnyBehavior()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
            services.AddTransient<IPipelineBehavior<PipeVoidRequest>, PipeHost<object>.VoidShortCircuitBehavior>());
        using var scope = provider.CreateScope();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<ISender>().Send(new PipeVoidRequest()));

        Assert.Empty(log.Snapshot());
    }

    // A behavior is bound to one request type. If IPipelineBehavior<X, R> leaked into the pipeline
    // for Y, an authorization or transaction behavior would fire on requests it was never meant for.
    [Fact]
    public async Task Send_WithABehaviorRegisteredForAnotherRequestType_DoesNotRunThatBehavior()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddTransient<IRequestHandler<PipeOtherRequest, string>, PipeHost<object>.OtherHandler>();
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.FirstBehavior>();
            services.AddTransient<IPipelineBehavior<PipeOtherRequest, string>, PipeHost<object>.OtherBehavior>();
        });
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });

        Assert.Equal(new[] { "first:before", "handler", "first:after" }, log.Snapshot());
    }

    // ---------------------------------------------------------------------------------------
    // Open generic behaviors (how AddMediatorSentry / AddMediatorValidation register theirs)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Send_WithAnOpenGenericBehavior_RunsItForEveryResponseRequestType()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddTransient<IRequestHandler<PipeOtherRequest, string>, PipeHost<object>.OtherHandler>();
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(PipeOpenBehavior<,>));
        });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await sender.Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });
        await sender.Send<PipeOtherRequest, string>(new PipeOtherRequest(7));

        Assert.Equal(
            new[]
            {
                "open:PipeStringRequest:before", "handler", "open:PipeStringRequest:after",
                "open:PipeOtherRequest:before", "other-handler", "open:PipeOtherRequest:after",
            },
            log.Snapshot());
    }

    // An open generic registered first must wrap the closed one, exactly like AddMediatorSentry()
    // before AddMediatorValidation() makes the Sentry span wrap validation.
    [Fact]
    public async Task Send_OpenGenericBehaviorRegisteredFirst_WrapsTheClosedBehavior()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(PipeOpenBehavior<,>));
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.FirstBehavior>();
        });
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });

        Assert.Equal(
            new[] { "open:PipeStringRequest:before", "first:before", "handler", "first:after", "open:PipeStringRequest:after" },
            log.Snapshot());
    }

    [Fact]
    public async Task Send_ClosedBehaviorRegisteredFirst_WrapsTheOpenGenericBehavior()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.FirstBehavior>();
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(PipeOpenBehavior<,>));
        });
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });

        Assert.Equal(
            new[] { "first:before", "open:PipeStringRequest:before", "handler", "open:PipeStringRequest:after", "first:after" },
            log.Snapshot());
    }

    // ---------------------------------------------------------------------------------------
    // Cancellation
    // ---------------------------------------------------------------------------------------

    // Every behavior gets the caller's token, unchanged — a behavior that linked or swallowed it
    // would silently make the whole pipeline uncancellable.
    [Fact]
    public async Task Send_PassesTheCallersCancellationTokenToEveryBehaviorAndTheHandler()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.FirstBehavior>();
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.SecondBehavior>();
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.ThirdBehavior>();
        });
        using var scope = provider.CreateScope();
        using var cts = new CancellationTokenSource();

        await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue }, cts.Token);

        var tokens = log.Tokens();
        Assert.Equal(4, tokens.Length);
        // Equality with cts.Token already implies CanBeCanceled: a token is equal to another only when
        // it comes from the same source, so a separate CanBeCanceled assertion could never fail on its own.
        Assert.All(tokens, token => Assert.Equal(cts.Token, token));
    }

    [Fact]
    public async Task Send_WithoutACancellationToken_PassesNoneToEveryBehaviorAndTheHandler()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.FirstBehavior>();
        });
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });

        var tokens = log.Tokens();
        Assert.Equal(2, tokens.Length);
        Assert.All(tokens, token => Assert.Equal(CancellationToken.None, token));
    }

    // A canceled token has to surface as a cancellation the enclosing behaviors can see — that is
    // what lets SentryBehavior finish its span as Cancelled instead of reporting an error.
    [Fact]
    public async Task Send_WhenTheTokenIsAlreadyCanceled_TheCancellationSurfacesThroughThePipeline()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.CatchAndRethrowBehavior>();
        });
        using var scope = provider.CreateScope();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scope.ServiceProvider.GetRequiredService<ISender>()
                .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue }, cts.Token));

        // The exact shape, not just "something was caught": the handler ran (the mediator does not
        // pre-empt a canceled token itself) and exactly one behavior observed the cancellation.
        var entries = log.Snapshot();
        Assert.Equal(2, entries.Length);
        Assert.Equal("handler", entries[0]);
        Assert.StartsWith("caught:", entries[1], StringComparison.Ordinal);
        Assert.Same(thrown, Assert.Single(log.Instances()));
    }

    // The mediator never inspects the token itself — it only hands it to behaviors and the handler.
    // That matters: an audit/tracing behavior wrapping a canceled request still gets to run and record
    // the cancellation, and a short-circuiting cache can still answer. If SendInternal ever grew a
    // ThrowIfCancellationRequested of its own, this send would start throwing instead of returning.
    [Fact]
    public async Task Send_WhenTheTokenIsCanceledButNothingObservesIt_CompletesNormally()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.ShortCircuitBehavior>();
        });
        using var scope = provider.CreateScope();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var response = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue }, cts.Token);

        Assert.Equal("short-circuited", response);
        Assert.Equal(new[] { "short-circuit" }, log.Snapshot());
    }

    // ---------------------------------------------------------------------------------------
    // Exception flow
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Send_WhenABehaviorThrowsBeforeNext_NeverRunsTheHandlerAndPropagates()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.ThrowBeforeNextBehavior>();
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.FirstBehavior>();
        });
        using var scope = provider.CreateScope();

        var thrown = await Assert.ThrowsAsync<PipeBehaviorException>(() =>
            scope.ServiceProvider.GetRequiredService<ISender>()
                .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue }));

        Assert.Equal("behavior failed before next", thrown.Message);
        Assert.Equal(new[] { "throw:before" }, log.Snapshot());
    }

    [Fact]
    public async Task Send_WhenABehaviorThrowsAfterNext_StillRanTheHandlerAndPropagates()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.ThrowAfterNextBehavior>();
        });
        using var scope = provider.CreateScope();

        var thrown = await Assert.ThrowsAsync<PipeBehaviorException>(() =>
            scope.ServiceProvider.GetRequiredService<ISender>()
                .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue }));

        Assert.Equal("behavior failed after next", thrown.Message);
        Assert.Equal(new[] { "handler", "throw:after" }, log.Snapshot());
    }

    // The handler here throws SYNCHRONOUSLY (before returning a Task). An enclosing behavior must
    // still catch it in its try/catch around next(), and the caller must get the same instance —
    // no AggregateException, no re-wrapping, stack trace intact.
    [Fact]
    public async Task Send_WhenTheHandlerThrows_TheEnclosingBehaviorSeesTheSameExceptionInstance()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            services.AddTransient<IRequestHandler<PipeStringRequest, string>, PipeHost<object>.ThrowingStringHandler>();
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.CatchAndRethrowBehavior>();
        });
        using var scope = provider.CreateScope();

        var thrown = await Assert.ThrowsAsync<PipeHandlerException>(() =>
            scope.ServiceProvider.GetRequiredService<ISender>()
                .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue }));

        Assert.Equal("handler blew up", thrown.Message);
        Assert.Equal(1, log.Occurrences("caught:PipeHandlerException"));
        Assert.Same(thrown, Assert.Single(log.Instances()));
    }

    // Rethrowing from an inner behavior must keep travelling outwards: logging/tracing behaviors
    // registered further out only work if they still observe the failure.
    [Fact]
    public async Task Send_WhenAnInnerBehaviorRethrows_EveryEnclosingBehaviorSeesTheException()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            services.AddTransient<IRequestHandler<PipeStringRequest, string>, PipeHost<object>.ThrowingStringHandler>();
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.CatchAndRethrowBehavior>();
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.CatchAndRethrowBehavior>();
        });
        using var scope = provider.CreateScope();

        var thrown = await Assert.ThrowsAsync<PipeHandlerException>(() =>
            scope.ServiceProvider.GetRequiredService<ISender>()
                .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue }));

        Assert.Equal(2, log.Occurrences("caught:PipeHandlerException"));
        // Length first: Assert.All over an empty array passes vacuously.
        var captured = log.Instances();
        Assert.Equal(2, captured.Length);
        Assert.All(captured, candidate => Assert.Same(thrown, candidate));
        // Nothing re-wrapped it on the way out: one handler call, two observers, same instance.
        Assert.Equal(1, log.Occurrences("handler"));
    }

    // The fallback/circuit-breaker shape: swallow the failure and answer with a default.
    [Fact]
    public async Task Send_WhenABehaviorSwallowsTheHandlerException_ReturnsItsFallbackResponse()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            services.AddTransient<IRequestHandler<PipeStringRequest, string>, PipeHost<object>.ThrowingStringHandler>();
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.SwallowBehavior>();
        });
        using var scope = provider.CreateScope();

        var response = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });

        Assert.Equal("fallback", response);
        Assert.Equal(new[] { "handler", "swallowed" }, log.Snapshot());
    }

    // ---------------------------------------------------------------------------------------
    // Lifetimes and per-send resolution
    // ---------------------------------------------------------------------------------------

    // Behaviors are resolved per send, so a transient one must never be shared between two sends —
    // otherwise per-request state a behavior holds (a stopwatch, a correlation id) bleeds across.
    [Fact]
    public async Task Send_ResolvesADistinctBehaviorInstanceForEachSend()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.InstanceCapturingBehavior>();
        });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await sender.Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });
        await sender.Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });

        var instances = log.Instances();
        Assert.Equal(2, instances.Length);
        Assert.NotSame(instances[0], instances[1]);
    }

    // ...but the lifetime is the container's decision, not the mediator's: a singleton registration
    // really is reused, which is why a stateful behavior must not be registered as one.
    [Fact]
    public async Task Send_WithASingletonBehaviorRegistration_ReusesTheSameInstanceAcrossSends()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddSingleton<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.InstanceCapturingBehavior>();
        });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await sender.Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });
        await sender.Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });

        var instances = log.Instances();
        Assert.Equal(2, instances.Length);
        Assert.Same(instances[0], instances[1]);
    }

    // A behavior may depend on scoped services (DbContext, current user). Within one scope the two
    // sends must share that dependency — a transaction behavior committing "the" unit of work
    // depends on it being the same one the handler used.
    [Fact]
    public async Task Send_BehaviorResolvingAScopedDependency_SharesItBetweenSendsInTheSameScope()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddScoped<PipeScopedMarker>();
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.ScopedCapturingBehavior>();
        });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await sender.Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });
        await sender.Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });

        var markers = log.Instances();
        Assert.Equal(2, markers.Length);
        Assert.Same(markers[0], markers[1]);
    }

    [Fact]
    public async Task Send_BehaviorResolvingAScopedDependency_GetsADifferentInstanceInAnotherScope()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddScoped<PipeScopedMarker>();
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.ScopedCapturingBehavior>();
        });

        using (var first = provider.CreateScope())
        {
            await first.ServiceProvider.GetRequiredService<ISender>()
                .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });
        }

        using (var second = provider.CreateScope())
        {
            await second.ServiceProvider.GetRequiredService<ISender>()
                .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });
        }

        var markers = log.Instances();
        Assert.Equal(2, markers.Length);
        Assert.NotSame(markers[0], markers[1]);
    }

    // SendInternal calls GetServices<IPipelineBehavior<,>>() on every send rather than caching the
    // array on the (scoped) mediator. A factory registration whose answer changes between sends
    // proves it: with a cached list the second send would still run the first send's behavior.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Send_ReResolvesTheBehaviorListOnEverySend(bool useTheSameScope)
    {
        var log = new PipeCallLog();
        var toggle = new PipeBehaviorToggle();
        var resolutions = 0;

        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>>(_ =>
            {
                resolutions++;

                if (toggle.Enabled)
                    return new PipeHost<object>.FirstBehavior(log);

                return new PipeHost<object>.PassThroughBehavior();
            });
        });

        var firstScope = provider.CreateScope();
        await firstScope.ServiceProvider.GetRequiredService<ISender>()
            .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });

        Assert.Equal(new[] { "handler" }, log.Snapshot());

        toggle.Enabled = true;
        var secondScope = useTheSameScope ? firstScope : provider.CreateScope();

        await secondScope.ServiceProvider.GetRequiredService<ISender>()
            .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });

        Assert.Equal(new[] { "handler", "first:before", "handler", "first:after" }, log.Snapshot());
        Assert.Equal(2, resolutions);

        firstScope.Dispose();

        if (!useTheSameScope)
            secondScope.Dispose();
    }

    // Transient behaviors that implement IDisposable are tracked by the SCOPE, not released after
    // each send. In a long-lived scope (a background worker looping over sends) that is a leak, so
    // the lifetime is worth pinning down.
    [Fact]
    public async Task Send_WithDisposableTransientBehaviors_DisposesThemWhenTheScopeEndsNotAfterEachSend()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.DisposableBehavior>();
        });

        var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await sender.Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });
        await sender.Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });

        Assert.Equal(0, log.Occurrences("dispose"));

        scope.Dispose();

        Assert.Equal(2, log.Occurrences("dispose"));
    }

    // ---------------------------------------------------------------------------------------
    // The request instance itself
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Send_EveryBehaviorAndTheHandler_ReceiveTheExactRequestInstanceThatWasSent()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.FirstBehavior>();
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.SecondBehavior>();
        });
        using var scope = provider.CreateScope();
        var request = new PipeStringRequest { Value = RequestValue };

        await scope.ServiceProvider.GetRequiredService<ISender>().Send<PipeStringRequest, string>(request);

        var seen = log.Requests();
        Assert.Equal(3, seen.Length);
        Assert.All(seen, candidate => Assert.Same(request, candidate));
    }

    // Enrichment behaviors (stamping a tenant id, a user id, a correlation id onto the request)
    // only work because the handler sees the same mutated instance.
    [Fact]
    public async Task Send_WhenABehaviorMutatesTheRequest_TheHandlerSeesTheChange()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.EnrichingBehavior>();
        });
        using var scope = provider.CreateScope();
        var request = new PipeStringRequest { Value = RequestValue };

        var response = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<PipeStringRequest, string>(request);

        Assert.Equal("handled:enriched", response);
        Assert.Equal("enriched", request.Value);
    }

    // next() is a delegate over the rest of the chain, not a memoized task: calling it twice really
    // runs the inner pipeline twice. Retry behaviors depend on exactly this.
    [Fact]
    public async Task Send_WhenABehaviorCallsNextTwice_TheInnerPipelineRunsTwice()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.CallNextTwiceBehavior>();
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.FirstBehavior>();
        });
        using var scope = provider.CreateScope();

        var response = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });

        Assert.Equal(HandlerResponse, response);
        Assert.Equal(
            new[] { "call-next-twice", "first:before", "handler", "first:after", "first:before", "handler", "first:after" },
            log.Snapshot());
    }

    // The chain is composed ONCE per send, from behaviors and a handler resolved up front — so a retry
    // behavior re-running next() reuses the very same inner objects. A behavior that opened a
    // transaction or started a stopwatch on the first attempt is therefore still holding it on the
    // second; rebuilding the chain lazily inside next() would change that silently.
    [Fact]
    public async Task Send_WhenABehaviorCallsNextTwice_ReusesTheSameInnerBehaviorAndHandlerInstances()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            services.AddTransient<IRequestHandler<PipeStringRequest, string>, PipeHost<object>.InstanceCapturingStringHandler>();
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.CallNextTwiceBehavior>();
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.InstanceCapturingBehavior>();
        });
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });

        // Captured as behavior, handler, behavior, handler — the two attempts run sequentially.
        var instances = log.Instances();
        Assert.Equal(4, instances.Length);
        Assert.Same(instances[0], instances[2]);
        Assert.Same(instances[1], instances[3]);
        Assert.NotSame(instances[0], instances[1]);
    }

    // ---------------------------------------------------------------------------------------
    // Boxed / interface-typed dispatch reuses the same pipeline
    // ---------------------------------------------------------------------------------------

    // Mediator claims its RequestHandlerWrapper reuses the exact same SendInternal, so the boxed
    // Send(object) — what an endpoint sending a request as object and any polymorphic dispatch take — must not silently
    // skip behaviors. Same request, both entry points, identical sequence.
    [Fact]
    public async Task Send_TypedAndBoxedOverloads_RunTheIdenticalBehaviorSequence()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.FirstBehavior>();
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.SecondBehavior>();
        });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var expected = new[] { "first:before", "second:before", "handler", "second:after", "first:after" };

        var typed = await sender.Send<PipeStringRequest, string>(new PipeStringRequest { Value = RequestValue });
        var typedSequence = log.Snapshot();
        log.Clear();

        var boxed = await sender.Send((object)new PipeStringRequest { Value = RequestValue });
        var boxedSequence = log.Snapshot();

        Assert.Equal(expected, typedSequence);
        Assert.Equal(expected, boxedSequence);
        Assert.Equal(HandlerResponse, typed);
        Assert.Equal(HandlerResponse, Assert.IsType<string>(boxed));
    }

    // A variable typed as IRequest<TResponse> binds TRequest to the interface, which Mediator
    // re-dispatches by runtime type. That detour must land in the same pipeline.
    [Fact]
    public async Task Send_InterfaceTypedRequest_StillRunsTheConcreteRequestsBehaviors()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.FirstBehavior>();
        });
        using var scope = provider.CreateScope();
        IRequest<string> request = new PipeStringRequest { Value = RequestValue };

        var response = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<IRequest<string>, string>(request);

        Assert.Equal(HandlerResponse, response);
        Assert.Equal(new[] { "first:before", "handler", "first:after" }, log.Snapshot());
    }

    // The flip side of that re-dispatch: because the interface-typed send is resolved by RUNTIME type,
    // a behavior registered against IRequest<string> itself never runs. Register behaviors against the
    // concrete request type (or open-generically) — an interface-typed registration is dead code.
    [Fact]
    public async Task Send_InterfaceTypedRequest_IgnoresBehaviorsRegisteredAgainstTheInterface()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddTransient<IPipelineBehavior<IRequest<string>, string>, PipeHost<object>.InterfaceRegisteredBehavior>();
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.FirstBehavior>();
        });
        using var scope = provider.CreateScope();
        IRequest<string> request = new PipeStringRequest { Value = RequestValue };

        await scope.ServiceProvider.GetRequiredService<ISender>().Send<IRequest<string>, string>(request);

        Assert.Equal(new[] { "first:before", "handler", "first:after" }, log.Snapshot());
    }

    // The typed overload composes the pipeline for the type argument it was given, NOT for the object's
    // runtime type: handing a derived request to Send<TBase, TResponse> runs the base's handler and the
    // base's behaviors. Anything else would make `Send<TBase, R>(derived)` silently route elsewhere.
    [Fact]
    public async Task Send_TypedOverloadWithADerivedRequestInstance_RunsTheDeclaredTypesPipeline()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, AddBaseAndDerivedPipelines);
        using var scope = provider.CreateScope();
        PipeBaseRequest request = new PipeDerivedRequest();

        var response = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<PipeBaseRequest, string>(request);

        Assert.Equal("base", response);
        Assert.Equal(new[] { "base:before", "base-handler", "base:after" }, log.Snapshot());
    }

    // ...whereas the boxed overload keys off request.GetType(), so the same instance runs the DERIVED
    // pipeline. That difference is the whole point of Send(object) for polymorphic dispatch, and it is
    // the one place where the two entry points are deliberately not interchangeable.
    [Fact]
    public async Task Send_BoxedOverloadWithADerivedRequestInstance_RunsTheRuntimeTypesPipeline()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, AddBaseAndDerivedPipelines);
        using var scope = provider.CreateScope();
        PipeBaseRequest request = new PipeDerivedRequest();

        var response = await scope.ServiceProvider.GetRequiredService<ISender>().Send((object)request);

        Assert.Equal("derived", Assert.IsType<string>(response));
        Assert.Equal(new[] { "derived:before", "derived-handler", "derived:after" }, log.Snapshot());
    }

    // ---------------------------------------------------------------------------------------
    // Concurrency on one scoped mediator
    // ---------------------------------------------------------------------------------------

    // ISender is scoped, and a scope's worth of work may fan out (Parallel/WhenAll over a batch of
    // commands). Each send composes its chain from locals, so two in-flight sends must not see each
    // other's request — hoisting `behaviors` or `next` onto the Mediator would cross the wires.
    // Only order-independent facts are asserted; interleaving is not a contract.
    [Fact]
    public async Task Send_ConcurrentSendsOnTheSameScopedMediator_EachPipelineKeepsItsOwnRequest()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddStringHandler(services);
            services.AddTransient<IPipelineBehavior<PipeStringRequest, string>, PipeHost<object>.FirstBehavior>();
        });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var one = new PipeStringRequest { Value = "one" };
        var two = new PipeStringRequest { Value = "two" };

        var responses = await Task.WhenAll(
            sender.Send<PipeStringRequest, string>(one),
            sender.Send<PipeStringRequest, string>(two));

        Assert.Equal(new[] { "handled:one", "handled:two" }, responses);
        Assert.Equal(2, log.Occurrences("handler"));
        Assert.Equal(2, log.Occurrences("first:before"));
        Assert.Equal(2, log.Occurrences("first:after"));
        var seen = log.Requests();
        Assert.Equal(4, seen.Length);
        Assert.Equal(2, seen.Count(candidate => ReferenceEquals(candidate, one)));
        Assert.Equal(2, seen.Count(candidate => ReferenceEquals(candidate, two)));
    }

    // ---------------------------------------------------------------------------------------
    // IPipelineBehavior<TRequest> — the void pipeline
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task SendVoid_WithThreeBehaviors_RunsThemOutermostFirstAndUnwindsInReverse()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddVoidHandler(services);
            services.AddTransient<IPipelineBehavior<PipeVoidRequest>, PipeHost<object>.VoidFirstBehavior>();
            services.AddTransient<IPipelineBehavior<PipeVoidRequest>, PipeHost<object>.VoidSecondBehavior>();
            services.AddTransient<IPipelineBehavior<PipeVoidRequest>, PipeHost<object>.VoidThirdBehavior>();
        });
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PipeVoidRequest());

        Assert.Equal(
            new[]
            {
                "void-first:before", "void-second:before", "void-third:before", "void-handler",
                "void-third:after", "void-second:after", "void-first:after",
            },
            log.Snapshot());
    }

    [Fact]
    public async Task SendVoid_WhenABehaviorDoesNotCallNext_SkipsTheHandler()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddVoidHandler(services);
            services.AddTransient<IPipelineBehavior<PipeVoidRequest>, PipeHost<object>.VoidFirstBehavior>();
            services.AddTransient<IPipelineBehavior<PipeVoidRequest>, PipeHost<object>.VoidShortCircuitBehavior>();
            services.AddTransient<IPipelineBehavior<PipeVoidRequest>, PipeHost<object>.VoidSecondBehavior>();
        });
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PipeVoidRequest());

        Assert.Equal(new[] { "void-first:before", "void-short-circuit", "void-first:after" }, log.Snapshot());
    }

    [Fact]
    public async Task SendVoid_WithNoBehaviorsRegistered_CallsTheHandlerExactlyOnce()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, AddVoidHandler);
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PipeVoidRequest());

        Assert.Equal(new[] { "void-handler" }, log.Snapshot());
    }

    [Fact]
    public async Task SendVoid_PassesTheCallersCancellationTokenToEveryBehaviorAndTheHandler()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddVoidHandler(services);
            services.AddTransient<IPipelineBehavior<PipeVoidRequest>, PipeHost<object>.VoidFirstBehavior>();
            services.AddTransient<IPipelineBehavior<PipeVoidRequest>, PipeHost<object>.VoidSecondBehavior>();
            services.AddTransient<IPipelineBehavior<PipeVoidRequest>, PipeHost<object>.VoidThirdBehavior>();
        });
        using var scope = provider.CreateScope();
        using var cts = new CancellationTokenSource();

        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PipeVoidRequest(), cts.Token);

        var tokens = log.Tokens();
        Assert.Equal(4, tokens.Length);
        Assert.All(tokens, token => Assert.Equal(cts.Token, token));
    }

    [Fact]
    public async Task SendVoid_WhenABehaviorThrowsBeforeNext_NeverRunsTheHandlerAndPropagates()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddVoidHandler(services);
            services.AddTransient<IPipelineBehavior<PipeVoidRequest>, PipeHost<object>.VoidThrowBeforeNextBehavior>();
            services.AddTransient<IPipelineBehavior<PipeVoidRequest>, PipeHost<object>.VoidFirstBehavior>();
        });
        using var scope = provider.CreateScope();

        var thrown = await Assert.ThrowsAsync<PipeBehaviorException>(() =>
            scope.ServiceProvider.GetRequiredService<ISender>().Send(new PipeVoidRequest()));

        Assert.Equal("void behavior failed before next", thrown.Message);
        Assert.Equal(new[] { "void-throw:before" }, log.Snapshot());
    }

    // SendInternalVoid is a second, separate copy of the composition loop, so the "the command already
    // succeeded but the outbox/commit step failed" path needs pinning on its own side: the handler ran,
    // and the caller still sees the failure rather than a silently completed Task.
    [Fact]
    public async Task SendVoid_WhenABehaviorThrowsAfterNext_StillRanTheHandlerAndPropagates()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddVoidHandler(services);
            services.AddTransient<IPipelineBehavior<PipeVoidRequest>, PipeHost<object>.VoidThrowAfterNextBehavior>();
        });
        using var scope = provider.CreateScope();

        var thrown = await Assert.ThrowsAsync<PipeBehaviorException>(() =>
            scope.ServiceProvider.GetRequiredService<ISender>().Send(new PipeVoidRequest()));

        Assert.Equal("void behavior failed after next", thrown.Message);
        Assert.Equal(new[] { "void-handler", "void-throw:after" }, log.Snapshot());
    }

    // RequestHandlerDelegate (the non-generic one) is a separate declaration from
    // RequestHandlerDelegate<TResponse>, and the void chain builds its own closures. Retrying a command
    // has to re-run the inner chain, not replay a memoized Task.
    [Fact]
    public async Task SendVoid_WhenABehaviorCallsNextTwice_TheInnerPipelineRunsTwice()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddVoidHandler(services);
            services.AddTransient<IPipelineBehavior<PipeVoidRequest>, PipeHost<object>.VoidCallNextTwiceBehavior>();
            services.AddTransient<IPipelineBehavior<PipeVoidRequest>, PipeHost<object>.VoidFirstBehavior>();
        });
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PipeVoidRequest());

        Assert.Equal(
            new[]
            {
                "void-call-next-twice",
                "void-first:before", "void-handler", "void-first:after",
                "void-first:before", "void-handler", "void-first:after",
            },
            log.Snapshot());
    }

    // The void handler here throws ASYNCHRONOUSLY (after an await), the other pipeline's throws
    // synchronously — between them both shapes are covered.
    [Fact]
    public async Task SendVoid_WhenTheHandlerThrows_TheEnclosingBehaviorSeesTheSameExceptionInstance()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            services.AddTransient<IRequestHandler<PipeVoidRequest>, PipeHost<object>.ThrowingVoidHandler>();
            services.AddTransient<IPipelineBehavior<PipeVoidRequest>, PipeHost<object>.VoidCatchAndRethrowBehavior>();
        });
        using var scope = provider.CreateScope();

        var thrown = await Assert.ThrowsAsync<PipeHandlerException>(() =>
            scope.ServiceProvider.GetRequiredService<ISender>().Send(new PipeVoidRequest()));

        Assert.Equal("void handler blew up", thrown.Message);
        Assert.Equal(1, log.Occurrences("void-caught:PipeHandlerException"));
        Assert.Same(thrown, Assert.Single(log.Instances()));
    }

    // Not-throwing IS the contract here: the behavior absorbed the failure, so the caller's await
    // has to complete normally.
    [Fact]
    public async Task SendVoid_WhenABehaviorSwallowsTheHandlerException_CompletesWithoutThrowing()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            services.AddTransient<IRequestHandler<PipeVoidRequest>, PipeHost<object>.ThrowingVoidHandler>();
            services.AddTransient<IPipelineBehavior<PipeVoidRequest>, PipeHost<object>.VoidSwallowBehavior>();
        });
        using var scope = provider.CreateScope();

        var exception = await Record.ExceptionAsync(() =>
            scope.ServiceProvider.GetRequiredService<ISender>().Send(new PipeVoidRequest()));

        Assert.Null(exception);
        Assert.Equal(new[] { "void-handler", "void-swallowed" }, log.Snapshot());
    }

    [Fact]
    public async Task SendVoid_ResolvesADistinctBehaviorInstanceForEachSend()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddVoidHandler(services);
            services.AddTransient<IPipelineBehavior<PipeVoidRequest>, PipeHost<object>.VoidInstanceCapturingBehavior>();
        });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await sender.Send(new PipeVoidRequest());
        await sender.Send(new PipeVoidRequest());

        var instances = log.Instances();
        Assert.Equal(2, instances.Length);
        Assert.NotSame(instances[0], instances[1]);
    }

    [Fact]
    public async Task SendVoid_BehaviorResolvingAScopedDependency_SharesItBetweenSendsInTheSameScope()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddVoidHandler(services);
            services.AddScoped<PipeScopedMarker>();
            services.AddTransient<IPipelineBehavior<PipeVoidRequest>, PipeHost<object>.VoidScopedCapturingBehavior>();
        });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await sender.Send(new PipeVoidRequest());
        await sender.Send(new PipeVoidRequest());

        var markers = log.Instances();
        Assert.Equal(2, markers.Length);
        Assert.Same(markers[0], markers[1]);
    }

    [Fact]
    public async Task SendVoid_WithABehaviorRegisteredForAnotherRequestType_DoesNotRunThatBehavior()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddVoidHandler(services);
            services.AddTransient<IRequestHandler<PipeOtherVoidRequest>, PipeHost<object>.OtherVoidHandler>();
            services.AddTransient<IPipelineBehavior<PipeVoidRequest>, PipeHost<object>.VoidFirstBehavior>();
            services.AddTransient<IPipelineBehavior<PipeOtherVoidRequest>, PipeHost<object>.OtherVoidBehavior>();
        });
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PipeVoidRequest());

        Assert.Equal(new[] { "void-first:before", "void-handler", "void-first:after" }, log.Snapshot());
    }

    [Fact]
    public async Task SendVoid_WithAnOpenGenericBehavior_RunsItForEveryVoidRequestType()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddVoidHandler(services);
            services.AddTransient<IRequestHandler<PipeOtherVoidRequest>, PipeHost<object>.OtherVoidHandler>();
            services.AddTransient(typeof(IPipelineBehavior<>), typeof(PipeOpenVoidBehavior<>));
        });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await sender.Send(new PipeVoidRequest());
        await sender.Send(new PipeOtherVoidRequest(3));

        Assert.Equal(
            new[]
            {
                "openvoid:PipeVoidRequest:before", "void-handler", "openvoid:PipeVoidRequest:after",
                "openvoid:PipeOtherVoidRequest:before", "other-void-handler", "openvoid:PipeOtherVoidRequest:after",
            },
            log.Snapshot());
    }

    [Fact]
    public async Task SendVoid_OpenGenericBehaviorRegisteredFirst_WrapsTheClosedBehavior()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddVoidHandler(services);
            services.AddTransient(typeof(IPipelineBehavior<>), typeof(PipeOpenVoidBehavior<>));
            services.AddTransient<IPipelineBehavior<PipeVoidRequest>, PipeHost<object>.VoidFirstBehavior>();
        });
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PipeVoidRequest());

        Assert.Equal(
            new[]
            {
                "openvoid:PipeVoidRequest:before", "void-first:before", "void-handler",
                "void-first:after", "openvoid:PipeVoidRequest:after",
            },
            log.Snapshot());
    }

    [Fact]
    public async Task SendVoid_TypedAndBoxedOverloads_RunTheIdenticalBehaviorSequence()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddVoidHandler(services);
            services.AddTransient<IPipelineBehavior<PipeVoidRequest>, PipeHost<object>.VoidFirstBehavior>();
            services.AddTransient<IPipelineBehavior<PipeVoidRequest>, PipeHost<object>.VoidSecondBehavior>();
        });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var expected = new[] { "void-first:before", "void-second:before", "void-handler", "void-second:after", "void-first:after" };

        await sender.Send(new PipeVoidRequest());
        var typedSequence = log.Snapshot();
        log.Clear();

        var boxed = await sender.Send((object)new PipeVoidRequest());
        var boxedSequence = log.Snapshot();

        Assert.Equal(expected, typedSequence);
        Assert.Equal(expected, boxedSequence);
        // A void request has no response: the boxed overload reports that as null.
        Assert.Null(boxed);
    }

    [Fact]
    public async Task SendVoid_InterfaceTypedRequest_StillRunsTheConcreteRequestsBehaviors()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, services =>
        {
            AddVoidHandler(services);
            services.AddTransient<IPipelineBehavior<PipeVoidRequest>, PipeHost<object>.VoidFirstBehavior>();
        });
        using var scope = provider.CreateScope();
        IRequest command = new PipeVoidRequest();

        await scope.ServiceProvider.GetRequiredService<ISender>().Send<IRequest>(command);

        Assert.Equal(new[] { "void-first:before", "void-handler", "void-first:after" }, log.Snapshot());
    }

    // ---------------------------------------------------------------------------------------
    // The two pipelines are independent
    // ---------------------------------------------------------------------------------------

    // A type implementing both IRequest and IRequest<T> has two separate pipelines. The void
    // overload must not pull in IPipelineBehavior<T, TResponse> registrations (and vice versa),
    // or a behavior would run twice — or once with the wrong contract — for the same request.
    [Fact]
    public async Task Send_DualRequestThroughTheVoidOverload_RunsOnlyTheVoidPipeline()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, AddDualHandlersAndOpenBehaviors);
        using var scope = provider.CreateScope();

        // Explicit type argument: PipeDualRequest satisfies both Send overloads, and this is the void one.
        await scope.ServiceProvider.GetRequiredService<ISender>().Send<PipeDualRequest>(new PipeDualRequest());

        Assert.Equal(
            new[] { "openvoid:PipeDualRequest:before", "dual-void-handler", "openvoid:PipeDualRequest:after" },
            log.Snapshot());
    }

    [Fact]
    public async Task Send_DualRequestThroughTheResponseOverload_RunsOnlyTheResponsePipeline()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, AddDualHandlersAndOpenBehaviors);
        using var scope = provider.CreateScope();

        var response = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<PipeDualRequest, string>(new PipeDualRequest());

        Assert.Equal("dual", response);
        Assert.Equal(
            new[] { "open:PipeDualRequest:before", "dual-response-handler", "open:PipeDualRequest:after" },
            log.Snapshot());
    }

    // The boxed overload has no type argument to disambiguate with, so CreateWrapper decides: it looks
    // for IRequest<TResponse> FIRST and only falls back to the void wrapper. A dual request therefore
    // runs the RESPONSE pipeline when sent as object — which is what an endpoint returning Send((object)request) relies on.
    [Fact]
    public async Task Send_BoxedDualRequest_UsesTheResponsePipelineNotTheVoidOne()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, AddDualHandlersAndOpenBehaviors);
        using var scope = provider.CreateScope();

        var response = await scope.ServiceProvider.GetRequiredService<ISender>().Send((object)new PipeDualRequest());

        Assert.Equal("dual", Assert.IsType<string>(response));
        Assert.Equal(
            new[] { "open:PipeDualRequest:before", "dual-response-handler", "open:PipeDualRequest:after" },
            log.Snapshot());
    }

    // The sharp edge worth knowing about: `IRequest command = dualRequest;` — a command dispatched out
    // of a List<IRequest> — goes through the void overload, which re-dispatches interfaces by runtime
    // type through Send(object)... and lands in the RESPONSE pipeline, not the void one. The void
    // handler and the void behaviors are skipped entirely even though the call looks like a command.
    [Fact]
    public async Task SendVoid_InterfaceTypedDualRequest_FallsThroughToTheResponsePipeline()
    {
        var log = new PipeCallLog();
        using var provider = BuildProvider(log, AddDualHandlersAndOpenBehaviors);
        using var scope = provider.CreateScope();
        IRequest command = new PipeDualRequest();

        // Explicit type argument so this binds to the void overload, not to Send(object).
        await scope.ServiceProvider.GetRequiredService<ISender>().Send<IRequest>(command);

        Assert.Equal(
            new[] { "open:PipeDualRequest:before", "dual-response-handler", "open:PipeDualRequest:after" },
            log.Snapshot());
        Assert.Equal(0, log.Occurrences("dual-void-handler"));
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A container with the mediator and nothing else scanned — no assembly is passed to
    /// AddMediator, so only the handlers and behaviors <paramref name="configure"/> registers exist.
    /// </summary>
    private static ServiceProvider BuildProvider(PipeCallLog log, Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddMediator();
        configure(services);

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static void AddStringHandler(IServiceCollection services)
        => services.AddTransient<IRequestHandler<PipeStringRequest, string>, PipeHost<object>.StringHandler>();

    private static void AddVoidHandler(IServiceCollection services)
        => services.AddTransient<IRequestHandler<PipeVoidRequest>, PipeHost<object>.VoidHandler>();

    private static void AddDualHandlersAndOpenBehaviors(IServiceCollection services)
    {
        services.AddTransient<IRequestHandler<PipeDualRequest>, PipeHost<object>.DualVoidHandler>();
        services.AddTransient<IRequestHandler<PipeDualRequest, string>, PipeHost<object>.DualResponseHandler>();
        services.AddTransient(typeof(IPipelineBehavior<>), typeof(PipeOpenVoidBehavior<>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(PipeOpenBehavior<,>));
    }

    /// <summary>A handler and a behavior for the base request, and another pair for the derived one.</summary>
    private static void AddBaseAndDerivedPipelines(IServiceCollection services)
    {
        services.AddTransient<IRequestHandler<PipeBaseRequest, string>, PipeHost<object>.BaseHandler>();
        services.AddTransient<IRequestHandler<PipeDerivedRequest, string>, PipeHost<object>.DerivedHandler>();
        services.AddTransient<IPipelineBehavior<PipeBaseRequest, string>, PipeHost<object>.BaseBehavior>();
        services.AddTransient<IPipelineBehavior<PipeDerivedRequest, string>, PipeHost<object>.DerivedBehavior>();
    }
}

// -------------------------------------------------------------------------------------------
// Requests. Declared at namespace scope on purpose: nothing registers a handler for them unless a
// test does, so the assembly scans other test files run stay unaffected.
// -------------------------------------------------------------------------------------------

public sealed class PipeStringRequest : IRequest<string>
{
    public string Value { get; set; } = string.Empty;
}

public sealed class PipeVoidRequest : IRequest
{
}

public sealed record PipeOtherRequest(int Id) : IRequest<string>;

public sealed record PipeOtherVoidRequest(int Id) : IRequest;

/// <summary>Implements both request contracts, so it has a void pipeline AND a response pipeline.</summary>
public sealed record PipeDualRequest : IRequest, IRequest<string>;

/// <summary>A value-type response: "no value" and <c>default</c> are distinguishable here.</summary>
public sealed record PipeCountRequest : IRequest<int>;

/// <summary>Base of a request hierarchy — deliberately not sealed.</summary>
public class PipeBaseRequest : IRequest<string>
{
}

/// <summary>Inherits <see cref="IRequest{TResponse}"/> from its base, so it has a pipeline of its own.</summary>
public sealed class PipeDerivedRequest : PipeBaseRequest
{
}

/// <summary>Records what the pipeline did, in order. Every test owns its own instance.</summary>
public sealed class PipeCallLog
{
    private readonly object gate = new();
    private readonly List<string> entries = [];
    private readonly List<CancellationToken> tokens = [];
    private readonly List<object> instances = [];
    private readonly List<object> requests = [];

    public void Add(string entry)
    {
        lock (gate)
            entries.Add(entry);
    }

    public void AddToken(CancellationToken token)
    {
        lock (gate)
            tokens.Add(token);
    }

    public void AddInstance(object instance)
    {
        lock (gate)
            instances.Add(instance);
    }

    public void AddRequest(object request)
    {
        lock (gate)
            requests.Add(request);
    }

    public string[] Snapshot()
    {
        lock (gate)
            return entries.ToArray();
    }

    public CancellationToken[] Tokens()
    {
        lock (gate)
            return tokens.ToArray();
    }

    public object[] Instances()
    {
        lock (gate)
            return instances.ToArray();
    }

    public object[] Requests()
    {
        lock (gate)
            return requests.ToArray();
    }

    public int Occurrences(string entry) => Snapshot().Count(candidate => candidate == entry);

    public void Clear()
    {
        lock (gate)
            entries.Clear();
    }
}

/// <summary>A scoped dependency a behavior can take, identified by reference.</summary>
public sealed class PipeScopedMarker
{
    public Guid Id { get; } = Guid.NewGuid();
}

/// <summary>Flips what a behavior factory returns between two sends.</summary>
public sealed class PipeBehaviorToggle
{
    public bool Enabled { get; set; }
}

public sealed class PipeBehaviorException(string message) : Exception(message)
{
}

public sealed class PipeHandlerException(string message) : Exception(message)
{
}

/// <summary>
/// Open generic response behavior, registered the way <c>AddMediatorSentry</c> registers its own
/// (<c>typeof(IPipelineBehavior&lt;,&gt;)</c>). Logs the request type so one registration can be
/// told apart across request types.
/// </summary>
public sealed class PipeOpenBehavior<TRequest, TResponse>(PipeCallLog log) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        log.Add($"open:{typeof(TRequest).Name}:before");
        var response = await next();
        log.Add($"open:{typeof(TRequest).Name}:after");

        return response;
    }
}

/// <summary>Open generic void behavior, the <c>IPipelineBehavior&lt;&gt;</c> half of the same pattern.</summary>
public sealed class PipeOpenVoidBehavior<TRequest>(PipeCallLog log) : IPipelineBehavior<TRequest>
    where TRequest : IRequest
{
    public async Task Handle(TRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken)
    {
        log.Add($"openvoid:{typeof(TRequest).Name}:before");
        await next();
        log.Add($"openvoid:{typeof(TRequest).Name}:after");
    }
}

/// <summary>
/// Handlers and behaviors with constructor dependencies, nested in an open generic so
/// <c>AddMediatorHandlers</c>'s assembly scan skips them (they carry <c>TMarker</c>, so
/// <c>ContainsGenericParameters</c> is true). Tests register the closed <c>PipeHost&lt;object&gt;</c>
/// versions by hand.
/// </summary>
public static class PipeHost<TMarker>
{
    // --- response pipeline -------------------------------------------------------------------

    public sealed class StringHandler(PipeCallLog log) : IRequestHandler<PipeStringRequest, string>
    {
        public Task<string> Handle(PipeStringRequest request, CancellationToken cancellationToken)
        {
            log.Add("handler");
            log.AddToken(cancellationToken);
            log.AddRequest(request);
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult($"handled:{request.Value}");
        }
    }

    /// <summary>Throws synchronously, before ever returning a Task.</summary>
    public sealed class ThrowingStringHandler(PipeCallLog log) : IRequestHandler<PipeStringRequest, string>
    {
        public Task<string> Handle(PipeStringRequest request, CancellationToken cancellationToken)
        {
            log.Add("handler");

            throw new PipeHandlerException("handler blew up");
        }
    }

    public sealed class FirstBehavior(PipeCallLog log) : IPipelineBehavior<PipeStringRequest, string>
    {
        public async Task<string> Handle(PipeStringRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            // Yield first: ordering must hold for genuinely asynchronous behaviors, not only for
            // ones that happen to complete synchronously.
            await Task.Yield();
            log.Add("first:before");
            log.AddToken(cancellationToken);
            log.AddRequest(request);
            var response = await next();
            log.Add("first:after");

            return response;
        }
    }

    public sealed class SecondBehavior(PipeCallLog log) : IPipelineBehavior<PipeStringRequest, string>
    {
        public async Task<string> Handle(PipeStringRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            await Task.Yield();
            log.Add("second:before");
            log.AddToken(cancellationToken);
            log.AddRequest(request);
            var response = await next();
            log.Add("second:after");

            return response;
        }
    }

    public sealed class ThirdBehavior(PipeCallLog log) : IPipelineBehavior<PipeStringRequest, string>
    {
        public async Task<string> Handle(PipeStringRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            await Task.Yield();
            log.Add("third:before");
            log.AddToken(cancellationToken);
            log.AddRequest(request);
            var response = await next();
            log.Add("third:after");

            return response;
        }
    }

    public sealed class PassThroughBehavior : IPipelineBehavior<PipeStringRequest, string>
    {
        public Task<string> Handle(PipeStringRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
            => next();
    }

    /// <summary>Answers without calling next(), like a cache hit.</summary>
    public sealed class ShortCircuitBehavior(PipeCallLog log) : IPipelineBehavior<PipeStringRequest, string>
    {
        public Task<string> Handle(PipeStringRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            log.Add("short-circuit");

            return Task.FromResult("short-circuited");
        }
    }

    public sealed class TransformBehavior(PipeCallLog log) : IPipelineBehavior<PipeStringRequest, string>
    {
        public async Task<string> Handle(PipeStringRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            log.Add("transform");

            return await next() + "+transformed";
        }
    }

    public sealed class UpperCaseBehavior(PipeCallLog log) : IPipelineBehavior<PipeStringRequest, string>
    {
        public async Task<string> Handle(PipeStringRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            log.Add("upper");

            return (await next()).ToUpperInvariant();
        }
    }

    public sealed class ThrowBeforeNextBehavior(PipeCallLog log) : IPipelineBehavior<PipeStringRequest, string>
    {
        public Task<string> Handle(PipeStringRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            log.Add("throw:before");

            throw new PipeBehaviorException("behavior failed before next");
        }
    }

    public sealed class ThrowAfterNextBehavior(PipeCallLog log) : IPipelineBehavior<PipeStringRequest, string>
    {
        public async Task<string> Handle(PipeStringRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            await next();
            log.Add("throw:after");

            throw new PipeBehaviorException("behavior failed after next");
        }
    }

    public sealed class CatchAndRethrowBehavior(PipeCallLog log) : IPipelineBehavior<PipeStringRequest, string>
    {
        public async Task<string> Handle(PipeStringRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            try
            {
                return await next();
            }
            catch (Exception exception)
            {
                log.Add($"caught:{exception.GetType().Name}");
                log.AddInstance(exception);

                throw;
            }
        }
    }

    public sealed class SwallowBehavior(PipeCallLog log) : IPipelineBehavior<PipeStringRequest, string>
    {
        public async Task<string> Handle(PipeStringRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            try
            {
                return await next();
            }
            catch (PipeHandlerException)
            {
                log.Add("swallowed");

                return "fallback";
            }
        }
    }

    public sealed class InstanceCapturingBehavior(PipeCallLog log) : IPipelineBehavior<PipeStringRequest, string>
    {
        public Task<string> Handle(PipeStringRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            log.AddInstance(this);

            return next();
        }
    }

    public sealed class ScopedCapturingBehavior(PipeCallLog log, PipeScopedMarker marker) : IPipelineBehavior<PipeStringRequest, string>
    {
        public Task<string> Handle(PipeStringRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            log.AddInstance(marker);

            return next();
        }
    }

    public sealed class DisposableBehavior(PipeCallLog log) : IPipelineBehavior<PipeStringRequest, string>, IDisposable
    {
        public Task<string> Handle(PipeStringRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
            => next();

        public void Dispose() => log.Add("dispose");
    }

    public sealed class CallNextTwiceBehavior(PipeCallLog log) : IPipelineBehavior<PipeStringRequest, string>
    {
        public async Task<string> Handle(PipeStringRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            log.Add("call-next-twice");
            await next();

            return await next();
        }
    }

    public sealed class EnrichingBehavior(PipeCallLog log) : IPipelineBehavior<PipeStringRequest, string>
    {
        public Task<string> Handle(PipeStringRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            log.Add("enrich");
            request.Value = "enriched";

            return next();
        }
    }

    /// <summary>Logs itself the way <see cref="InstanceCapturingBehavior"/> does, so retries can be told apart.</summary>
    public sealed class InstanceCapturingStringHandler(PipeCallLog log) : IRequestHandler<PipeStringRequest, string>
    {
        public Task<string> Handle(PipeStringRequest request, CancellationToken cancellationToken)
        {
            log.Add("handler");
            log.AddInstance(this);

            return Task.FromResult($"handled:{request.Value}");
        }
    }

    /// <summary>Answers null — a legitimate "not found" response, not a failure.</summary>
    public sealed class NullStringHandler(PipeCallLog log) : IRequestHandler<PipeStringRequest, string>
    {
        public Task<string> Handle(PipeStringRequest request, CancellationToken cancellationToken)
        {
            log.Add("handler");

            return Task.FromResult<string>(null!);
        }
    }

    /// <summary>Records whether the response it received was null, then passes it straight through.</summary>
    public sealed class NullObservingBehavior(PipeCallLog log) : IPipelineBehavior<PipeStringRequest, string>
    {
        public async Task<string> Handle(PipeStringRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            var response = await next();
            log.Add(response is null ? "saw:null" : $"saw:{response}");

            // The point of this behavior is that a handler CAN hand back null for a reference-typed
            // response, and the pipeline passes it through untouched rather than substituting anything.
            return response!;
        }
    }

    /// <summary>
    /// Registered against the interface <c>IRequest&lt;string&gt;</c> rather than a concrete request, which
    /// the mediator's runtime-type re-dispatch never looks up.
    /// </summary>
    public sealed class InterfaceRegisteredBehavior(PipeCallLog log) : IPipelineBehavior<IRequest<string>, string>
    {
        public async Task<string> Handle(IRequest<string> request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            log.Add("interface:before");
            var response = await next();
            log.Add("interface:after");

            return response;
        }
    }

    // --- a value-type response ------------------------------------------------------------------

    public sealed class CountHandler(PipeCallLog log) : IRequestHandler<PipeCountRequest, int>
    {
        public Task<int> Handle(PipeCountRequest request, CancellationToken cancellationToken)
        {
            log.Add("count-handler");

            return Task.FromResult(10);
        }
    }

    public sealed class IncrementBehavior(PipeCallLog log) : IPipelineBehavior<PipeCountRequest, int>
    {
        public async Task<int> Handle(PipeCountRequest request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
        {
            log.Add("increment");

            return await next() + 1;
        }
    }

    // --- a request hierarchy ---------------------------------------------------------------------

    public sealed class BaseHandler(PipeCallLog log) : IRequestHandler<PipeBaseRequest, string>
    {
        public Task<string> Handle(PipeBaseRequest request, CancellationToken cancellationToken)
        {
            log.Add("base-handler");

            return Task.FromResult("base");
        }
    }

    public sealed class DerivedHandler(PipeCallLog log) : IRequestHandler<PipeDerivedRequest, string>
    {
        public Task<string> Handle(PipeDerivedRequest request, CancellationToken cancellationToken)
        {
            log.Add("derived-handler");

            return Task.FromResult("derived");
        }
    }

    public sealed class BaseBehavior(PipeCallLog log) : IPipelineBehavior<PipeBaseRequest, string>
    {
        public async Task<string> Handle(PipeBaseRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            log.Add("base:before");
            var response = await next();
            log.Add("base:after");

            return response;
        }
    }

    public sealed class DerivedBehavior(PipeCallLog log) : IPipelineBehavior<PipeDerivedRequest, string>
    {
        public async Task<string> Handle(PipeDerivedRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            log.Add("derived:before");
            var response = await next();
            log.Add("derived:after");

            return response;
        }
    }

    // --- a different request type -------------------------------------------------------------

    public sealed class OtherHandler(PipeCallLog log) : IRequestHandler<PipeOtherRequest, string>
    {
        public Task<string> Handle(PipeOtherRequest request, CancellationToken cancellationToken)
        {
            log.Add("other-handler");

            return Task.FromResult($"other:{request.Id}");
        }
    }

    public sealed class OtherBehavior(PipeCallLog log) : IPipelineBehavior<PipeOtherRequest, string>
    {
        public async Task<string> Handle(PipeOtherRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            log.Add("other:before");
            var response = await next();
            log.Add("other:after");

            return response;
        }
    }

    // --- void pipeline --------------------------------------------------------------------------

    public sealed class VoidHandler(PipeCallLog log) : IRequestHandler<PipeVoidRequest>
    {
        public Task Handle(PipeVoidRequest request, CancellationToken cancellationToken)
        {
            log.Add("void-handler");
            log.AddToken(cancellationToken);
            log.AddRequest(request);

            return Task.CompletedTask;
        }
    }

    /// <summary>Throws asynchronously, after the first await.</summary>
    public sealed class ThrowingVoidHandler(PipeCallLog log) : IRequestHandler<PipeVoidRequest>
    {
        public async Task Handle(PipeVoidRequest request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            log.Add("void-handler");

            throw new PipeHandlerException("void handler blew up");
        }
    }

    public sealed class VoidFirstBehavior(PipeCallLog log) : IPipelineBehavior<PipeVoidRequest>
    {
        public async Task Handle(PipeVoidRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken)
        {
            await Task.Yield();
            log.Add("void-first:before");
            log.AddToken(cancellationToken);
            log.AddRequest(request);
            await next();
            log.Add("void-first:after");
        }
    }

    public sealed class VoidSecondBehavior(PipeCallLog log) : IPipelineBehavior<PipeVoidRequest>
    {
        public async Task Handle(PipeVoidRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken)
        {
            await Task.Yield();
            log.Add("void-second:before");
            log.AddToken(cancellationToken);
            log.AddRequest(request);
            await next();
            log.Add("void-second:after");
        }
    }

    public sealed class VoidThirdBehavior(PipeCallLog log) : IPipelineBehavior<PipeVoidRequest>
    {
        public async Task Handle(PipeVoidRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken)
        {
            await Task.Yield();
            log.Add("void-third:before");
            log.AddToken(cancellationToken);
            log.AddRequest(request);
            await next();
            log.Add("void-third:after");
        }
    }

    public sealed class VoidShortCircuitBehavior(PipeCallLog log) : IPipelineBehavior<PipeVoidRequest>
    {
        public Task Handle(PipeVoidRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken)
        {
            log.Add("void-short-circuit");

            return Task.CompletedTask;
        }
    }

    public sealed class VoidThrowBeforeNextBehavior(PipeCallLog log) : IPipelineBehavior<PipeVoidRequest>
    {
        public Task Handle(PipeVoidRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken)
        {
            log.Add("void-throw:before");

            throw new PipeBehaviorException("void behavior failed before next");
        }
    }

    public sealed class VoidThrowAfterNextBehavior(PipeCallLog log) : IPipelineBehavior<PipeVoidRequest>
    {
        public async Task Handle(PipeVoidRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken)
        {
            await next();
            log.Add("void-throw:after");

            throw new PipeBehaviorException("void behavior failed after next");
        }
    }

    public sealed class VoidCallNextTwiceBehavior(PipeCallLog log) : IPipelineBehavior<PipeVoidRequest>
    {
        public async Task Handle(PipeVoidRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken)
        {
            log.Add("void-call-next-twice");
            await next();
            await next();
        }
    }

    public sealed class VoidCatchAndRethrowBehavior(PipeCallLog log) : IPipelineBehavior<PipeVoidRequest>
    {
        public async Task Handle(PipeVoidRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken)
        {
            try
            {
                await next();
            }
            catch (Exception exception)
            {
                log.Add($"void-caught:{exception.GetType().Name}");
                log.AddInstance(exception);

                throw;
            }
        }
    }

    public sealed class VoidSwallowBehavior(PipeCallLog log) : IPipelineBehavior<PipeVoidRequest>
    {
        public async Task Handle(PipeVoidRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken)
        {
            try
            {
                await next();
            }
            catch (PipeHandlerException)
            {
                log.Add("void-swallowed");
            }
        }
    }

    public sealed class VoidInstanceCapturingBehavior(PipeCallLog log) : IPipelineBehavior<PipeVoidRequest>
    {
        public Task Handle(PipeVoidRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken)
        {
            log.AddInstance(this);

            return next();
        }
    }

    public sealed class VoidScopedCapturingBehavior(PipeCallLog log, PipeScopedMarker marker) : IPipelineBehavior<PipeVoidRequest>
    {
        public Task Handle(PipeVoidRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken)
        {
            log.AddInstance(marker);

            return next();
        }
    }

    public sealed class OtherVoidHandler(PipeCallLog log) : IRequestHandler<PipeOtherVoidRequest>
    {
        public Task Handle(PipeOtherVoidRequest request, CancellationToken cancellationToken)
        {
            log.Add("other-void-handler");

            return Task.CompletedTask;
        }
    }

    public sealed class OtherVoidBehavior(PipeCallLog log) : IPipelineBehavior<PipeOtherVoidRequest>
    {
        public async Task Handle(PipeOtherVoidRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken)
        {
            log.Add("other-void:before");
            await next();
            log.Add("other-void:after");
        }
    }

    // --- the dual request -----------------------------------------------------------------------

    public sealed class DualVoidHandler(PipeCallLog log) : IRequestHandler<PipeDualRequest>
    {
        public Task Handle(PipeDualRequest request, CancellationToken cancellationToken)
        {
            log.Add("dual-void-handler");

            return Task.CompletedTask;
        }
    }

    public sealed class DualResponseHandler(PipeCallLog log) : IRequestHandler<PipeDualRequest, string>
    {
        public Task<string> Handle(PipeDualRequest request, CancellationToken cancellationToken)
        {
            log.Add("dual-response-handler");

            return Task.FromResult("dual");
        }
    }
}
