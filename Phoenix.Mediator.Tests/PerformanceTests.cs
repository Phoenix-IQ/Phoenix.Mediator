using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Tests.Infrastructure;
using Phoenix.Mediator.Web;
using Phoenix.Mediator.Web.Middlewares;
using Phoenix.Mediator.Wrappers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Xunit;
using Xunit.Abstractions;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Phoenix.Mediator.Tests;

// xUnit1031: these tests block on the sends they measure on purpose. Allocation is counted with
// GC.GetAllocatedBytesForCurrentThread, which is per-thread, so awaiting would let a continuation resume
// on a different thread and attribute the allocations to nobody. xunit v2 installs no synchronization
// context in a test, so blocking here cannot deadlock.
// ASP0022: a few tests map the same route twice on purpose — they are measuring the duplicate detector.
#pragma warning disable xUnit1031, ASP0022


/// <summary>
/// The library makes four performance claims about itself in source comments, and every one of them
/// can regress silently — nothing else in the suite would go red:
/// <list type="bullet">
/// <item>Mediator.cs: per request-type wrapper objects, "built once per type ... no per-request
/// reflection (MethodInfo.Invoke), no object[] arg allocation".</item>
/// <item>ISender.cs / AutoResponseMappingExtensions.cs: the typed overloads "avoid the runtime lookup
/// and boxing".</item>
/// <item>ExceptionHandlingMiddleware.cs: configuration is snapshotted once at construction, not
/// re-read on every error.</item>
/// <item>RequestBodyJsonOptionsSetup.cs: the resolver wraps a COPY of the chain, because wrapping the
/// live chain makes the wrapper call itself until the stack overflows.</item>
/// </list>
/// <para>
/// These run on shared CI hardware, so the evidence is allocation counts wherever possible: bytes
/// allocated on the calling thread are a property of the code path, not of how busy the machine is, so
/// they neither flake under load nor need generous slack to stay green. The few assertions that
/// genuinely have to be wall-clock — complexity class cannot be measured any other way — are kept at
/// least five times above the value the code actually produces. Every measurement is written to the
/// test output so a failure is diagnosable from the log alone.
/// </para>
/// </summary>
[Trait("Category", "Performance")]
public sealed class PerformanceTests(ITestOutputHelper output)
{
    private const int WarmupIterations = 200;
    private const int MeasuredIterations = 2_000;

    // --------------------------------------------------------------------------------------------
    // The wrapper cache: "Built once per type, then dispatched via a virtual call".
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// Per-send cost must be flat: the 1,000th send of a type costs what its 200th did. This does not on
    /// its own prove the cache exists — reflection on every send would be expensive but equally flat,
    /// which is what <see cref="Send_ObjectOverload_AllocatesABoundedAmountPerSend"/> and the cold-start
    /// test below are for. What it catches is cost that accumulates: a cache keyed by something other than
    /// the request type, a per-send entry appended to a static collection, a wrapper rebuilt because the
    /// cached one is being evicted. In a long-lived app that is the regression that only shows up in
    /// production, after the process has been serving for hours.
    /// </summary>
    [Fact]
    public void Send_ObjectOverload_PerSendAllocationDoesNotGrowWithTheNumberOfSends()
    {
        using var provider = CreateSenderProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new PerfEchoRequest("steady");
        Action send = () => sender.Send((object)request).GetAwaiter().GetResult();

        Warmup(WarmupIterations, send);
        var early = AllocatedBytesPerOperation(20, send);
        Warmup(1_000, send);
        var late = AllocatedBytesPerOperation(20, send);

        output.WriteLine($"Send(object): {early} B/send after {WarmupIterations} sends, {late} B/send after {WarmupIterations + 1_000}.");

        // Expected identical. Both samples are short (20 sends), so one runtime event that happens to land
        // inside the late sample — a tiered-compilation transition, a resized internal cache — can move it.
        // 5x plus 1 KB is far more slack than any of those need and still an order of magnitude below what
        // accumulating per-send state would produce.
        Assert.True(
            late <= (early * 5) + 1_024,
            $"Per-send allocation grew with the number of sends: {early} B early, {late} B after another 1,000 sends. The wrapper cache is not holding.");
    }

    /// <summary>
    /// A steady-state budget. Real cost is a few hundred bytes (the async state machines, the behavior
    /// array, the handler); 8 KB leaves roughly an order of magnitude of headroom and still fails if a
    /// send starts allocating object[] argument arrays or boxing through reflection.
    /// </summary>
    [Fact]
    public void Send_ObjectOverload_AllocatesABoundedAmountPerSend()
    {
        using var provider = CreateSenderProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new PerfEchoRequest("bounded");
        Action send = () => sender.Send((object)request).GetAwaiter().GetResult();

        Warmup(WarmupIterations, send);
        var perSend = AllocatedBytesPerOperation(MeasuredIterations, send);

        output.WriteLine($"Send(object) steady state: {perSend} B/send over {MeasuredIterations} sends.");

        Assert.True(perSend < 8_192, $"Send(object) allocated {perSend} B/send, over the 8 KB budget.");
    }

    /// <summary>
    /// The per-type setup (building the wrapper by reflection, building the container's call sites) must
    /// be paid on the first send of a type and never again. PerfColdStartRequest is used by this test and
    /// nothing else, so its entry in the process-wide static wrapper cache is created here.
    /// </summary>
    [Fact]
    public void Send_ObjectOverload_PaysPerRequestTypeSetupOnceNotOnEverySend()
    {
        using var provider = CreateSenderProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        // Warm everything that is NOT specific to the cold request type, so the difference measured
        // below is the per-type setup and not one-off generic/JIT machinery.
        var warmupRequest = new PerfEchoRequest("warm");
        Warmup(WarmupIterations, () => sender.Send((object)warmupRequest).GetAwaiter().GetResult());

        var coldRequest = new PerfColdStartRequest();
        Action sendCold = () => sender.Send((object)coldRequest).GetAwaiter().GetResult();

        var before = GC.GetAllocatedBytesForCurrentThread();
        sendCold();
        var firstSend = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        sendCold();
        var secondSend = GC.GetAllocatedBytesForCurrentThread() - before;

        Warmup(WarmupIterations, sendCold);
        var steadyPerSend = AllocatedBytesPerOperation(MeasuredIterations, sendCold);

        // The second send is reported, not asserted on: it is one unaveraged sample, and how much of the
        // remaining first-call machinery (tiered JIT, generic dictionary population) lands on it is the
        // runtime's business rather than this library's.
        output.WriteLine($"First ever send of a request type: {firstSend} B. Second: {secondSend} B. Steady state: {steadyPerSend} B/send.");

        // The first send builds the wrapper by reflection and the container's call sites; that is
        // kilobytes. Requiring only 2x leaves a large margin, while a per-send rebuild would make the
        // two numbers comparable and fail here.
        Assert.True(
            steadyPerSend * 2 < firstSend,
            $"The first send of a request type cost {firstSend} B and steady state costs {steadyPerSend} B/send — per-type setup looks like it is being repeated per send.");
    }

    /// <summary>
    /// The cache is a process-wide static keyed by request type. A cache that handed back the wrong
    /// wrapper would show up as one request type's handler answering for another — and only under
    /// interleaving, which is exactly how a real app uses it.
    /// </summary>
    [Fact]
    public void Send_ObjectOverload_KeepsOneWrapperPerRequestTypeWhenTypesAreInterleaved()
    {
        using var provider = CreateSenderProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        for (var i = 0; i < 200; i++)
        {
            var echoed = sender.Send((object)new PerfEchoRequest($"echo-{i}")).GetAwaiter().GetResult();
            Assert.Equal($"echo-{i}", Assert.IsType<string>(echoed));

            var counted = sender.Send((object)new PerfCounterRequest(i)).GetAwaiter().GetResult();
            Assert.Equal(i, Assert.IsType<SingleResponse<int>>(counted).Result);

            var named = sender.Send((object)new PerfJsonRequest(i, $"name-{i}")).GetAwaiter().GetResult();
            Assert.Equal($"name-{i}", Assert.IsType<string>(named));

            // A void request dispatched through the boxed path reports "no response" as null.
            Assert.Null(sender.Send((object)new PerfVoidRequest()).GetAwaiter().GetResult());
        }
    }

