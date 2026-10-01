using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Mediator;
using Xunit;
using MediatorImplementation = Phoenix.Mediator.Mediator.Mediator;

namespace Phoenix.Mediator.Tests;

/// <summary>
/// <see cref="IRequestBehavior{TRequest}"/>: one behavior for every request, with or without a response, running in one
/// registration order with the <see cref="IPipelineBehavior{TRequest, TResponse}"/>/<see cref="IPipelineBehavior{TRequest}"/>
/// behaviors, and <c>AddMediatorBehavior</c>, which registers either kind.
/// <para>
/// Types are prefixed <c>Rb</c>. Handlers record into the log their request carries, so they stay dependency-free for the
/// other tests' assembly scans; behaviors are never scanned, so they take the log from the container.
/// </para>
/// </summary>
public sealed class RequestBehaviorTests
{
    // ---------------------------------------------------------------------------------------------
    // One behavior, both kinds of request.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task RequestBehavior_RunsForARequestWithAResponse_AndSeesItsResponseType()
    {
        var log = new RbLog();
        using var scope = CreateScope(log, services => services.AddMediatorBehavior(typeof(RbResponseTypeBehavior<>)));

        var response = await scope.Sender.Send(new RbQuery(log));

        Assert.Equal("handled", response);
        Assert.Equal(new[] { "behavior sees String", "handler" }, log.Entries);
    }

    // The point of the interface: the same class runs for a request without a response, which it sees as NoResponse.
    [Fact]
    public async Task RequestBehavior_RunsForARequestWithoutAResponse_AndSeesNoResponse()
    {
        var log = new RbLog();
        using var scope = CreateScope(log, services => services.AddMediatorBehavior(typeof(RbResponseTypeBehavior<>)));

        await scope.Sender.Send(new RbCommand(log));

        Assert.Equal(new[] { "behavior sees NoResponse", "handler" }, log.Entries);
    }

