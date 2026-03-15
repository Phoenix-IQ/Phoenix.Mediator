using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Phoenix.Mediator.Abstractions;

namespace Phoenix.Mediator.Mediator;

public sealed class Mediator(IServiceProvider serviceProvider) : ISender
{
    private static readonly MethodInfo SendBoxedMethod =
        typeof(Mediator).GetMethod(nameof(SendBoxed), BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing SendBoxed method.");

    private static readonly MethodInfo SendVoidBoxedMethod =
        typeof(Mediator).GetMethod(nameof(SendVoidBoxed), BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing SendVoidBoxed method.");

    private static readonly ConcurrentDictionary<Type, MethodInfo> MethodCache = new();

    public async Task<object?> Send(object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();

        var mi = MethodCache.GetOrAdd(requestType, static type =>
        {
            var genericIRequest = type
                .GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>));

            if (genericIRequest is not null)
            {
                var responseType = genericIRequest.GetGenericArguments()[0];
                return SendBoxedMethod.MakeGenericMethod(type, responseType);
            }

            if (typeof(IRequest).IsAssignableFrom(type))
            {
                return SendVoidBoxedMethod.MakeGenericMethod(type);
            }

            throw new ArgumentException($"Request type '{type.FullName}' must implement IRequest or IRequest<TResponse>.");
        });

        return await ((Task<object?>)mi.Invoke(this, [request, cancellationToken])!).ConfigureAwait(false);
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

    private async Task<object?> SendBoxed<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken) where TRequest : IRequest<TResponse>
    {
        return await SendInternal<TRequest, TResponse>(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object?> SendVoidBoxed<TRequest>(TRequest request, CancellationToken cancellationToken) where TRequest : IRequest
    {
        await SendInternalVoid(request, cancellationToken).ConfigureAwait(false);
        return null;
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
}