    /// <summary>
    /// The cache outlives every container: it is static, the mediator is scoped. So the thing cached has
    /// to be a stateless dispatcher and nothing else. Caching the resolved handler — an easy
    /// "optimization" to reach for — would let the first container to send a request type decide the
    /// handler for every container after it in the process: in tests, whichever test ran first; in a host
    /// running more than one app, whichever app took traffic first.
    /// </summary>
    [Fact]
    public void Send_ObjectOverload_ResolvesHandlersFromTheSendingContainerNotTheOneThatWarmedTheCache()
    {
        using var primary = CreateSenderProvider();
        using var alternate = CreateSenderProvider(static services =>
            services.AddTransient<IRequestHandler<PerfSharedCacheRequest, string>, PerfHandlerHost<object>.AlternateSharedCacheHandler>());

        using var primaryScope = primary.CreateScope();
        using var alternateScope = alternate.CreateScope();
        var primarySender = primaryScope.ServiceProvider.GetRequiredService<ISender>();
        var alternateSender = alternateScope.ServiceProvider.GetRequiredService<ISender>();

        // The first send in the process creates the cache entry for this request type.
        Assert.Equal("primary", Assert.IsType<string>(primarySender.Send((object)new PerfSharedCacheRequest()).GetAwaiter().GetResult()));
        Assert.Equal("alternate", Assert.IsType<string>(alternateSender.Send((object)new PerfSharedCacheRequest()).GetAwaiter().GetResult()));
        // ...and the second container's send must not have rewritten the entry either.
        Assert.Equal("primary", Assert.IsType<string>(primarySender.Send((object)new PerfSharedCacheRequest()).GetAwaiter().GetResult()));
    }

    /// <summary>
    /// The cold path of a ConcurrentDictionary.GetOrAdd is the one that races: several threads can enter
    /// the factory for the same key, and each of them builds a wrapper by reflection. Whichever instance
    /// wins, every caller has to get a working one — a half-built or discarded wrapper surfaces as a
    /// NullReferenceException or a wrong response on exactly the requests that arrive in the first seconds
    /// after a deploy, which is also when nobody is watching a single request's response body.
    /// </summary>
    [Fact]
    public void Send_ObjectOverload_FromManyThreadsOnAnUnseenRequestType_GivesEveryThreadAWorkingWrapper()
    {
        const int Workers = 8;
        const int SendsPerWorker = 50;

        using var provider = CreateSenderProvider();
        var failures = new ConcurrentQueue<string>();

        // No warmup on purpose: PerfConcurrentColdStartRequest is sent by nothing else in the process, so
        // the workers race to create its one cache entry.
        Parallel.For(0, Workers, new ParallelOptions { MaxDegreeOfParallelism = Workers }, _ =>
        {
            try
            {
                using var scope = provider.CreateScope();
                var sender = scope.ServiceProvider.GetRequiredService<ISender>();

                for (var i = 0; i < SendsPerWorker; i++)
                {
                    var answered = sender.Send((object)new PerfConcurrentColdStartRequest()).GetAwaiter().GetResult();
                    if ((answered as string) != "cold-concurrent")
                        failures.Enqueue($"expected 'cold-concurrent', got '{answered}'");
                }
            }
            catch (Exception exception)
            {
                failures.Enqueue(exception.ToString());
            }
        });

        Assert.True(
            failures.IsEmpty,
            $"{failures.Count} sends on a cold request type failed under contention. First few:{Environment.NewLine}{string.Join(Environment.NewLine, failures.Take(5))}");
    }

    /// <summary>
    /// A type that is not a request has no wrapper to build, and the failure has to be reported the same
    /// way every time. ConcurrentDictionary.GetOrAdd does not remember a factory that threw, and this pins
    /// that: caching the failure (or a null entry) would turn a clear "must implement IRequest" into a
    /// NullReferenceException on the second call — the one an app actually finds in its logs.
    /// </summary>
    [Fact]
    public void Send_ObjectOverload_WithATypeThatIsNotARequest_ReportsTheSameErrorOnEveryCall()
    {
        using var provider = CreateSenderProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var notARequest = new PerfNotARequest();

        var first = Assert.Throws<ArgumentException>(() => { sender.Send(notARequest).GetAwaiter().GetResult(); });
        var second = Assert.Throws<ArgumentException>(() => { sender.Send(notARequest).GetAwaiter().GetResult(); });

        Assert.Contains(nameof(PerfNotARequest), first.Message, StringComparison.Ordinal);
        Assert.Equal(first.Message, second.Message);
    }

    // --------------------------------------------------------------------------------------------
    // The typed overloads: "avoid the runtime lookup and boxing" / "no reflection, no boxing".
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// This is the documented reason the typed overloads exist. The boxed path runs the same pipeline
    /// through two extra async frames (Send and the wrapper's Handle), so it must cost strictly more per
    /// send. Allocation is deterministic — the same code path allocates the same bytes on an idle laptop
    /// and on a saturated CI agent — so no slack is needed here and none is given: if the two ever
    /// converge, the typed overloads have stopped being worth calling and ISender's guidance is wrong.
    /// </summary>
    [Fact]
    public void Send_TypedOverload_AllocatesLessPerSendThanTheObjectOverload()
    {
        using var provider = CreateSenderProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new PerfEchoRequest("typed-vs-boxed");

        // Both paths must produce the same answer; the measurement is only meaningful if they do.
        Assert.Equal("typed-vs-boxed", sender.Send<PerfEchoRequest, string>(request).GetAwaiter().GetResult());
        Assert.Equal("typed-vs-boxed", Assert.IsType<string>(sender.Send((object)request).GetAwaiter().GetResult()));

        Action typed = () => sender.Send<PerfEchoRequest, string>(request).GetAwaiter().GetResult();
        Action boxed = () => sender.Send((object)request).GetAwaiter().GetResult();

        Warmup(WarmupIterations, typed);
        Warmup(WarmupIterations, boxed);
        var typedPerSend = AllocatedBytesPerOperation(MeasuredIterations, typed);
        var boxedPerSend = AllocatedBytesPerOperation(MeasuredIterations, boxed);

        output.WriteLine($"IRequest<TResponse>: typed {typedPerSend} B/send, object overload {boxedPerSend} B/send (difference {boxedPerSend - typedPerSend} B).");

        Assert.True(
            typedPerSend < boxedPerSend,
            $"The typed overload allocated {typedPerSend} B/send and the object overload {boxedPerSend} B/send; the typed overload is supposed to be the cheaper one.");
    }

    /// <summary>
    /// Same claim for the void overload, which dispatches through VoidRequestWrapper.
    /// <para>
    /// Asserted as "no more" rather than "strictly less", deliberately and not out of weakness: the boxed
    /// void path's two extra frames both complete synchronously with a result the runtime is free to hand
    /// back as a cached Task, so whether they cost anything at all is a runtime implementation choice that
    /// differs between versions. The direction that is this library's business — the typed overload must
    /// never become the expensive one — is what gets pinned; the actual gap is reported instead.
    /// </para>
    /// </summary>
    [Fact]
    public void Send_TypedVoidOverload_AllocatesNoMorePerSendThanTheObjectOverload()
    {
        using var provider = CreateSenderProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new PerfVoidRequest();

        Action typed = () => sender.Send<PerfVoidRequest>(request).GetAwaiter().GetResult();
        Action boxed = () => sender.Send((object)request).GetAwaiter().GetResult();

        Warmup(WarmupIterations, typed);
        Warmup(WarmupIterations, boxed);
        var typedPerSend = AllocatedBytesPerOperation(MeasuredIterations, typed);
        var boxedPerSend = AllocatedBytesPerOperation(MeasuredIterations, boxed);

        output.WriteLine($"IRequest (void): typed {typedPerSend} B/send, object overload {boxedPerSend} B/send (difference {boxedPerSend - typedPerSend} B).");

        Assert.True(
            typedPerSend <= boxedPerSend,
            $"The typed void overload allocated {typedPerSend} B/send and the object overload {boxedPerSend} B/send.");
    }

