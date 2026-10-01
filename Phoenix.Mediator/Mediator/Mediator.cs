using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Phoenix.Mediator.Abstractions;

namespace Phoenix.Mediator.Mediator;

public sealed class Mediator(IServiceProvider serviceProvider, IOptions<MediatorOptions> options) : ISender, IMediatorOptionsAccessor
{
    // Per request-type wrapper objects. Built once per type, then dispatched via a virtual
    // call — no per-request reflection (MethodInfo.Invoke), no object[] arg allocation.
    private static readonly ConcurrentDictionary<Type, RequestHandlerWrapper> WrapperCache = new();

    // A type implementing both request interfaces is cached above under its response wrapper. A void send that falls
    // back to such a runtime type needs the void one.
    private static readonly ConcurrentDictionary<Type, RequestHandlerWrapper> VoidWrapperCache = new();

    // Resolved when the mediator is built — once per scope — rather than on first use, so two sends racing on one scope
    // can never see one of them set and the other not. A Mediator constructed by hand, outside AddMediator, has no
    // behavior order; a container other than Microsoft's may not answer IServiceProviderIsService.
    private readonly PipelineBehaviorOrder? behaviorOrder = serviceProvider.GetService<PipelineBehaviorOrder>();
    private readonly IServiceProviderIsService? isService = serviceProvider.GetService<IServiceProviderIsService>();

    public MediatorOptions Options => options.Value;

