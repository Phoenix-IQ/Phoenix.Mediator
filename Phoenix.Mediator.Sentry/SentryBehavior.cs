using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Exceptions;
using Sentry;
using System.Net;

namespace Phoenix.Mediator.Sentry;

public sealed class SentryBehavior<TRequest, TResponse>(IHub? hub = null) : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        return hub is null
            ? next()
            : SentryRequestTracer.TraceAsync(hub, typeof(TRequest).Name, () => next(), cancellationToken);
    }
}

public sealed class SentryBehavior<TRequest>(IHub? hub = null) : IPipelineBehavior<TRequest> where TRequest : IRequest
{
    public Task Handle(TRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken)
    {
        if (hub is null)
            return next();

        return SentryRequestTracer.TraceAsync<object?>(hub, typeof(TRequest).Name, async () =>
        {
            await next().ConfigureAwait(false);
            return null;
        }, cancellationToken);
    }
}

/// <summary>
/// Shared Sentry tracing for mediator requests.
/// <para>
/// When a request transaction already exists on the hub (e.g. the one Sentry.AspNetCore
/// starts per HTTP request), this starts a CHILD span off it instead of a second root
/// transaction — otherwise the mediator transaction would overwrite the ambient one and
/// detach request-level context. A new root transaction (with its own scope) is only
/// created when there is no active span (e.g. background/non-HTTP usage).
/// </para>
/// </summary>
internal static class SentryRequestTracer
{
    public static async Task<TResult> TraceAsync<TResult>(IHub hub, string requestName, Func<Task<TResult>> next, CancellationToken cancellationToken)
    {
        var parent = hub.GetSpan();

        ISpan span;
        IDisposable? ownedScope = null;

        if (parent is not null)
        {
            span = parent.StartChild("mediator.request", requestName);
        }
        else
        {
            var transaction = hub.StartTransaction(requestName, "mediator.request");
            ownedScope = hub.PushScope();
            hub.ConfigureScope(scope => scope.Transaction = transaction);
            span = transaction;
        }

        try
        {
            var result = await next().ConfigureAwait(false);
            span.Finish(SpanStatus.Ok);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            span.Finish(SpanStatus.Cancelled);
            throw;
        }
        catch (HttpResponseException ex)
        {
            span.Finish(SentryBehaviorStatusMapper.Map(ex.HttpStatusCode));

            if ((int)ex.HttpStatusCode >= 500)
                CaptureException(hub, ex, requestName);

            throw;
        }
        catch (Exception ex)
        {
            span.Finish(SpanStatus.InternalError);
            CaptureException(hub, ex, requestName);
            throw;
        }
        finally
        {
            ownedScope?.Dispose();
        }
    }

    private static void CaptureException(IHub hub, Exception exception, string requestName)
    {
        hub.CaptureException(exception, scope =>
        {
            scope.SetExtra("RequestType", requestName);
            scope.Level = SentryLevel.Error;
        });
    }
}

internal static class SentryBehaviorStatusMapper
{
    public static SpanStatus Map(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.BadRequest => SpanStatus.InvalidArgument,
            HttpStatusCode.Unauthorized => SpanStatus.Unauthenticated,
            HttpStatusCode.Forbidden => SpanStatus.PermissionDenied,
            HttpStatusCode.NotFound => SpanStatus.NotFound,
            HttpStatusCode.Conflict => SpanStatus.AlreadyExists,
            HttpStatusCode.PreconditionFailed => SpanStatus.FailedPrecondition,
            HttpStatusCode.RequestTimeout => SpanStatus.DeadlineExceeded,
            HttpStatusCode.RequestedRangeNotSatisfiable => SpanStatus.OutOfRange,
            HttpStatusCode.TooManyRequests => SpanStatus.ResourceExhausted,
            HttpStatusCode.NotImplemented => SpanStatus.Unimplemented,
            HttpStatusCode.ServiceUnavailable => SpanStatus.Unavailable,
            _ => (int)statusCode >= 500 ? SpanStatus.InternalError : SpanStatus.UnknownError
        };
    }
}