    /// <summary>
    /// The documented exception to "prefer the generic overloads": a variable declared as the interface
    /// (<c>IRequest&lt;T&gt; query = ...</c>, a <c>List&lt;IRequest&gt;</c>, a factory return value) binds
    /// TRequest to the interface, and no handler is registered for an interface. The mediator detects that
    /// and re-dispatches by runtime type. This used to throw "no service for IRequestHandler&lt;IRequest&gt;",
    /// so it is asserted through a behavior: reaching the pipeline, not merely not throwing, is the contract.
    /// </summary>
    [Fact]
    public void Send_RequestDeclaredAsTheInterface_StillReachesTheHandlerPipeline()
    {
        var counter = new PerfCallCounter();
        using var provider = CreateSenderProvider(services =>
        {
            services.AddSingleton(counter);
            services.AddTransient<IPipelineBehavior<PerfVoidRequest>, PerfVoidCountingBehavior>();
        });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        IRequest<string> query = new PerfEchoRequest("interface-typed");
        Assert.Equal("interface-typed", sender.Send<IRequest<string>, string>(query).GetAwaiter().GetResult());

        IRequest command = new PerfVoidRequest();
        sender.Send<IRequest>(command).GetAwaiter().GetResult();

        Assert.Equal(1, counter.Count);
    }

    /// <summary>
    /// The interface-typed call re-enters the boxed path, so it should cost that plus one more async
    /// frame — not a fresh reflection lookup per send. Nothing else measures this path, and it is the one
    /// a codebase that dispatches commands polymorphically uses for every single request it serves.
    /// </summary>
    [Fact]
    public void Send_RequestDeclaredAsTheInterface_CostsNoMorePerSendThanTheObjectOverload()
    {
        using var provider = CreateSenderProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new PerfEchoRequest("interface-cost");
        IRequest<string> asInterface = request;

        Action throughInterface = () => sender.Send<IRequest<string>, string>(asInterface).GetAwaiter().GetResult();
        Action boxed = () => sender.Send((object)request).GetAwaiter().GetResult();

        Warmup(WarmupIterations, throughInterface);
        Warmup(WarmupIterations, boxed);
        var interfacePerSend = AllocatedBytesPerOperation(MeasuredIterations, throughInterface);
        var boxedPerSend = AllocatedBytesPerOperation(MeasuredIterations, boxed);

        output.WriteLine($"IRequest<TResponse> declared as the interface: {interfacePerSend} B/send, object overload {boxedPerSend} B/send.");

        // One extra frame, not a different algorithm. 5x is far above one async frame and far below what a
        // per-send MakeGenericType/Activator.CreateInstance would add.
        Assert.True(
            interfacePerSend <= (boxedPerSend * 5) + 512,
            $"An interface-typed send allocated {interfacePerSend} B against {boxedPerSend} B for the object overload; it is supposed to be the object overload plus one frame.");
    }

    // --------------------------------------------------------------------------------------------
    // Throughput and the pipeline.
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// A smoke test, not a micro-benchmark: 20,000 sends across four request types, each of which has to
    /// return the right answer. The budget allows 1.5 ms per send — hundreds of times the real cost — so
    /// that it survives a saturated shared agent while still catching a per-send container rebuild or a
    /// per-send assembly scan.
    /// </summary>
    [Fact]
    public void Send_TwentyThousandSendsAcrossFourRequestTypes_CompletesWellWithinBudget()
    {
        const int Rounds = 5_000;
        const int TotalSends = Rounds * 4;

        using var provider = CreateSenderProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var echo = new PerfEchoRequest("throughput");
        var counter = new PerfCounterRequest(7);
        var json = new PerfJsonRequest(1, "throughput");
        var empty = new PerfVoidRequest();

        Warmup(WarmupIterations, () => sender.Send<PerfEchoRequest, string>(echo).GetAwaiter().GetResult());

        var wrongResults = 0;
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < Rounds; i++)
        {
            if (sender.Send<PerfEchoRequest, string>(echo).GetAwaiter().GetResult() != "throughput")
                wrongResults++;

            if (sender.Send<PerfCounterRequest, SingleResponse<int>>(counter).GetAwaiter().GetResult().Result != 7)
                wrongResults++;

            if (sender.Send((object)json).GetAwaiter().GetResult() is not "throughput")
                wrongResults++;

            sender.Send<PerfVoidRequest>(empty).GetAwaiter().GetResult();
        }
        stopwatch.Stop();

        var sendsPerSecond = TotalSends / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        output.WriteLine($"{TotalSends} sends in {stopwatch.Elapsed.TotalMilliseconds:F0} ms ({sendsPerSecond:F0} sends/second).");