    // A caching behavior has no TResponse to construct; returning a cached value cast to TResponse is how it answers.
    [Fact]
    public async Task RequestBehavior_CanShortCircuitWithAResponseOfItsOwn()
    {
        var log = new RbLog();
        using var scope = CreateScope(log, services => services.AddMediatorBehavior(typeof(RbCachingBehavior<>)));

        var response = await scope.Sender.Send(new RbQuery(log));

        Assert.Equal("from cache", response);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task RequestBehavior_CanShortCircuitARequestWithoutAResponse()
    {
        var log = new RbLog();
        using var scope = CreateScope(log, services => services.AddMediatorBehavior(typeof(RbCachingBehavior<>)));

        await scope.Sender.Send(new RbCommand(log));

        Assert.Empty(log.Entries);
    }

    // A transaction behavior's rollback path: the handler's exception has to pass through the behavior, unchanged.
    [Fact]
    public async Task RequestBehavior_SeesTheHandlersException()
    {
        var log = new RbLog();
        using var scope = CreateScope(log, services => services.AddMediatorBehavior(typeof(RbRollbackBehavior<>)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Sender.Send(new RbFailingCommand(log)));

        Assert.Equal("rb-failure", exception.Message);
        Assert.Equal(new[] { "begin", "handler", "rollback: rb-failure" }, log.Entries);
    }

    // Constraints on the behavior's TRequest decide which requests it runs for; the container skips the rest instead
    // of failing to close the generic.
    [Fact]
    public async Task RequestBehavior_WithAConstraint_RunsOnlyForRequestsThatSatisfyIt()
    {
        var log = new RbLog();
        using var scope = CreateScope(log, services => services.AddMediatorBehavior(typeof(RbTransactionalOnlyBehavior<>)));

        await scope.Sender.Send(new RbQuery(log));
        await scope.Sender.Send(new RbTransactionalCommand(log));

        Assert.Equal(new[] { "handler", "transactional behavior", "handler" }, log.Entries);
    }

    // ---------------------------------------------------------------------------------------------
    // One registration order across both behavior kinds.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Behaviors_OfBothKinds_RunInRegistrationOrder_ForARequestWithAResponse()
    {
        var log = new RbLog();
        using var scope = CreateScope(log, services => services
            .AddMediatorBehavior(typeof(RbNamedPipelineBehaviorA))
            .AddMediatorBehavior(typeof(RbNamedRequestBehaviorB<>))
            .AddMediatorBehavior(typeof(RbNamedPipelineBehaviorC))
            .AddMediatorBehavior(typeof(RbNamedRequestBehaviorD<>)));

        await scope.Sender.Send(new RbQuery(log));

        Assert.Equal(new[] { "A", "B", "C", "D", "handler" }, log.Entries);
    }

    [Fact]
    public async Task Behaviors_OfBothKinds_RunInRegistrationOrder_ForARequestWithoutAResponse()
    {
        var log = new RbLog();
        using var scope = CreateScope(log, services => services
            .AddMediatorBehavior(typeof(RbNamedRequestBehaviorB<>))
            .AddMediatorBehavior(typeof(RbNamedVoidPipelineBehaviorA))
            .AddMediatorBehavior(typeof(RbNamedRequestBehaviorD<>))
            .AddMediatorBehavior(typeof(RbNamedVoidPipelineBehaviorC)));

        await scope.Sender.Send(new RbCommand(log));

        Assert.Equal(new[] { "B", "A", "D", "C", "handler" }, log.Entries);
    }

    // The order is the registration order, not the order of the AddMediatorBehavior calls: plain container
    // registrations count the same, which is how every behavior written before the helper existed is registered.
    [Fact]
    public async Task Behaviors_RegisteredDirectlyWithTheContainer_KeepTheirRegistrationOrder()
    {
        var log = new RbLog();
        using var scope = CreateScope(log, services =>
        {
            services.AddTransient(typeof(IRequestBehavior<>), typeof(RbNamedRequestBehaviorB<>));
            services.AddTransient<IPipelineBehavior<RbQuery, string>, RbNamedPipelineBehaviorA>();
        });

        await scope.Sender.Send(new RbQuery(log));

        Assert.Equal(new[] { "B", "A", "handler" }, log.Entries);
    }

    // A factory registration has no implementation type to key on; its place comes from its position in the service
    // collection, like every other registration. Registered first, it runs outermost.
    [Fact]
    public async Task Behaviors_RegisteredThroughAFactory_KeepTheirRegistrationOrder()
    {
        var log = new RbLog();
        using var scope = CreateScope(log, services =>
        {
            services.AddTransient<IRequestBehavior<RbQuery>>(provider => new RbNamedRequestBehaviorD<RbQuery>(provider.GetRequiredService<RbLog>()));
            services.AddMediatorBehavior(typeof(RbNamedPipelineBehaviorA));
            services.AddTransient<IPipelineBehavior<RbQuery, string>>(provider => new RbNamedPipelineBehaviorC(provider.GetRequiredService<RbLog>()));
            services.AddMediatorBehavior(typeof(RbNamedRequestBehaviorB<>));
        });

        await scope.Sender.Send(new RbQuery(log));

        Assert.Equal(new[] { "D", "A", "C", "B", "handler" }, log.Entries);
    }

    // A behavior registered where AddMediator cannot see it — natively in a third-party container, simulated here by
    // registering it in a copy of the collection — must still run. Whether any exist is asked of the container.
    [Fact]
    public async Task RequestBehavior_RegisteredOutsideTheCollectionAddMediatorWasGiven_StillRuns()
    {
        var log = new RbLog();
        var seenByAddMediator = new ServiceCollection();
        seenByAddMediator.AddMediator(typeof(RequestBehaviorTests).Assembly);

        IServiceCollection container = new ServiceCollection();
        foreach (var descriptor in seenByAddMediator)
            container.Add(descriptor);
        container.AddSingleton(log);
        container.AddTransient(typeof(IRequestBehavior<>), typeof(RbNamedRequestBehaviorB<>));

        await using var provider = container.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new RbQuery(log));

        Assert.Equal(new[] { "B", "handler" }, log.Entries);
    }

    // A Mediator built by hand, outside AddMediator, has no registration order to read. It must still run every
    // behavior, and does so with the request behaviors outside.
    [Fact]
    public async Task Mediator_ConstructedWithoutAddMediator_RunsRequestBehaviorsOutsidePipelineBehaviors()
    {
        var log = new RbLog();
        var services = new ServiceCollection()
            .AddSingleton(log)
            .AddTransient<IRequestHandler<RbQuery, string>, RbQueryHandler>()
            .AddTransient<IPipelineBehavior<RbQuery, string>, RbNamedPipelineBehaviorA>()
            .AddTransient<IRequestBehavior<RbQuery>, RbNamedRequestBehaviorB<RbQuery>>();
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var mediator = new MediatorImplementation(provider, Options.Create(new MediatorOptions()));

        await mediator.Send(new RbQuery(log));

        Assert.Equal(new[] { "B", "A", "handler" }, log.Entries);
    }

    // ---------------------------------------------------------------------------------------------
    // AddMediatorBehavior.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AddMediatorBehavior_RegistersAnOpenGenericRequestBehaviorAsTheOpenInterface()
    {
        var services = new ServiceCollection().AddMediatorBehavior(typeof(RbResponseTypeBehavior<>));

        var descriptor = Assert.Single(services);
        Assert.Equal(typeof(IRequestBehavior<>), descriptor.ServiceType);
        Assert.Equal(typeof(RbResponseTypeBehavior<>), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
    }

    [Fact]
    public void AddMediatorBehavior_RegistersAnOpenGenericPipelineBehaviorOfEitherShape()
    {
        var services = new ServiceCollection()
            .AddMediatorBehavior(typeof(RbOpenPipelineBehavior<,>))
            .AddMediatorBehavior(typeof(RbOpenVoidPipelineBehavior<>));

        Assert.Contains(services, static d => d.ServiceType == typeof(IPipelineBehavior<,>) && d.ImplementationType == typeof(RbOpenPipelineBehavior<,>));
        Assert.Contains(services, static d => d.ServiceType == typeof(IPipelineBehavior<>) && d.ImplementationType == typeof(RbOpenVoidPipelineBehavior<>));
    }

    [Fact]
    public void AddMediatorBehavior_RegistersAClosedBehaviorAsTheClosedInterface()
    {
        var services = new ServiceCollection().AddMediatorBehavior(typeof(RbNamedPipelineBehaviorA), ServiceLifetime.Scoped);

        var descriptor = Assert.Single(services);
        Assert.Equal(typeof(IPipelineBehavior<RbQuery, string>), descriptor.ServiceType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    // Called from several places (a shared registration method, a test fixture), a behavior must still run once.
    [Fact]
    public void AddMediatorBehavior_CalledTwiceForOneType_RegistersItOnce()
    {
        var services = new ServiceCollection()
            .AddMediatorBehavior(typeof(RbResponseTypeBehavior<>))
            .AddMediatorBehavior(typeof(RbResponseTypeBehavior<>));

        Assert.Single(services);
    }

    [Fact]
    public void AddMediatorBehavior_RejectsATypeThatIsNotABehavior()
    {
        var exception = Assert.Throws<ArgumentException>(() => new ServiceCollection().AddMediatorBehavior(typeof(RbLog)));

        Assert.Contains(typeof(RbLog).FullName!, exception.Message);
    }

    [Fact]
    public void AddMediatorBehavior_RejectsAnAbstractBehavior()
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddMediatorBehavior(typeof(RbAbstractBehavior<>)));
    }

    // The container applies no variance: a closed IRequestBehavior<object> is never asked for in place of
    // IRequestBehavior<RbQuery>, and requests are never dispatched as an interface. Registered, either would silently
    // never run, so both are refused with the generic alternative spelled out.
    [Theory]
    [InlineData(typeof(RbEverythingBehavior))]
    [InlineData(typeof(RbTransactionalInterfaceBehavior))]
    public void AddMediatorBehavior_RejectsAClosedBehaviorThatWouldNeverRun(Type behaviorType)
    {
        var exception = Assert.Throws<ArgumentException>(() => new ServiceCollection().AddMediatorBehavior(behaviorType));

        Assert.Contains("would never run", exception.Message);
        Assert.Contains($"{behaviorType.Name}<TRequest> : IRequestBehavior<TRequest>", exception.Message);
    }

    // An abstract base request is a type requests ARE dispatched as — Send<TBase, TResponse>(request) — so a closed
    // behavior over one is legitimate.
    [Fact]
    public void AddMediatorBehavior_AcceptsAClosedBehaviorOverAnAbstractBaseRequest()
    {
        var services = new ServiceCollection().AddMediatorBehavior(typeof(RbBaseQueryBehavior));

        Assert.Equal(typeof(IRequestBehavior<RbBaseQuery>), Assert.Single(services).ServiceType);
    }

    // The container closes an open generic by handing it the service's type arguments in order. A behavior whose type
    // parameters are not exactly the interface's arguments cannot be closed; registering it anyway fails on the first
    // request, so it is refused here, at startup, instead.
    [Fact]
    public void AddMediatorBehavior_RejectsAnOpenGenericTheContainerCannotClose()
    {
        var exception = Assert.Throws<ArgumentException>(() => new ServiceCollection().AddMediatorBehavior(typeof(RbPartlyClosedBehavior<>)));

        Assert.Contains("type parameters", exception.Message);
    }

    [Fact]
    public void NoResponse_HasOneValue()
    {
        Assert.Equal(NoResponse.Value, default);
        Assert.True(NoResponse.Value == new NoResponse());
        Assert.False(NoResponse.Value != new NoResponse());
        Assert.Equal(NoResponse.Value.GetHashCode(), new NoResponse().GetHashCode());
    }

    private static SendScope CreateScope(RbLog log, Action<IServiceCollection> configure)
    {
        return new SendScope(services =>
        {
            services.AddSingleton(log);
            configure(services);
        });
    }
}

// -------------------------------------------------------------------------------------------------
// Requests and handlers.
// -------------------------------------------------------------------------------------------------

public sealed class RbLog
{
    private readonly List<string> entries = [];

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (entries)
                return entries.ToArray();
        }
    }

