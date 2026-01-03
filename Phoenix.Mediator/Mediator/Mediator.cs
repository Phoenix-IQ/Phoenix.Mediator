using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Wrappers;
using Serilog;

namespace Phoenix.Mediator.Mediator;

public sealed class Mediator(IServiceProvider serviceProvider) : ISender
{
    private static readonly MethodInfo SendBoxedMethod =
        typeof(Mediator).GetMethod(nameof(SendBoxed), BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing SendBoxed method.");

    private static readonly MethodInfo SendVoidBoxedMethod =
        typeof(Mediator).GetMethod(nameof(SendVoidBoxed), BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing SendVoidBoxed method.");

    public async Task<object?> Send(object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var genericIRequest = requestType
            .GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>));

        if (genericIRequest is not null)
        {
            var responseType = genericIRequest.GetGenericArguments()[0];
            var mi = SendBoxedMethod.MakeGenericMethod(requestType, responseType);
            return await ((Task<object?>)mi.Invoke(this, new object[] { request, cancellationToken })!).ConfigureAwait(false);
        }

        if (request is IRequest)
        {
            var mi = SendVoidBoxedMethod.MakeGenericMethod(requestType);
            return await ((Task<object?>)mi.Invoke(this, new object[] { request, cancellationToken })!).ConfigureAwait(false);
        }

        throw new ArgumentException($"Request type '{requestType.FullName}' must implement IRequest or IRequest<TResponse>.", nameof(request));
    }

    private async Task<object?> SendBoxed<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken) where TRequest : IRequest<TResponse>
    {
        try
        {
            var response = await SendInternal<TRequest, TResponse>(request, cancellationToken).ConfigureAwait(false);
            EnsureOkMetadata(response);
            return response;
        }
        catch (RequestValidationException vex)
        {
            return vex.Errors;
        }
        catch (Exception ex)
        {
            Log.Error(ex,"exception {ex}");
            return new ErrorsResponse(["Unkown error occured"]);
        }
    }

    private async Task<object?> SendVoidBoxed<TRequest>(TRequest request, CancellationToken cancellationToken) where TRequest : IRequest
    {
        try
        {
            await SendInternalVoid(request, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (RequestValidationException vex)
        {
            return vex.Errors;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "exception {ex}");
            return new ErrorsResponse(["Unkown error occured"]);
        }
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

    private static void EnsureOkMetadata(object? response)
    {
        if (response is null) return;

        if (response is ErrorsResponse)
            return;

        // Set StatusCode/Message when present (SingleResponse/MultiResponse).
        var t = response.GetType();

        var statusCodeProp = t.GetProperty("StatusCode");
        if (statusCodeProp?.CanWrite == true)
        {
            var current = statusCodeProp.GetValue(response);
            if (current is int i && i == 0)
                statusCodeProp.SetValue(response, 200);
        }

        var messageProp = t.GetProperty("Message");
        if (messageProp?.CanWrite == true)
        {
            var current = messageProp.GetValue(response);
            if (current is null)
                messageProp.SetValue(response, "ok");
        }
    }
}