        Assert.Equal(0, wrongResults);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"{TotalSends} sends took {stopwatch.Elapsed.TotalSeconds:F1} s, over the 30 s budget.");
    }

    /// <summary>
    /// Behaviors are composed into a delegate chain once per send, so cost has to grow by a fixed amount
    /// per behavior: a closure, a delegate, an instance and an async frame, which is low hundreds of bytes.
    /// A pipeline rebuilt per behavior (or a chain re-walked per behavior) grows faster than that, and
    /// eight behaviors is not exotic — validation, Sentry, logging, transactions, caching, tenancy,
    /// auditing and idempotency is an ordinary enterprise stack.
    /// </summary>
    [Fact]
    public void Send_WithADeeperBehaviorPipeline_CostsABoundedAmountPerExtraBehavior()
    {
        const int DeepPipeline = 8;

        var oneBehavior = MeasurePerSendWithBehaviors(1);
        var deepPipeline = MeasurePerSendWithBehaviors(DeepPipeline);
        var perExtraBehavior = (deepPipeline - oneBehavior) / (DeepPipeline - 1);

        output.WriteLine($"Pipeline: 1 behavior {oneBehavior} B/send, {DeepPipeline} behaviors {deepPipeline} B/send, {perExtraBehavior} B per extra behavior.");

        Assert.True(
            perExtraBehavior <= 2_048,
            $"Each extra behavior added {perExtraBehavior} B/send, over the 2 KB per-behavior budget (1 behavior: {oneBehavior} B, {DeepPipeline} behaviors: {deepPipeline} B).");
        Assert.True(
            deepPipeline < 32_768,
            $"A {DeepPipeline}-behavior pipeline allocated {deepPipeline} B/send, over the 32 KB budget.");
    }

    /// <summary>
    /// The cheapest possible guard against the pipeline being built or walked more than once per send:
    /// a behavior that ran twice would double every behavior's work in every request.
    /// </summary>
    [Fact]
    public void Send_WithBehaviors_RunsEachBehaviorExactlyOncePerSend()
    {
        const int Sends = 100;
        var counter = new PerfCallCounter();
        using var provider = CreateSenderProvider(services =>
        {
            services.AddSingleton(counter);
            services.AddTransient<IPipelineBehavior<PerfEchoRequest, string>, PerfCountingBehavior>();
            services.AddTransient<IPipelineBehavior<PerfEchoRequest, string>, PerfCountingBehavior>();
        });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new PerfEchoRequest("counted");

        for (var i = 0; i < Sends; i++)
            Assert.Equal("counted", sender.Send<PerfEchoRequest, string>(request).GetAwaiter().GetResult());

        Assert.Equal(Sends * 2, counter.Count);
    }

    /// <summary>
    /// A send resolves the behaviors registered for its own request type, so the cost of a pipeline is
    /// paid by the requests it is attached to and by nobody else. Resolving everything and filtering
    /// afterwards would make every request in the app pay for the most heavily decorated one — and would
    /// also run behaviors against requests their author never intended them for.
    /// </summary>
    [Fact]
    public void Send_WithBehaviors_DoesNotRunBehaviorsRegisteredForAnotherRequestType()
    {
        var counter = new PerfCallCounter();
        using var provider = CreateSenderProvider(services =>
        {
            services.AddSingleton(counter);
            services.AddTransient<IPipelineBehavior<PerfEchoRequest, string>, PerfCountingBehavior>();
        });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(i, sender.Send<PerfCounterRequest, SingleResponse<int>>(new PerfCounterRequest(i)).GetAwaiter().GetResult().Result);
            sender.Send<PerfVoidRequest>(new PerfVoidRequest()).GetAwaiter().GetResult();
        }

        Assert.Equal(0, counter.Count);

        // ...and the behavior really is registered, so the zero above is not simply a missing registration.
        Assert.Equal("decorated", sender.Send<PerfEchoRequest, string>(new PerfEchoRequest("decorated")).GetAwaiter().GetResult());
        Assert.Equal(1, counter.Count);
    }

    /// <summary>
    /// The wrapper cache is a static ConcurrentDictionary shared by the whole process, and the mediator
    /// is scoped, so every request in a real app hits it from a different thread at once. A torn read
    /// would surface as a caller receiving another caller's response.
    /// </summary>
    [Fact]
    public void Send_FromManyThreadsAtOnce_ReturnsEachCallerItsOwnResult()
    {
        const int Workers = 8;
        const int SendsPerWorker = 400;

        using var provider = CreateSenderProvider();
        var failures = new ConcurrentQueue<string>();

        var stopwatch = Stopwatch.StartNew();
        Parallel.For(0, Workers, new ParallelOptions { MaxDegreeOfParallelism = Workers }, worker =>
        {
            try
            {
                // One scope per worker, the way a request pipeline gives each request its own.
                using var scope = provider.CreateScope();
                var sender = scope.ServiceProvider.GetRequiredService<ISender>();

                for (var i = 0; i < SendsPerWorker; i++)
                {
                    var value = $"w{worker}-{i}";

                    var echoed = sender.Send((object)new PerfEchoRequest(value)).GetAwaiter().GetResult();
                    if ((echoed as string) != value)
                        failures.Enqueue($"echo expected '{value}', got '{echoed}'");

                    var counted = sender.Send<PerfCounterRequest, SingleResponse<int>>(new PerfCounterRequest(i)).GetAwaiter().GetResult();
                    if (counted.Result != i)
                        failures.Enqueue($"counter expected {i}, got {counted.Result}");

                    var named = sender.Send((object)new PerfJsonRequest(i, value)).GetAwaiter().GetResult();
                    if ((named as string) != value)
                        failures.Enqueue($"name expected '{value}', got '{named}'");

                    sender.Send<PerfVoidRequest>(new PerfVoidRequest()).GetAwaiter().GetResult();
                }
            }
            catch (Exception exception)
            {
                failures.Enqueue(exception.ToString());
            }
        });
        stopwatch.Stop();

        output.WriteLine($"{Workers} workers x {SendsPerWorker} iterations x 4 request types in {stopwatch.Elapsed.TotalMilliseconds:F0} ms.");

        Assert.True(
            failures.IsEmpty,
            $"{failures.Count} concurrent sends failed or returned another caller's result. First few:{Environment.NewLine}{string.Join(Environment.NewLine, failures.Take(5))}");
    }

    // --------------------------------------------------------------------------------------------
    // ExceptionHandlingMiddleware: "Snapshot configuration once at construction".
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// The middleware instance lives for the life of the app, so the error-message table is built once.
    /// Rebuilding it per error would make the per-exception cost proportional to the size of the
    /// ErrorMessages section — measured here with 125x as many messages on one of the two instances.
    /// </summary>
    [Fact]
    public void ExceptionHandlingMiddleware_PerExceptionAllocationDoesNotGrowWithTheConfiguredErrorMessageCount()
    {
        var fewMessages = CreateMiddlewareWithErrorMessages(4);
        var manyMessages = CreateMiddlewareWithErrorMessages(500);

        // Both instances must actually be handling the exception; a middleware that rethrew would
        // otherwise look wonderfully cheap.
        Assert.Equal(StatusCodes.Status500InternalServerError, InvokeHandlingOneException(fewMessages));
        Assert.Equal(StatusCodes.Status500InternalServerError, InvokeHandlingOneException(manyMessages));

        Action handleWithFew = () => InvokeHandlingOneException(fewMessages);
        Action handleWithMany = () => InvokeHandlingOneException(manyMessages);

        Warmup(WarmupIterations, handleWithFew);
        Warmup(WarmupIterations, handleWithMany);
        var withFew = AllocatedBytesPerOperation(500, handleWithFew);
        var withMany = AllocatedBytesPerOperation(500, handleWithMany);

        output.WriteLine($"Exception handling: {withFew} B/exception with 4 configured messages, {withMany} B/exception with 500.");

        // Snapshotting makes the two indistinguishable. 5x plus 2 KB is enormous headroom next to the
        // blow-up that re-reading a 125x larger configuration section on every error would produce.
        Assert.True(
            withMany <= (withFew * 5) + 2_048,
            $"Handling an exception cost {withFew} B with 4 configured messages and {withMany} B with 500 — configuration looks like it is being re-read per error.");
    }

    /// <summary>
    /// An absolute per-exception budget. Real cost is the HttpContext, the serialized body and the log
    /// record — single-digit kilobytes; 64 KB is a deliberately loose ceiling that still catches the
    /// configuration dictionary being rebuilt for every failed request.
    /// </summary>
    [Fact]
    public void ExceptionHandlingMiddleware_AllocatesABoundedAmountPerHandledException()
    {
        var middleware = CreateMiddlewareWithErrorMessages(500);
        Assert.Equal(StatusCodes.Status500InternalServerError, InvokeHandlingOneException(middleware));

        Action handle = () => InvokeHandlingOneException(middleware);
        Warmup(WarmupIterations, handle);
        var perException = AllocatedBytesPerOperation(500, handle);

        output.WriteLine($"Exception handling steady state: {perException} B/exception (500 configured messages).");

        Assert.True(perException < 65_536, $"Handling one exception allocated {perException} B, over the 64 KB budget.");
    }

    /// <summary>
    /// Accept-Language is attacker-controlled and unbounded: a client may send dozens of tags, and every
    /// one of them is expanded into several lookup candidates on the error path. That work has to stay
    /// proportional to the header — an expansion that went quadratic would make one malformed request plus
    /// a long header a cheap way to turn a failing endpoint into an expensive one.
    /// </summary>
    [Fact]
    public void ExceptionHandlingMiddleware_WithAPathologicalAcceptLanguageHeader_StaysProportionalToTheHeader()
    {
        var middleware = CreateMiddlewareWithErrorMessages(4);
        var manyLanguages = string.Join(",", Enumerable.Range(0, 40).Select(static i => $"zz-{i};q=0.5"));

        // None of them is configured, so the lookup walks every candidate and then falls back — the most
        // expensive route through GetUnknownErrorMessage, and still a 500 with a body.
        Assert.Equal(StatusCodes.Status500InternalServerError, InvokeHandlingOneException(middleware, manyLanguages));

        Action oneLanguage = () => InvokeHandlingOneException(middleware);
        Action fortyLanguages = () => InvokeHandlingOneException(middleware, manyLanguages);

        Warmup(WarmupIterations, oneLanguage);
        Warmup(WarmupIterations, fortyLanguages);
        var withOne = AllocatedBytesPerOperation(500, oneLanguage);
        var withForty = AllocatedBytesPerOperation(500, fortyLanguages);

        output.WriteLine($"Exception handling: {withOne} B/exception with 1 Accept-Language tag, {withForty} B/exception with 40.");

        // Linear expansion is a few hundred bytes per tag on top of the HttpContext and the body; 128 KB is
        // roughly ten times that, and an order of magnitude under a quadratic expansion of 40 tags.
        Assert.True(
            withForty < 131_072,
            $"Handling one exception with 40 Accept-Language tags allocated {withForty} B against {withOne} B for a single tag, over the 128 KB budget.");
    }

    /// <summary>
    /// The observable consequence of snapshotting, and the only non-statistical way to prove it: the
    /// middleware answers from the table it built at construction even after the configuration behind
    /// it changes. If it re-read configuration per error, it would pick the new message up.
    /// </summary>
    [Fact]
    public async Task ExceptionHandlingMiddleware_KeepsTheSnapshottedErrorMessageWhenConfigurationChangesAfterConstruction()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ErrorMessages:En"] = "original message" })
            .Build();

        var middleware = new ExceptionHandlingMiddleware(
            static _ => throw new InvalidOperationException("boom"),
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            configuration);

        configuration["ErrorMessages:En"] = "changed message";
        // Guard: if the configuration did not actually change, the assertion below would pass for the
        // wrong reason.
        Assert.Equal("changed message", configuration["ErrorMessages:En"]);

        var context = new DefaultHttpContext();
        context.Request.Headers.AcceptLanguage = "en";
        var body = new MemoryStream();
        context.Response.Body = body;

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        body.Position = 0;
        using var document = JsonDocument.Parse(body);
        var errors = document.RootElement.GetProperty("errors");
        // Exactly one message, not "the message is in there somewhere": a snapshot that leaked extra
        // entries — every configured language, say — would still satisfy a contains-style check.
        Assert.Equal(1, errors.GetArrayLength());
        Assert.Equal("original message", errors[0].GetString());
        // The documented error body is { errors, traceId }. The value is the ambient trace; the shape is ours.
        Assert.True(document.RootElement.TryGetProperty("traceId", out _), "The error body is missing its traceId member.");
    }

    /// <summary>
    /// The default-language list is the second thing snapshotted at construction, and it is the one that
    /// decides the message when the caller asks for a language nobody configured — the common case for a
    /// public API. Nothing else asserts it, and it would be easy to "fix" configuration reloading for the
    /// message table alone and leave this one reading live configuration.
    /// </summary>
    [Fact]
    public async Task ExceptionHandlingMiddleware_KeepsTheSnapshottedDefaultLanguageWhenConfigurationChangesAfterConstruction()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ErrorMessages:Default"] = "ar",
                ["ErrorMessages:Ar"] = "arabic message",
                ["ErrorMessages:En"] = "english message",
            })
            .Build();

        var middleware = new ExceptionHandlingMiddleware(
            static _ => throw new InvalidOperationException("boom"),
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            configuration);

        configuration["ErrorMessages:Default"] = "en";
        Assert.Equal("en", configuration["ErrorMessages:Default"]);

        var context = new DefaultHttpContext();
        // A language with no configured message, so the answer comes from the default-language list.
        context.Request.Headers.AcceptLanguage = "fr";
        var body = new MemoryStream();
        context.Response.Body = body;

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        body.Position = 0;
        using var document = JsonDocument.Parse(body);
        Assert.Equal("arabic message", document.RootElement.GetProperty("errors")[0].GetString());
    }

    // --------------------------------------------------------------------------------------------
    // RequestBodyJsonOptionsSetup: the resolver wraps a COPY of the chain.
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// Wrapping the live TypeInfoResolverChain instead of a copy makes the wrapper call itself until the
    /// stack overflows — a crash that takes the whole test host with it and cannot be caught. Appending to
    /// the chain is what AddProblemDetails() does, and it is the case that recursed.
    /// <para>
    /// Completing at all is half the contract; the other half is that the wrapper ran. The type info is
    /// therefore inspected, not merely null-checked: deleting the whole post-configure step would still
    /// return a JsonTypeInfo here, so a test that stopped at NotNull would go green for a library that no
    /// longer did anything.
    /// </para>
    /// </summary>
    [Fact]
    public void RequestBodyJsonOptionsSetup_ResolvingTypeInfoForAMediatorRequestExcludesRouteMembersWithoutRecursing()
    {
        var serializerOptions = CreatePerfHttpSerializerOptions(appendToResolverChain: true);

        var typeInfo = serializerOptions.GetTypeInfo(typeof(PerfJsonRequest));

        Assert.Equal(typeof(PerfJsonRequest), typeInfo.Type);
        AssertRouteMemberExcludedFromBody(typeInfo);
    }

    /// <summary>
    /// The same, for an app that never touches the resolver chain — no AddProblemDetails, no
    /// ConfigureHttpJsonOptions of its own. That is the default shape of an app using this package, and it
    /// reaches the post-configure step with a different chain than the test above, so the exclusion has to
    /// be asserted on both rather than assumed to carry over.
    /// </summary>
    [Fact]
    public void RequestBodyJsonOptionsSetup_WithNothingAddedToTheResolverChain_StillExcludesRouteMembers()
    {
        var serializerOptions = CreatePerfHttpSerializerOptions(appendToResolverChain: false);

        AssertRouteMemberExcludedFromBody(serializerOptions.GetTypeInfo(typeof(PerfJsonRequest)));

        var request = JsonSerializer.Deserialize<PerfJsonRequest>("""{"id":7,"name":"n"}""", serializerOptions);
        Assert.NotNull(request);
        Assert.Equal(0, request!.Id);
        Assert.Equal("n", request.Name);
    }

    /// <summary>
    /// The resolver reflects over constructors and attributes, which is affordable only because
    /// System.Text.Json asks it for a type once and caches the answer. If the wrapper started running
    /// per payload, every request body in the app would pay for it.
    /// </summary>
    [Fact]
    public void RequestBodyJsonOptionsSetup_DeserializingManyTimesAllocatesABoundedAmountPerCall()
    {
        var serializerOptions = CreatePerfHttpSerializerOptions(appendToResolverChain: true);
        const string Payload = """{"id":7,"name":"n"}""";

        // Correctness first: the route-bound member is discarded rather than bound from the body.
        var request = JsonSerializer.Deserialize<PerfJsonRequest>(Payload, serializerOptions);
        Assert.NotNull(request);
        Assert.Equal(0, request!.Id);
        Assert.Equal("n", request.Name);

        Action deserialize = () => JsonSerializer.Deserialize<PerfJsonRequest>(Payload, serializerOptions);
        Warmup(WarmupIterations, deserialize);
        var perCall = AllocatedBytesPerOperation(MeasuredIterations, deserialize);

        output.WriteLine($"Deserializing a mediator request through the HTTP JSON options: {perCall} B/call.");

        Assert.True(perCall < 8_192, $"Deserializing one request body allocated {perCall} B, over the 8 KB budget.");
    }

    /// <summary>Same budget on the way out, where the excluded members must also stay out of the body.</summary>
    [Fact]
    public void RequestBodyJsonOptionsSetup_SerializingManyTimesAllocatesABoundedAmountPerCall()
    {
        var serializerOptions = CreatePerfHttpSerializerOptions(appendToResolverChain: true);
        var request = new PerfJsonRequest(7, "n");

        var payload = JsonSerializer.Serialize(request, serializerOptions);
        Assert.DoesNotContain("\"id\"", payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"name\"", payload, StringComparison.OrdinalIgnoreCase);

        Action serialize = () => JsonSerializer.Serialize(request, serializerOptions);
        Warmup(WarmupIterations, serialize);
        var perCall = AllocatedBytesPerOperation(MeasuredIterations, serialize);

        output.WriteLine($"Serializing a mediator request through the HTTP JSON options: {perCall} B/call.");

        Assert.True(perCall < 8_192, $"Serializing one request allocated {perCall} B, over the 8 KB budget.");
    }

    /// <summary>
    /// The resolver is installed on the app-wide JSON options, so it sees every DTO the app serializes and
    /// not just mediator requests. It rewrites only requests, and the difference is observable: a plain DTO
    /// that happens to carry [FromRoute] — an MVC model, a type shared with a controller — keeps that
    /// member in its body, in both directions. Widening the check to "anything with a binding attribute"
    /// would silently start dropping fields from responses across the whole application.
    /// </summary>
    [Fact]
    public void RequestBodyJsonOptionsSetup_ATypeThatIsNotAMediatorRequest_KeepsItsRouteAttributedMembers()
    {
        var serializerOptions = CreatePerfHttpSerializerOptions(appendToResolverChain: true);

        var payload = JsonSerializer.Serialize(new PerfPlainDto { Id = 5, Name = "x" }, serializerOptions);
        Assert.Contains("\"id\"", payload, StringComparison.OrdinalIgnoreCase);

        var roundTripped = JsonSerializer.Deserialize<PerfPlainDto>("""{"id":5,"name":"x"}""", serializerOptions);
        Assert.NotNull(roundTripped);
        Assert.Equal(5, roundTripped!.Id);
        Assert.Equal("x", roundTripped.Name);
    }

    // --------------------------------------------------------------------------------------------
    // Endpoints: metadata is computed while routes are built, and duplicate detection is not quadratic.
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// The verb helpers infer the OpenAPI response contract by reflecting over the delegate's mediator
    /// request parameter. That reflection belongs to route building: the metadata is on the endpoint
    /// before any request has been served, and nothing has to look at the delegate again at runtime.
    /// The absent 204 matters as much as the present 200 — the helper deliberately does not advertise an
    /// empty response for a request that always has a body, or Swagger shows callers a status they can
    /// never receive and generated clients grow a branch for it.
    /// </summary>
    [Fact]
    public async Task VerbHelpers_MappingARequestWithAResponse_DeclaresTheResponseTypeAndNoEmptyResponse()
    {
        await using var app = BuildApp();

        app.MapGroup("perf-metadata").Post("echo", static (ISender sender, PerfEchoRequest request, CancellationToken cancellationToken)
            => sender.Send((object)request, cancellationToken));

        var responses = ResponsesOf(app, "perf-metadata/echo");

        Assert.Contains(responses, metadata => metadata.StatusCode == StatusCodes.Status200OK && metadata.Type == typeof(string));
        Assert.Contains(responses, metadata => metadata.StatusCode == StatusCodes.Status400BadRequest);
        Assert.Contains(responses, metadata => metadata.StatusCode == StatusCodes.Status500InternalServerError);
        Assert.Contains(responses, metadata => metadata.StatusCode == StatusCodes.Status401Unauthorized);
        Assert.DoesNotContain(responses, metadata => metadata.StatusCode == StatusCodes.Status204NoContent);
    }

    // Deliberately not covered here: a delegate whose parameter is declared as IRequest<TResponse> itself.
    // The response-type inference handles it, but mapping such a delegate also makes the interface a JSON
    // body parameter, and what ASP.NET Core does with that while building the endpoint is the framework's
    // business, not this library's. That case belongs with the rest of the verb helpers' metadata tests.

    /// <summary>
    /// A void request advertises the configured empty-response status and nothing else. "Nothing else" is
    /// the part with teeth: minimal APIs infer a 200 from the delegate's return type, so without the helper
    /// stripping it an endpoint that can only ever answer 204 would be documented as answering both. This
    /// also ties the two features together — the option decides the metadata, not only the response — which
    /// is otherwise a pair a reviewer has to check by hand.
    /// </summary>
    [Theory]
    [InlineData(EmptyResponseStatusCode.NoContent, StatusCodes.Status204NoContent, StatusCodes.Status200OK)]
    [InlineData(EmptyResponseStatusCode.Ok, StatusCodes.Status200OK, StatusCodes.Status204NoContent)]
    public async Task VerbHelpers_MappingAVoidRequest_DeclaresOnlyTheConfiguredEmptyResponseStatus(
        EmptyResponseStatusCode configured,
        int expectedStatusCode,
        int unexpectedStatusCode)
    {
        await using var app = BuildApp(options => options.EmptyResponseStatusCode = configured);

        app.MapGroup("perf-empty").Post("command", static (ISender sender, PerfVoidRequest request, CancellationToken cancellationToken)
            => sender.Send(request, cancellationToken));

        var responses = ResponsesOf(app, "perf-empty/command");

        Assert.Contains(responses, metadata => metadata.StatusCode == expectedStatusCode);
        Assert.DoesNotContain(responses, metadata => metadata.StatusCode == unexpectedStatusCode);
    }

    /// <summary>
    /// The detector groups endpoints by a match key, so a large route table costs one pass over it. This
    /// runs at startup on every app that calls MapEndpoints, so it must not be something a 2,000-route API
    /// notices. Completing is an assertion in its own right: 2,000 distinct routes have to produce no
    /// findings, and a match key that collapsed routes together would throw here instead of timing out.
    /// </summary>
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_OverTwoThousandUniqueEndpoints_ReportsNothingAndStaysWithinBudget()
    {
        await using var app = BuildApp();
        MapUniqueEndpoints(app, 2_000, "perf-detector");

        app.ValidateNoDuplicateEndpoints();
        var elapsed = MinimumElapsed(3, () => app.ValidateNoDuplicateEndpoints());

        output.WriteLine($"Duplicate detection over 2,000 endpoints: {elapsed.TotalMilliseconds:F1} ms.");

        Assert.True(
            elapsed < TimeSpan.FromSeconds(10),
            $"Duplicate detection over 2,000 endpoints took {elapsed.TotalSeconds:F2} s, over the 10 s budget.");
    }

    /// <summary>
    /// Scale must not blunt the detector. One route mapped twice among 2,000 is exactly the mistake this
    /// exists to catch — a merge that adds a route a colleague already added — and it has to be named in
    /// the report, because "somewhere in this application there are duplicate routes" is not a finding
    /// anybody can act on at 3am.
    /// </summary>
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_WithOneDuplicateAmongTwoThousandEndpoints_NamesThatRoute()
    {
        await using var app = BuildApp();
        MapUniqueEndpoints(app, 2_000, "perf-needle");
        app.MapGet("/perf-needle/collide", static () => Results.Ok());
        app.MapGet("/perf-needle/collide", static () => Results.Ok());

        var exception = Assert.Throws<DuplicateEndpointException>(() => app.ValidateNoDuplicateEndpoints());

        Assert.Equal("GET /perf-needle/collide", Assert.Single(exception.Routes));
        Assert.Contains("/perf-needle/collide", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// DuplicateEndpointHandling.None is the escape hatch for an app that maps duplicates on purpose, and
    /// it is documented as "do not look for duplicates" — not "look, then ignore what you find". An app
    /// that opted out must not pay for the scan at startup, and the proof is that the call allocates
    /// essentially nothing over a 2,000-route table it would otherwise have to walk and key.
    /// </summary>
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_WithHandlingNone_ReturnsWithoutScanningTheRouteTable()
    {
        await using var app = BuildApp();
        MapUniqueEndpoints(app, 2_000, "perf-skip");
        // A real duplicate, so "did not throw" cannot be explained by there being nothing to find.
        app.MapGet("/perf-skip/collide", static () => Results.Ok());
        app.MapGet("/perf-skip/collide", static () => Results.Ok());

        Assert.Same(app, app.ValidateNoDuplicateEndpoints(DuplicateEndpointHandling.None));

        var perCall = AllocatedBytesPerOperation(10, () => app.ValidateNoDuplicateEndpoints(DuplicateEndpointHandling.None));

        output.WriteLine($"Duplicate detection skipped over 2,000 endpoints: {perCall} B/call.");

        Assert.True(
            perCall <= 256,
            $"Skipping duplicate detection allocated {perCall} B over a 2,000-route table; the opt-out is not short-circuiting.");
    }

    /// <summary>
    /// Grouping by key is linear; comparing every endpoint against every other one is not. With 16x the
    /// endpoints, linear means roughly 16x the time and pairwise comparison means roughly 256x.
    /// </summary>
    [Fact]
    public async Task ValidateNoDuplicateEndpoints_CostGrowsWithEndpointCountNotItsSquare()
    {
        await using var small = BuildApp();
        await using var large = BuildApp();
        MapUniqueEndpoints(small, 200, "perf-scale-small");
        MapUniqueEndpoints(large, 3_200, "perf-scale-large");

        small.ValidateNoDuplicateEndpoints();
        large.ValidateNoDuplicateEndpoints();

        // Minimum of several rounds: noise on a shared machine only ever adds time, never removes it, so
        // the best round of each is the closest either measurement gets to the code's own cost.
        var smallElapsed = MinimumElapsed(5, () => small.ValidateNoDuplicateEndpoints());
        var largeElapsed = MinimumElapsed(5, () => large.ValidateNoDuplicateEndpoints());
        var ratio = RatioOf(largeElapsed, smallElapsed);

        output.WriteLine($"Duplicate detection: 200 endpoints {smallElapsed.TotalMilliseconds:F2} ms, 3,200 endpoints {largeElapsed.TotalMilliseconds:F2} ms (ratio {ratio:F1}x for 16x the endpoints).");

        // 120x is five times the linear expectation — enough for worse cache locality at the larger size
        // and an unlucky round on a busy agent — and still less than half the quadratic one.
        Assert.True(
            ratio < 120,
            $"16x the endpoints cost {ratio:F1}x the time (200 endpoints: {smallElapsed.TotalMilliseconds:F2} ms, 3,200 endpoints: {largeElapsed.TotalMilliseconds:F2} ms).");
    }

    /// <summary>
    /// Mapping cost is per endpoint: each call adds its route entry and its response metadata. Anything
    /// that re-walked the already-mapped routes on every new one would turn startup of a large API into
    /// a quadratic wait.
    /// </summary>
    [Fact]
    public async Task VerbHelpers_MappingCostGrowsWithTheNumberOfEndpointsNotItsSquare()
    {
        // Three rounds, not five: each round needs its own WebApplication, and building one costs more
        // than the mapping being measured.
        var smallElapsed = await MinimumMapTimeAsync(3, 100, "perf-map-small");
        var largeElapsed = await MinimumMapTimeAsync(3, 1_600, "perf-map-large");
        var ratio = RatioOf(largeElapsed, smallElapsed);

        output.WriteLine($"Mapping: 100 endpoints {smallElapsed.TotalMilliseconds:F2} ms, 1,600 endpoints {largeElapsed.TotalMilliseconds:F2} ms (ratio {ratio:F1}x for 16x the endpoints).");

        Assert.True(
            ratio < 120,
            $"Mapping 16x the endpoints cost {ratio:F1}x the time (100 endpoints: {smallElapsed.TotalMilliseconds:F2} ms, 1,600 endpoints: {largeElapsed.TotalMilliseconds:F2} ms).");
    }

    // --------------------------------------------------------------------------------------------
    // Measurement helpers.
    // --------------------------------------------------------------------------------------------

    private static void Warmup(int iterations, Action operation)
    {
        for (var i = 0; i < iterations; i++)
            operation();
    }

    /// <summary>
    /// Bytes allocated per iteration, measured on the calling thread only — other test classes running
    /// in parallel allocate on their own threads and cannot pollute this. Every operation measured here
    /// completes synchronously, so no continuation escapes to a pool thread and out of the count.
    /// </summary>
    private static long AllocatedBytesPerOperation(int iterations, Action operation)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < iterations; i++)
            operation();

        return (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
    }

    private static TimeSpan MinimumElapsed(int rounds, Action operation)
    {
        var best = TimeSpan.MaxValue;

        for (var round = 0; round < rounds; round++)
        {
            var stopwatch = Stopwatch.StartNew();
            operation();
            stopwatch.Stop();

            if (stopwatch.Elapsed < best)
                best = stopwatch.Elapsed;
        }

        return best;
    }

    /// <summary>
    /// How many times longer the larger run took. The floor only keeps a division by zero out; it sits far
    /// below any measurement here, so it never flatters the ratio the way rounding the smaller run up to a
    /// whole millisecond would.
    /// </summary>
    private static double RatioOf(TimeSpan larger, TimeSpan smaller)
        => larger.TotalMilliseconds / Math.Max(smaller.TotalMilliseconds, 0.01);

    private static async Task<TimeSpan> MinimumMapTimeAsync(int rounds, int endpointCount, string prefix)
    {
        var best = TimeSpan.MaxValue;

        for (var round = 0; round < rounds; round++)
        {
            // A fresh app per round: routes cannot be unmapped, so mapping can only be timed once per app.
            await using var app = BuildApp();

            var stopwatch = Stopwatch.StartNew();
            for (var i = 0; i < endpointCount; i++)
                app.Post($"/{prefix}/{i}", static (ISender sender, PerfEchoRequest request) => sender.Send((object)request));
            stopwatch.Stop();

            if (stopwatch.Elapsed < best)
                best = stopwatch.Elapsed;
        }

        return best;
    }

    private static long MeasurePerSendWithBehaviors(int behaviorCount)
    {
        using var provider = CreateSenderProvider(services =>
        {
            for (var i = 0; i < behaviorCount; i++)
                services.AddTransient<IPipelineBehavior<PerfEchoRequest, string>, PerfPassThroughBehavior<PerfEchoRequest, string>>();
        });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new PerfEchoRequest("pipeline");

        // The response still has to come back out through every behavior.
        Assert.Equal("pipeline", sender.Send<PerfEchoRequest, string>(request).GetAwaiter().GetResult());

        Action send = () => sender.Send<PerfEchoRequest, string>(request).GetAwaiter().GetResult();
        Warmup(WarmupIterations, send);

        return AllocatedBytesPerOperation(MeasuredIterations, send);
    }

    // --------------------------------------------------------------------------------------------
    // Assertion helpers.
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// The shape the resolver leaves behind for a member bound outside the body: no getter, so it is never
    /// written, and no setter, so the body cannot supply it. The ordinary body member beside it keeps its
    /// getter, which is what makes this a statement about the excluded member rather than about the type as
    /// a whole — a resolver that emptied every property would pass the first half of this on its own.
    /// </summary>
    private static void AssertRouteMemberExcludedFromBody(JsonTypeInfo typeInfo)
    {
        var routeMember = PropertyNamed(typeInfo, "id");
        var bodyMember = PropertyNamed(typeInfo, "name");

        Assert.Null(routeMember.Get);
        Assert.Null(routeMember.Set);
        Assert.NotNull(bodyMember.Get);
    }

    private static JsonPropertyInfo PropertyNamed(JsonTypeInfo typeInfo, string name)
        => Assert.Single(typeInfo.Properties, property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase));

    private static IProducesResponseTypeMetadata[] ResponsesOf(WebApplication app, string routeSuffix)
        => app.Endpoint(routeSuffix).Metadata.OfType<IProducesResponseTypeMetadata>().ToArray();

    // --------------------------------------------------------------------------------------------
    // Fixture helpers.
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// Registers exactly the handlers this file needs instead of scanning the test assembly, so a
    /// handler added by a sibling test file cannot move these numbers.
    /// </summary>
    private static ServiceProvider CreateSenderProvider(Action<IServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddMediator();
        services.AddTransient<IRequestHandler<PerfEchoRequest, string>, PerfEchoRequestHandler>();
        services.AddTransient<IRequestHandler<PerfCounterRequest, SingleResponse<int>>, PerfCounterRequestHandler>();
        services.AddTransient<IRequestHandler<PerfJsonRequest, string>, PerfJsonRequestHandler>();
        services.AddTransient<IRequestHandler<PerfColdStartRequest, string>, PerfColdStartRequestHandler>();
        services.AddTransient<IRequestHandler<PerfConcurrentColdStartRequest, string>, PerfConcurrentColdStartRequestHandler>();
        services.AddTransient<IRequestHandler<PerfSharedCacheRequest, string>, PerfSharedCacheRequestHandler>();
        services.AddTransient<IRequestHandler<PerfVoidRequest>, PerfVoidRequestHandler>();
        configureServices?.Invoke(services);

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static WebApplication BuildApp(Action<MediatorOptions>? configureOptions = null)
    {
        var builder = TestApps.CreateBuilder();

        if (configureOptions is null)
            builder.Services.AddMediator();
        else
            builder.Services.AddMediator(configureOptions);

        builder.Services.AddTransient<IRequestHandler<PerfEchoRequest, string>, PerfEchoRequestHandler>();

        return builder.Build();
    }

    private static void MapUniqueEndpoints(WebApplication app, int count, string prefix)
    {
        for (var i = 0; i < count; i++)
            app.MapGet($"/{prefix}/{i}", static () => Results.Ok());
    }

    private static ExceptionHandlingMiddleware CreateMiddlewareWithErrorMessages(int additionalMessageCount)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ErrorMessages:Default"] = "en",
            ["ErrorMessages:En"] = "Unknown error occurred",
            ["ErrorMessages:Ar"] = "unknown",
        };

        for (var i = 0; i < additionalMessageCount; i++)
            settings[$"ErrorMessages:lang-{i}"] = $"message {i}";

        return new ExceptionHandlingMiddleware(
            static _ => throw new InvalidOperationException("boom"),
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    /// <summary>
    /// One trip through the middleware with a failing inner delegate, returning the status it wrote.
    /// The response body is left at the context's default (Stream.Null) so the measurement is the
    /// middleware's cost and not a growing MemoryStream's.
    /// </summary>
    private static int InvokeHandlingOneException(ExceptionHandlingMiddleware middleware, string acceptLanguage = "en")
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.AcceptLanguage = acceptLanguage;

        middleware.InvokeAsync(context).GetAwaiter().GetResult();

        return context.Response.StatusCode;
    }

    private static JsonSerializerOptions CreatePerfHttpSerializerOptions(bool appendToResolverChain)
    {
        var services = new ServiceCollection();
        services.AddMediator();

        if (appendToResolverChain)
        {
            services.ConfigureHttpJsonOptions(static options =>
                options.SerializerOptions.TypeInfoResolverChain.Insert(0, new DefaultJsonTypeInfoResolver()));
        }

        using var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;
    }
}