    public void Add(string entry)
    {
        lock (entries)
            entries.Add(entry);
    }
}

public interface IRbTransactional;

public sealed record RbQuery(RbLog Log) : IRequest<string>;

public sealed record RbCommand(RbLog Log) : IRequest;

public sealed record RbFailingCommand(RbLog Log) : IRequest;

public sealed record RbTransactionalCommand(RbLog Log) : IRequest, IRbTransactional;

public sealed class RbQueryHandler : IRequestHandler<RbQuery, string>
{
    public Task<string> Handle(RbQuery request, CancellationToken cancellationToken)
    {
        request.Log.Add("handler");
        return Task.FromResult("handled");
    }
}

public sealed class RbCommandHandler : IRequestHandler<RbCommand>
{
    public Task Handle(RbCommand request, CancellationToken cancellationToken)
    {
        request.Log.Add("handler");
        return Task.CompletedTask;
    }
}

public sealed class RbFailingCommandHandler : IRequestHandler<RbFailingCommand>
{
    public Task Handle(RbFailingCommand request, CancellationToken cancellationToken)
    {
        request.Log.Add("handler");
        throw new InvalidOperationException("rb-failure");
    }
}

public sealed class RbTransactionalCommandHandler : IRequestHandler<RbTransactionalCommand>
{
    public Task Handle(RbTransactionalCommand request, CancellationToken cancellationToken)
    {
        request.Log.Add("handler");
        return Task.CompletedTask;
    }
}

