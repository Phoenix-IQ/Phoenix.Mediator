using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Exceptions;
using System.Net;

namespace Phoenix.Mediator.Mediator;

public sealed class SentryBehavior<TRequest, TResponse>(IHub? hub = null) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (hub is null)
            return await next().ConfigureAwait(false);

        var name = typeof(TRequest).Name;

        var tx = hub.StartTransaction(name, "mediator.request");
        using var _ = hub.PushScope();
        hub.ConfigureScope(scope => scope.Transaction = tx);

        try
        {
            var response = await next().ConfigureAwait(false);
            tx.Finish(SpanStatus.Ok);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            tx.Finish(SpanStatus.Cancelled);
            throw;
        }
        catch (HttpResponseException ex)
        {
            tx.Finish(SentryBehaviorStatusMapper.Map(ex.HttpStatusCode));

            if ((int)ex.HttpStatusCode >= 500)
            {
                hub.CaptureException(ex, scope =>
                {
                    scope.SetExtra("RequestType", name);
                    scope.Level = SentryLevel.Error;
                });
            }

            throw;
        }
        catch (Exception ex)
        {
            tx.Finish(SpanStatus.InternalError);

            hub.CaptureException(ex, scope =>
            {
                scope.SetExtra("RequestType", name);
                scope.Level = SentryLevel.Error;
            });

            throw;
        }
    }
}

public sealed class SentryBehavior<TRequest>(IHub? hub = null) : IPipelineBehavior<TRequest>
    where TRequest : IRequest
{
    public async Task Handle(TRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken)
    {
        if (hub is null)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var name = typeof(TRequest).Name;

        var tx = hub.StartTransaction(name, "mediator.request");
        using var _ = hub.PushScope();
        hub.ConfigureScope(scope => scope.Transaction = tx);

        try
        {
            await next().ConfigureAwait(false);
            tx.Finish(SpanStatus.Ok);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            tx.Finish(SpanStatus.Cancelled);
            throw;
        }
        catch (HttpResponseException ex)
        {
            tx.Finish(SentryBehaviorStatusMapper.Map(ex.HttpStatusCode));

            if ((int)ex.HttpStatusCode >= 500)
            {
                hub.CaptureException(ex, scope =>
                {
                    scope.SetExtra("RequestType", name);
                    scope.Level = SentryLevel.Error;
                });
            }

            throw;
        }
        catch (Exception ex)
        {
            tx.Finish(SpanStatus.InternalError);

            hub.CaptureException(ex, scope =>
            {
                scope.SetExtra("RequestType", name);
                scope.Level = SentryLevel.Error;
            });

            throw;
        }
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