// ------------------------------------------------------------------------------------------------
// Requests and handlers. Every one is declared here rather than reused from another test file, and
// every namespace-scope handler is parameterless so the assembly scans other tests run can construct
// them.
// ------------------------------------------------------------------------------------------------

/// <summary>A class request that echoes its value, so a response can be attributed to its sender.</summary>
public sealed class PerfEchoRequest(string value) : IRequest<string>
{
    public string Value { get; } = value;
}

public sealed class PerfEchoRequestHandler : IRequestHandler<PerfEchoRequest, string>
{
    public Task<string> Handle(PerfEchoRequest request, CancellationToken cancellationToken)
        => Task.FromResult(request.Value);
}

/// <summary>A record request wrapped in SingleResponse, the shape the README documents.</summary>
public sealed record PerfCounterRequest(int Value) : IRequest<SingleResponse<int>>;

public sealed class PerfCounterRequestHandler : IRequestHandler<PerfCounterRequest, SingleResponse<int>>
{
    public Task<SingleResponse<int>> Handle(PerfCounterRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new SingleResponse<int>(request.Value));
}

public sealed class PerfVoidRequest : IRequest
{
}

public sealed class PerfVoidRequestHandler : IRequestHandler<PerfVoidRequest>
{
    public Task Handle(PerfVoidRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A positional record with a route-bound member, which is what the JSON resolver rewrites.</summary>
public sealed record PerfJsonRequest([FromRoute] int Id, string Name) : IRequest<string>;

public sealed class PerfJsonRequestHandler : IRequestHandler<PerfJsonRequest, string>
{
    public Task<string> Handle(PerfJsonRequest request, CancellationToken cancellationToken)
        => Task.FromResult(request.Name);
}

/// <summary>
/// Used by exactly one test, so that test is the first thing in the process to put this type into the
/// mediator's static wrapper cache.
/// </summary>
public sealed class PerfColdStartRequest : IRequest<string>
{
}

public sealed class PerfColdStartRequestHandler : IRequestHandler<PerfColdStartRequest, string>
{
    public Task<string> Handle(PerfColdStartRequest request, CancellationToken cancellationToken)
        => Task.FromResult("cold");
}

/// <summary>
/// The same idea for the concurrency test: nothing else in the process sends it, so several threads
/// genuinely race to create its one entry in the wrapper cache.
/// </summary>
public sealed class PerfConcurrentColdStartRequest : IRequest<string>
{
}

public sealed class PerfConcurrentColdStartRequestHandler : IRequestHandler<PerfConcurrentColdStartRequest, string>
{
    public Task<string> Handle(PerfConcurrentColdStartRequest request, CancellationToken cancellationToken)
        => Task.FromResult("cold-concurrent");
}

/// <summary>Sent from two containers at once, to show the static wrapper cache holds no container state.</summary>
public sealed class PerfSharedCacheRequest : IRequest<string>
{
}

public sealed class PerfSharedCacheRequestHandler : IRequestHandler<PerfSharedCacheRequest, string>
{
    public Task<string> Handle(PerfSharedCacheRequest request, CancellationToken cancellationToken)
        => Task.FromResult("primary");
}

/// <summary>
/// A generic host for the second handler of <see cref="PerfSharedCacheRequest"/>. Nested inside an open
/// generic so the assembly scan skips it — it still has generic parameters — and only the explicit
/// registration in one test ever uses it. Two handlers for one request type at namespace scope would
/// instead fail every other test file's assembly scan.
/// </summary>
public static class PerfHandlerHost<TMarker>
{
    public sealed class AlternateSharedCacheHandler : IRequestHandler<PerfSharedCacheRequest, string>
    {
        public Task<string> Handle(PerfSharedCacheRequest request, CancellationToken cancellationToken)
            => Task.FromResult("alternate");
    }
}

/// <summary>Not a request at all: the input for the "must implement IRequest" failure path.</summary>
public sealed class PerfNotARequest
{
}

/// <summary>
/// A plain DTO carrying [FromRoute] without being a mediator request — an MVC model, or a type shared
/// with a controller. The body resolver has to leave it alone.
/// </summary>
public sealed class PerfPlainDto
{
    [FromRoute]
    public int Id { get; set; }

    public string? Name { get; set; }
}

/// <summary>
/// An await-and-forward behavior, the shape validation and Sentry use. Open generic, so the assembly
/// scans never see it; it is registered explicitly, several times over, to price a deeper pipeline.
/// </summary>
public sealed class PerfPassThroughBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        => await next().ConfigureAwait(false);
}

/// <summary>Counts behavior invocations across a run. Only ever registered by this file's tests.</summary>
public sealed class PerfCallCounter
{
    private int count;

    public int Count => Volatile.Read(ref count);

    public void Increment() => Interlocked.Increment(ref count);
}

public sealed class PerfCountingBehavior(PerfCallCounter counter) : IPipelineBehavior<PerfEchoRequest, string>
{
    public Task<string> Handle(PerfEchoRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
    {
        counter.Increment();

        return next();
    }
}

/// <summary>The void-pipeline counterpart, so a void send can be shown to have reached the pipeline.</summary>
public sealed class PerfVoidCountingBehavior(PerfCallCounter counter) : IPipelineBehavior<PerfVoidRequest>
{
    public Task Handle(PerfVoidRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken)
    {
        counter.Increment();

        return next();
    }
}

#pragma warning restore xUnit1031, ASP0022