// -------------------------------------------------------------------------------------------------
// Request behaviors: written once, for any request.
// -------------------------------------------------------------------------------------------------

public sealed class RbResponseTypeBehavior<TRequest>(RbLog log) : IRequestBehavior<TRequest>
{
    public Task<TResponse> Handle<TResponse>(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        log.Add($"behavior sees {typeof(TResponse).Name}");
        return next();
    }
}

public sealed class RbCachingBehavior<TRequest> : IRequestBehavior<TRequest>
{
    public Task<TResponse> Handle<TResponse>(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        object cached = typeof(TResponse) == typeof(NoResponse) ? NoResponse.Value : "from cache";
        return Task.FromResult((TResponse)cached);
    }
}

public sealed class RbRollbackBehavior<TRequest>(RbLog log) : IRequestBehavior<TRequest>
{
    public async Task<TResponse> Handle<TResponse>(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        log.Add("begin");
        try
        {
            return await next();
        }
        catch (Exception exception)
        {
            log.Add($"rollback: {exception.Message}");
            throw;
        }
    }
}

public sealed class RbTransactionalOnlyBehavior<TRequest>(RbLog log) : IRequestBehavior<TRequest> where TRequest : IRbTransactional
{
    public Task<TResponse> Handle<TResponse>(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        log.Add("transactional behavior");
        return next();
    }
}