    public async Task<object?> Send(object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wrapper = WrapperCache.GetOrAdd(request.GetType(), static type => CreateWrapper(type));

        return await wrapper.Handle(this, request, cancellationToken).ConfigureAwait(false);
    }

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        // The parameter is the interface, so the request can only be dispatched by its runtime type.
        return SendByRuntimeType<TResponse>(request, cancellationToken);
    }

    public Task<TResponse> Send<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest<TResponse>
    {
        // A variable declared as IRequest<TResponse> binds here with TRequest = the interface, and no
        // handler is registered for an interface. Dispatch those by the runtime type instead of failing.
        return typeof(TRequest).IsInterface
            ? SendByRuntimeType<TResponse>(request, cancellationToken)
            : SendInternal<TRequest, TResponse>(request, cancellationToken);
    }

    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest
    {
        // Same for `IRequest command = new SomeCommand();` — common when commands are dispatched
        // polymorphically (a List<IRequest>, a factory return value, ...).
        return typeof(TRequest).IsInterface
            ? Send((object)request!, cancellationToken)
            : SendInternalVoid(request, cancellationToken);
    }

    /// <summary>
    /// Dispatches by the request's runtime type through the wrapper cache, and hands the response back typed:
    /// no boxing, unlike going through <see cref="Send(object, CancellationToken)"/>.
    /// </summary>
    private Task<TResponse> SendByRuntimeType<TResponse>(object? request, CancellationToken cancellationToken)
    {
        // A null declared as the interface has no runtime type to dispatch by. Reported as a faulted task, the way
        // Send(object) reports it, and with the parameter name callers see on every overload.
        if (request is null)
            return Task.FromException<TResponse>(new ArgumentNullException(nameof(request)));

        var requestType = request.GetType();
        var wrapper = WrapperCache.GetOrAdd(requestType, static type => CreateWrapper(type));

        // The cached wrapper answers for the first IRequest<> the type implements. A type that implements two of
        // them can be sent as either, so the other one gets a wrapper of its own; rare enough not to cache.
        var typed = wrapper as ResponseWrapper<TResponse>
            ?? (ResponseWrapper<TResponse>)Activator.CreateInstance(
                typeof(RequestResponseWrapper<,>).MakeGenericType(requestType, typeof(TResponse)))!;

        return typed.HandleTyped(this, request, cancellationToken);
    }

    private static RequestHandlerWrapper CreateWrapper(Type requestType)
    {
        var responseType = MediatorRequestTypes.GetResponseType(requestType);

        if (responseType is not null)
        {
            var wrapperType = typeof(RequestResponseWrapper<,>).MakeGenericType(requestType, responseType);
            return (RequestHandlerWrapper)Activator.CreateInstance(wrapperType)!;
        }

        if (typeof(IRequest).IsAssignableFrom(requestType))
        {
            var wrapperType = typeof(VoidRequestWrapper<>).MakeGenericType(requestType);
            return (RequestHandlerWrapper)Activator.CreateInstance(wrapperType)!;
        }

        throw new ArgumentException($"Request type '{requestType.FullName}' must implement IRequest or IRequest<TResponse>.");
    }

    private async Task<TResponse> SendInternal<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken)
        where TRequest : IRequest<TResponse>
    {
        var handler = serviceProvider.GetService<IRequestHandler<TRequest, TResponse>>();
        if (handler is null)
        {
            // Sent through a base class (an abstract record, say) that has no handler of its own, so the handler
            // is registered for the runtime type. A handler registered for the base type is found above and still
            // wins: sending through a base-typed variable keeps meaning "the base type's handler" where there is one.
            if (request is not null && request.GetType() != typeof(TRequest))
                return await SendByRuntimeType<TResponse>(request, cancellationToken).ConfigureAwait(false);

            throw MissingHandler(typeof(TRequest), typeof(IRequestHandler<TRequest, TResponse>));
        }

        var behaviors = serviceProvider.GetServices<IPipelineBehavior<TRequest, TResponse>>().ToArray();
        var requestBehaviors = GetRequestBehaviors<TRequest>();

        RequestHandlerDelegate<TResponse> next = () => handler.Handle(request, cancellationToken);

        // Wrap from the innermost behavior (the one registered last) outwards. Each array is already in
        // registration order, so only how the two interleave has to be worked out, and only when both have any.
        var pipelineIndex = behaviors.Length - 1;
        var requestIndex = requestBehaviors.Length - 1;
        int[]? pipelinePositions = null, requestPositions = null;

        if (behaviors.Length > 0 && requestBehaviors.Length > 0)
        {
            pipelinePositions = PositionsOf(typeof(IPipelineBehavior<TRequest, TResponse>), behaviors, isRequestBehavior: false);
            requestPositions = PositionsOf(typeof(IRequestBehavior<TRequest>), requestBehaviors, isRequestBehavior: true);
        }

        while (pipelineIndex >= 0 || requestIndex >= 0)
        {
            var currentNext = next;

            if (requestIndex < 0 || (pipelineIndex >= 0 && pipelinePositions![pipelineIndex] > requestPositions![requestIndex]))
            {
                var behavior = behaviors[pipelineIndex--];
                next = () => behavior.Handle(request, currentNext, cancellationToken);
            }
            else
            {
                var behavior = requestBehaviors[requestIndex--];
                next = () => behavior.Handle(request, currentNext, cancellationToken);
            }
        }

        return await next().ConfigureAwait(false);
    }

    private async Task SendInternalVoid<TRequest>(TRequest request, CancellationToken cancellationToken) where TRequest : IRequest
    {
        var handler = serviceProvider.GetService<IRequestHandler<TRequest>>();
        if (handler is null)
        {
            // See SendInternal: a base class without a handler of its own falls back to the runtime type — through the
            // void wrapper, even when the runtime type also implements IRequest<T>, because this is a void send.
            if (request is not null && request.GetType() != typeof(TRequest))
            {
                var wrapper = VoidWrapperCache.GetOrAdd(request.GetType(), static type =>
                    (RequestHandlerWrapper)Activator.CreateInstance(typeof(VoidRequestWrapper<>).MakeGenericType(type))!);

                await wrapper.Handle(this, request, cancellationToken).ConfigureAwait(false);
                return;
            }

            throw MissingHandler(typeof(TRequest), typeof(IRequestHandler<TRequest>));
        }

        var behaviors = serviceProvider.GetServices<IPipelineBehavior<TRequest>>().ToArray();
        var requestBehaviors = GetRequestBehaviors<TRequest>();

        RequestHandlerDelegate next = () => handler.Handle(request, cancellationToken);

        var pipelineIndex = behaviors.Length - 1;
        var requestIndex = requestBehaviors.Length - 1;
        int[]? pipelinePositions = null, requestPositions = null;

        if (behaviors.Length > 0 && requestBehaviors.Length > 0)
        {
            pipelinePositions = PositionsOf(typeof(IPipelineBehavior<TRequest>), behaviors, isRequestBehavior: false);
            requestPositions = PositionsOf(typeof(IRequestBehavior<TRequest>), requestBehaviors, isRequestBehavior: true);
        }

        while (pipelineIndex >= 0 || requestIndex >= 0)
        {
            var currentNext = next;

            if (requestIndex < 0 || (pipelineIndex >= 0 && pipelinePositions![pipelineIndex] > requestPositions![requestIndex]))
            {
                var behavior = behaviors[pipelineIndex--];
                next = () => behavior.Handle(request, currentNext, cancellationToken);
            }
            else
            {
                // An IRequestBehavior is written against a response; for a request without one that is NoResponse.
                var behavior = requestBehaviors[requestIndex--];
                next = () => behavior.Handle<NoResponse>(request, async () =>
                {
                    await currentNext().ConfigureAwait(false);
                    return NoResponse.Value;
                }, cancellationToken);
            }
        }

        await next().ConfigureAwait(false);
    }

    private IRequestBehavior<TRequest>[] GetRequestBehaviors<TRequest>()
    {
        // Ask the container, not the service collection, so behaviors registered natively in a third-party container
        // are seen too. When nothing is registered — true of every app written before IRequestBehavior existed — this
        // lookup is all it costs.
        if (isService is not null && !isService.IsService(typeof(IRequestBehavior<TRequest>)))
            return [];

        return serviceProvider.GetServices<IRequestBehavior<TRequest>>().ToArray();
    }

    /// <summary>
    /// Where each behavior was registered, to interleave the two behavior lists. Exact when the container returned what
    /// the service collection AddMediator was given accounts for; otherwise each behavior is placed by its type, and
    /// one that cannot be placed runs innermost. Without AddMediator there is no order to read at all, and pipeline
    /// behaviors run inside request behaviors.
    /// </summary>
    private int[] PositionsOf(Type serviceType, object[] behaviors, bool isRequestBehavior)
    {
        if (behaviorOrder is null)
            return Enumerable.Repeat(isRequestBehavior ? int.MinValue : int.MaxValue, behaviors.Length).ToArray();

        var registered = behaviorOrder.PositionsFor(serviceType);
        if (registered.Length == behaviors.Length)
            return registered;

        var positions = new int[behaviors.Length];
        for (var i = 0; i < behaviors.Length; i++)
            positions[i] = behaviorOrder.PositionOf(behaviors[i], isRequestBehavior);

        return positions;
    }

    /// <summary>
    /// The container's own message ("No service for type 'IRequestHandler`2[...]' has been registered") names neither
    /// the request in C# terms nor what to do about it. Only built on the failure path.
    /// </summary>
    private InvalidOperationException MissingHandler(Type requestType, Type handlerType)
    {
        var scanned = serviceProvider.GetService<MediatorAssemblyRegistry>()?.GetAssemblies() ?? [];
        var scannedText = scanned.Length == 0
            ? "No assemblies were passed to AddMediator(...)."
            : $"Assemblies passed to AddMediator(...): {string.Join(", ", scanned.Select(static assembly => assembly.GetName().Name))}.";

        return new InvalidOperationException(
            $"No handler is registered for request '{TypeNames.Display(requestType)}' ({requestType.Namespace}): nothing implements " +
            $"{TypeNames.Display(handlerType)}. Add a class implementing it to an assembly passed to AddMediator(...), or register " +
            $"one explicitly. {scannedText}");
    }

    // Nested types can access the enclosing Mediator's private SendInternal* methods, so the
    // boxed dispatch reuses the exact same pipeline as the strongly-typed overloads.
    private abstract class RequestHandlerWrapper
    {
        public abstract Task<object?> Handle(Mediator mediator, object request, CancellationToken cancellationToken);
    }

    /// <summary>The typed half, so a request dispatched by its runtime type returns its response without boxing.</summary>
    private abstract class ResponseWrapper<TResponse> : RequestHandlerWrapper
    {
        public abstract Task<TResponse> HandleTyped(Mediator mediator, object request, CancellationToken cancellationToken);
    }

    private sealed class RequestResponseWrapper<TRequest, TResponse> : ResponseWrapper<TResponse> where TRequest : IRequest<TResponse>
    {
        public override async Task<object?> Handle(Mediator mediator, object request, CancellationToken cancellationToken)
        {
            return await mediator.SendInternal<TRequest, TResponse>((TRequest)request, cancellationToken).ConfigureAwait(false);
        }

        public override Task<TResponse> HandleTyped(Mediator mediator, object request, CancellationToken cancellationToken)
        {
            return mediator.SendInternal<TRequest, TResponse>((TRequest)request, cancellationToken);
        }
    }

    private sealed class VoidRequestWrapper<TRequest> : RequestHandlerWrapper where TRequest : IRequest
    {
        public override async Task<object?> Handle(Mediator mediator, object request, CancellationToken cancellationToken)
        {
            await mediator.SendInternalVoid<TRequest>((TRequest)request, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }
}
