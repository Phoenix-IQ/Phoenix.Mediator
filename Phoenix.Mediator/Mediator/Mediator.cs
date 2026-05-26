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

    public MediatorOptions Options => options.Value;

    public async Task<object?> Send(object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wrapper = WrapperCache.GetOrAdd(request.GetType(), static type => CreateWrapper(type));

        return await wrapper.Handle(this, request, cancellationToken).ConfigureAwait(false);
    }

    public Task<TResponse> Send<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest<TResponse>
    {
        return SendInternal<TRequest, TResponse>(request, cancellationToken);
    }

    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest
    {
        return SendInternalVoid(request, cancellationToken);
    }

    private static RequestHandlerWrapper CreateWrapper(Type requestType)
    {
        var genericIRequest = requestType
            .GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>));

        if (genericIRequest is not null)
        {
            var responseType = genericIRequest.GetGenericArguments()[0];
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
        var handler = serviceProvider.GetRequiredService<IRequestHandler<TRequest, TResponse>>();
        var behaviors = serviceProvider.GetServices<IPipelineBehavior<TRequest, TResponse>>().ToArray();

        RequestHandlerDelegate<TResponse> next = () => handler.Handle(request, cancellationToken);

        for (var i = behaviors.Length - 1; i >= 0; i--)
        {
            var behavior = behaviors[i];
            var currentNext = next;
            next = () => behavior.Handle(request, currentNext, cancellationToken);
        }

        return await next().ConfigureAwait(false);
    }

    private async Task SendInternalVoid<TRequest>(TRequest request, CancellationToken cancellationToken) where TRequest : IRequest
    {
        var handler = serviceProvider.GetRequiredService<IRequestHandler<TRequest>>();
        var behaviors = serviceProvider.GetServices<IPipelineBehavior<TRequest>>().ToArray();

        RequestHandlerDelegate next = () => handler.Handle(request, cancellationToken);

        for (var i = behaviors.Length - 1; i >= 0; i--)
        {
            var behavior = behaviors[i];
            var currentNext = next;
            next = () => behavior.Handle(request, currentNext, cancellationToken);
        }

        await next().ConfigureAwait(false);
    }

    // Nested types can access the enclosing Mediator's private SendInternal* methods, so the
    // boxed dispatch reuses the exact same pipeline as the strongly-typed overloads.
    private abstract class RequestHandlerWrapper
    {
        public abstract Task<object?> Handle(Mediator mediator, object request, CancellationToken cancellationToken);
    }

    private sealed class RequestResponseWrapper<TRequest, TResponse> : RequestHandlerWrapper where TRequest : IRequest<TResponse>
    {
        public override async Task<object?> Handle(Mediator mediator, object request, CancellationToken cancellationToken)
        {
            return await mediator.SendInternal<TRequest, TResponse>((TRequest)request, cancellationToken).ConfigureAwait(false);
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