public sealed class RbNamedRequestBehaviorB<TRequest>(RbLog log) : IRequestBehavior<TRequest>
{
    public Task<TResponse> Handle<TResponse>(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        log.Add("B");
        return next();
    }
}

public sealed class RbNamedRequestBehaviorD<TRequest>(RbLog log) : IRequestBehavior<TRequest>
{
    public Task<TResponse> Handle<TResponse>(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        log.Add("D");
        return next();
    }
}

public abstract class RbAbstractBehavior<TRequest> : IRequestBehavior<TRequest>
{
    public abstract Task<TResponse> Handle<TResponse>(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}

/// <summary>Closed over object, hoping variance would make it run for everything. It would not.</summary>
public sealed class RbEverythingBehavior : IRequestBehavior<object>
{
    public Task<TResponse> Handle<TResponse>(object request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next();
}

/// <summary>Closed over a marker interface. Requests are never dispatched as an interface, so it would not run either.</summary>
public sealed class RbTransactionalInterfaceBehavior : IRequestBehavior<IRbTransactional>
{
    public Task<TResponse> Handle<TResponse>(IRbTransactional request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next();
}

public abstract record RbBaseQuery : IRequest<string>;

public sealed class RbBaseQueryBehavior : IRequestBehavior<RbBaseQuery>
{
    public Task<TResponse> Handle<TResponse>(RbBaseQuery request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next();
}

// -------------------------------------------------------------------------------------------------
// Pipeline behaviors of the two original shapes, to interleave with the request behaviors.
// -------------------------------------------------------------------------------------------------

public sealed class RbNamedPipelineBehaviorA(RbLog log) : IPipelineBehavior<RbQuery, string>
{
    public Task<string> Handle(RbQuery request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
    {
        log.Add("A");
        return next();
    }
}

public sealed class RbNamedPipelineBehaviorC(RbLog log) : IPipelineBehavior<RbQuery, string>
{
    public Task<string> Handle(RbQuery request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
    {
        log.Add("C");
        return next();
    }
}

public sealed class RbNamedVoidPipelineBehaviorA(RbLog log) : IPipelineBehavior<RbCommand>
{
    public Task Handle(RbCommand request, RequestHandlerDelegate next, CancellationToken cancellationToken)
    {
        log.Add("A");
        return next();
    }
}

public sealed class RbNamedVoidPipelineBehaviorC(RbLog log) : IPipelineBehavior<RbCommand>
{
    public Task Handle(RbCommand request, RequestHandlerDelegate next, CancellationToken cancellationToken)
    {
        log.Add("C");
        return next();
    }
}

public sealed class RbOpenPipelineBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next();
}

public sealed class RbOpenVoidPipelineBehavior<TRequest> : IPipelineBehavior<TRequest> where TRequest : IRequest
{
    public Task Handle(TRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken) => next();
}

/// <summary>Open over the request but closed over the response, so the container cannot close it as IPipelineBehavior&lt;,&gt;.</summary>
public sealed class RbPartlyClosedBehavior<TRequest> : IPipelineBehavior<TRequest, string> where TRequest : IRequest<string>
{
    public Task<string> Handle(TRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken) => next();
}
