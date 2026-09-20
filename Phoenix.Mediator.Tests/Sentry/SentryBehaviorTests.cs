using System.Net;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Exceptions;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Sentry;
using Phoenix.Mediator.Validation;
using Phoenix.Mediator.Wrappers;
using Xunit;
// Sentry's own namespace is reachable unqualified (the Sentry package contributes `global using Sentry;`),
// but `Sentry.Something` written inside namespace Phoenix.Mediator.Tests binds to Phoenix.Mediator.Sentry.
// These two live in nested Sentry namespaces, so they need the global:: escape hatch.
using SentryEnvelope = global::Sentry.Protocol.Envelopes.Envelope;
using SentryMeasurement = global::Sentry.Protocol.Measurement;

namespace Phoenix.Mediator.Tests;

/// <summary>
/// Covers Phoenix.Mediator.Sentry end to end: both pipeline behaviors, the tracer they share
/// (child span vs root transaction, scope ownership and ordering, capture rules), the status mapper,
/// <c>AddMediatorSentry()</c>, and the composition with validation the README prescribes.
/// The tracer and the mapper are internal, so every assertion here is made through the public
/// behaviors against a hand-written <see cref="SentryTestHub"/>.
/// </summary>
public sealed class SentryBehaviorTests
{
    // ------------------------------------------------------------------ no hub: pure pass-through

    [Fact]
    public async Task Handle_InvokesTheHandlerAndReturnsItsTaskUnchanged_WhenNoHubIsRegistered()
    {
        // Sentry is opt-in. With no IHub the behavior must return the handler's own task, not a
        // wrapper: an app that never configured Sentry should not pay for a state machine per request.
        var handlerRan = false;
        var response = new SingleResponse<string>("ok");
        var handlerTask = CompletedTask(response);

        var task = new SentryBehavior<SentryTracedRequest, SingleResponse<string>>().Handle(
            new SentryTracedRequest(),
            () =>
            {
                handlerRan = true;
                return handlerTask;
            },
            CancellationToken.None);

        Assert.True(handlerRan);
        Assert.Same(handlerTask, task);
        Assert.Same(response, await task);
    }

    [Fact]
    public async Task Handle_InvokesTheHandlerAndReturnsItsTaskUnchanged_WhenNoHubIsRegisteredForAVoidRequest()
    {
        var handlerRan = false;
        var handlerTask = CompletedTask();

        var task = new SentryBehavior<SentryTracedVoidRequest>().Handle(
            new SentryTracedVoidRequest(),
            () =>
            {
                handlerRan = true;
                return handlerTask;
            },
            CancellationToken.None);

        Assert.True(handlerRan);
        Assert.Same(handlerTask, task);
        await task;
    }

    // ------------------------------------------------------------------ an ambient span exists

    [Fact]
    public async Task Handle_StartsAChildSpanNamedAfterTheRequest_WhenTheHubAlreadyHasAnActiveSpan()
    {
        var hub = new SentryTestHub { ActiveSpan = new SentryTestSpan("http.server") };

        await TraceAsync(hub, () => CompletedTask(new SingleResponse<string>("ok")));

        var child = Assert.Single(hub.ActiveSpan!.Children);
        Assert.Equal("mediator.request", child.Operation);
        Assert.Equal(nameof(SentryTracedRequest), child.Description);
    }

    [Fact]
    public async Task Handle_DoesNotStartATransactionOrPushAScope_WhenTheHubAlreadyHasAnActiveSpan()
    {
        // Sentry.AspNetCore already runs a transaction per HTTP request. Starting a second root
        // transaction here would overwrite the ambient one on the scope and detach request-level
        // context (URL, user, breadcrumbs) from everything the handler reports.
        var hub = new SentryTestHub { ActiveSpan = new SentryTestSpan("http.server") };

        await TraceAsync(hub, () => CompletedTask(new SingleResponse<string>("ok")));

        Assert.Empty(hub.StartedTransactions);
        Assert.Empty(hub.PushedScopes);
        Assert.Null(hub.CurrentScope.Transaction);
    }

    [Fact]
    public async Task Handle_StartsAChildSpanNamedAfterTheRequest_WhenAVoidRequestRunsUnderAnActiveSpan()
    {
        var hub = new SentryTestHub { ActiveSpan = new SentryTestSpan("http.server") };

        await TraceVoidAsync(hub, CompletedTask);

        var child = Assert.Single(hub.ActiveSpan!.Children);
        Assert.Equal("mediator.request", child.Operation);
        Assert.Equal(nameof(SentryTracedVoidRequest), child.Description);
        Assert.Empty(hub.StartedTransactions);
    }

    [Fact]
    public async Task Handle_FinishesTheChildSpanAsOk_WhenTheHandlerSucceedsUnderAnActiveSpan()
    {
        var hub = new SentryTestHub { ActiveSpan = new SentryTestSpan("http.server") };

        await TraceAsync(hub, () => CompletedTask(new SingleResponse<string>("ok")));

        var child = Assert.Single(hub.ActiveSpan!.Children);
        Assert.Equal(SpanStatus.Ok, Assert.Single(child.FinishCalls).Status);
    }

    [Fact]
    public async Task Handle_FinishesTheChildSpanAsInternalError_WhenTheHandlerThrowsUnderAnActiveSpan()
    {
        var hub = new SentryTestHub { ActiveSpan = new SentryTestSpan("http.server") };
        var failure = new SentryHandlerFailure();

        await Assert.ThrowsAsync<SentryHandlerFailure>(
            () => TraceAsync(hub, () => Task.FromException<SingleResponse<string>>(failure)));

        var child = Assert.Single(hub.ActiveSpan!.Children);
        Assert.Equal(SpanStatus.InternalError, Assert.Single(child.FinishCalls).Status);
        // The child span belongs to the ambient transaction, so there is no owned scope to dispose.
        Assert.Empty(hub.PushedScopes);
    }

    [Fact]
    public async Task Handle_LeavesTheAmbientSpanUntouched_WhenItRunsUnderAnActiveSpan()
    {
        // Sentry.AspNetCore owns that transaction for the whole HTTP request. Finishing it here would cut
        // the request's trace short at the first mediator call and lose every span after it; writing the
        // request name onto it would relabel the endpoint's transaction in the Sentry UI.
        var parent = new SentryTestSpan("http.server") { Name = "GET /orders/{id}" };
        var hub = new SentryTestHub { ActiveSpan = parent };

        await TraceAsync(hub, () => CompletedTask(new SingleResponse<string>("ok")));

        Assert.Empty(parent.FinishCalls);
        Assert.False(parent.IsFinished);
        Assert.Null(parent.Status);
        Assert.Equal("GET /orders/{id}", parent.Name);
        Assert.Equal("http.server", parent.Operation);
        Assert.Null(parent.Description);
    }

    [Fact]
    public async Task Handle_CapturesTheExceptionWithTheRequestType_WhenTheHandlerThrowsUnderAnActiveSpan()
    {
        // Capturing is independent of scope ownership. This is the path every HTTP request takes, so a
        // regression that only captured on the root-transaction branch would make handler failures
        // invisible in Sentry for exactly the deployments that matter.
        var parent = new SentryTestSpan("http.server");
        var hub = new SentryTestHub { ActiveSpan = parent };
        var failure = new SentryHandlerFailure();

        await Assert.ThrowsAsync<SentryHandlerFailure>(
            () => TraceAsync(hub, () => Task.FromException<SingleResponse<string>>(failure)));

        var captured = Assert.Single(hub.CapturedEvents);
        Assert.Same(failure, captured.Event.Exception);
        Assert.Equal(SentryLevel.Error, captured.Scope.Level);
        Assert.True(captured.Scope.Extra.TryGetValue("RequestType", out var requestType));
        Assert.Equal(nameof(SentryTracedRequest), requestType);
        Assert.Empty(parent.FinishCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, SpanStatus.NotFound, 0)]
    [InlineData(HttpStatusCode.BadRequest, SpanStatus.InvalidArgument, 0)]
    [InlineData(HttpStatusCode.InternalServerError, SpanStatus.InternalError, 1)]
    public async Task Handle_MapsTheStatusOntoTheChildSpanAndCapturesOnlyServerErrors_WhenRunningUnderAnActiveSpan(
        HttpStatusCode statusCode,
        SpanStatus expectedStatus,
        int expectedCaptures)
    {
        // Same mapping and the same noise control as the root-transaction path, but applied to the child
        // span — and without disturbing the ambient transaction the whole request is reported under.
        var parent = new SentryTestSpan("http.server");
        var hub = new SentryTestHub { ActiveSpan = parent };
        var failure = new HttpResponseException(new ErrorResponse(statusCode, ["nope"]));

        await Assert.ThrowsAsync<HttpResponseException>(
            () => TraceAsync(hub, () => Task.FromException<SingleResponse<string>>(failure)));

        var child = Assert.Single(parent.Children);
        Assert.Equal(expectedStatus, Assert.Single(child.FinishCalls).Status);
        Assert.Equal(expectedCaptures, hub.CapturedEvents.Count);
        Assert.Empty(parent.FinishCalls);
    }

    // ------------------------------------------------------------------ no ambient span: root transaction

    [Fact]
    public async Task Handle_StartsARootTransactionNamedAfterTheRequest_WhenThereIsNoActiveSpan()
    {
        // Background/non-HTTP usage (a hosted service, a queue consumer) has no ambient transaction,
        // so the mediator request itself has to become the root of the trace.
        var hub = new SentryTestHub();

        await TraceAsync(hub, () => CompletedTask(new SingleResponse<string>("ok")));

        var transaction = Assert.Single(hub.StartedTransactions);
        Assert.Equal(nameof(SentryTracedRequest), transaction.Name);
        Assert.Equal("mediator.request", transaction.Operation);
    }

    [Fact]
    public async Task Handle_PushesAScopeAndPutsTheTransactionOnIt_WhenItStartsARootTransaction()
    {
        // Without its own scope the transaction would leak into whatever scope the caller is on,
        // and events reported after the request would still be attributed to it.
        // Read from INSIDE the request on purpose: Sentry's Scope.Transaction getter reports null once
        // the transaction is finished, so asserting after the behavior returned would pass whether or
        // not the transaction was ever put on the scope. While the handler runs is also the only window
        // in which this matters — it is what attributes the handler's own events to this transaction.
        var hub = new SentryTestHub();
        global::Sentry.ITransactionTracer? transactionOnScopeDuringRequest = null;

        await TraceAsync(hub, () =>
        {
            transactionOnScopeDuringRequest = hub.CurrentScope.Transaction;
            return CompletedTask(new SingleResponse<string>("ok"));
        });

        Assert.Single(hub.PushedScopes);
        Assert.Same(Assert.Single(hub.StartedTransactions), transactionOnScopeDuringRequest);
    }

    [Fact]
    public async Task Handle_PushesItsOwnScopeBeforePuttingTheTransactionOnIt_WhenItStartsARootTransaction()
    {
        // Order, not just occurrence: configuring first would write the transaction onto the CALLER's
        // scope, which nothing here pops. That scope outlives the request, so every later event on it
        // would still carry this request's trace — and the scope this behavior pushed would be empty.
        var hub = new SentryTestHub();

        await TraceAsync(hub, () => CompletedTask(new SingleResponse<string>("ok")));

        Assert.Equal(
            new[] { "PushScope", "ConfigureScope" },
            hub.Operations.Where(static operation => operation is "PushScope" or "ConfigureScope"));
    }

    [Fact]
    public async Task Handle_DisposesThePushedScopeExactlyOnce_WhenTheRequestSucceeds()
    {
        // Exactly once: PushScope hands back a token that pops one scope off the hub's stack, so a
        // second dispose pops the caller's scope instead and silently unwinds context it does not own.
        var hub = new SentryTestHub();

        await TraceAsync(hub, () => CompletedTask(new SingleResponse<string>("ok")));

        Assert.Equal(1, Assert.Single(hub.PushedScopes).DisposeCount);
    }

    [Fact]
    public async Task Handle_DisposesThePushedScopeExactlyOnce_WhenTheRequestThrows()
    {
        // The scope is popped in a finally: a failing request must not leave the pushed scope on the
        // hub, or every later event on that thread inherits this request's transaction and extras.
        var hub = new SentryTestHub();

        await Assert.ThrowsAsync<SentryHandlerFailure>(
            () => TraceAsync(hub, () => Task.FromException<SingleResponse<string>>(new SentryHandlerFailure())));

        Assert.Equal(1, Assert.Single(hub.PushedScopes).DisposeCount);
    }

    [Fact]
    public async Task Handle_CapturesTheExceptionBeforePoppingItsScope_WhenTheRequestFails()
    {
        // The event is captured against whatever scope is current, and this request's transaction only
        // lives on the scope the behavior pushed. Capturing after the finally popped it would detach
        // every background-job error from the trace it belongs to.
        var hub = new SentryTestHub();

        await Assert.ThrowsAsync<SentryHandlerFailure>(
            () => TraceAsync(hub, () => Task.FromException<SingleResponse<string>>(new SentryHandlerFailure())));

        Assert.Equal(
            new[] { "CaptureEvent", "DisposeScope" },
            hub.Operations.Where(static operation => operation is "CaptureEvent" or "DisposeScope"));
    }

    [Fact]
    public async Task Handle_DisposesThePushedScopeExactlyOnce_WhenTheRequestIsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var hub = new SentryTestHub();

        await Assert.ThrowsAsync<OperationCanceledException>(() => TraceAsync(
            hub,
            () => Task.FromException<SingleResponse<string>>(new OperationCanceledException(cancellation.Token)),
            cancellation.Token));

        Assert.Equal(1, Assert.Single(hub.PushedScopes).DisposeCount);
    }

    [Fact]
    public async Task Handle_StartsARootTransactionNamedAfterTheRequest_WhenAVoidRequestHasNoActiveSpan()
    {
        var hub = new SentryTestHub();

        await TraceVoidAsync(hub, CompletedTask);

        var transaction = Assert.Single(hub.StartedTransactions);
        Assert.Equal(nameof(SentryTracedVoidRequest), transaction.Name);
        Assert.Equal("mediator.request", transaction.Operation);
        Assert.True(Assert.Single(hub.PushedScopes).IsDisposed);
    }

    // ------------------------------------------------------------------ success

    [Fact]
    public async Task Handle_FinishesTheTransactionAsOkExactlyOnce_WhenTheHandlerSucceeds()
    {
        var hub = new SentryTestHub();

        await TraceAsync(hub, () => CompletedTask(new SingleResponse<string>("ok")));

        var finish = Assert.Single(Assert.Single(hub.StartedTransactions).FinishCalls);
        Assert.Equal(SpanStatus.Ok, finish.Status);
        Assert.Null(finish.Exception);
    }

    [Fact]
    public async Task Handle_FinishesTheTransactionAsOk_WhenAVoidHandlerSucceeds()
    {
        var hub = new SentryTestHub();

        await TraceVoidAsync(hub, CompletedTask);

        Assert.Equal(SpanStatus.Ok, Assert.Single(Assert.Single(hub.StartedTransactions).FinishCalls).Status);
    }

    [Fact]
    public async Task Handle_ReturnsTheHandlerResponseUnchanged_WhenItTraces()
    {
        // Tracing is transparent: the traced path must hand back the handler's own response instance.
        var hub = new SentryTestHub();
        var response = new SingleResponse<string>("payload");

        var result = await TraceAsync(hub, () => CompletedTask(response));

        Assert.Same(response, result);
    }

    [Fact]
    public async Task Handle_RunsTheHandlerExactlyOnce_WhenItTraces()
    {
        var hub = new SentryTestHub();
        var calls = 0;

        await TraceAsync(hub, () =>
        {
            calls++;
            return CompletedTask(new SingleResponse<string>("ok"));
        });

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Handle_CapturesNothing_WhenTheHandlerSucceeds()
    {
        var hub = new SentryTestHub();

        await TraceAsync(hub, () => CompletedTask(new SingleResponse<string>("ok")));

        Assert.Empty(hub.CapturedEvents);
    }

    [Fact]
    public async Task Handle_LeavesTheSpanOpenUntilTheHandlerCompletes_WhenItTraces()
    {
        // The span's duration is the whole point of tracing it. Every test above hands the behavior an
        // already-completed task, which would pass just as happily against a behavior that finished the
        // span before awaiting — and then every mediator span in Sentry would read as ~0 ms.
        var hub = new SentryTestHub();
        var handlerCompletion = new TaskCompletionSource<SingleResponse<string>>();

        var trace = TraceAsync(hub, () => handlerCompletion.Task);

        var transaction = Assert.Single(hub.StartedTransactions);
        Assert.Empty(transaction.FinishCalls);
        Assert.False(trace.IsCompleted);

        handlerCompletion.SetResult(new SingleResponse<string>("ok"));
        await trace;

        Assert.Equal(SpanStatus.Ok, Assert.Single(transaction.FinishCalls).Status);
    }

    [Fact]
    public async Task Handle_LeavesTheSpanOpenUntilTheHandlerCompletes_WhenItTracesAVoidRequest()
    {
        // The void overload is the one that can regress here: it wraps the handler in its own async
        // lambda, and a missing await there would finish the span immediately AND swallow whatever the
        // handler threw afterwards, leaving a green span for a request that failed.
        var hub = new SentryTestHub();
        var handlerCompletion = new TaskCompletionSource<object?>();

        var trace = TraceVoidAsync(hub, () => handlerCompletion.Task);

        var transaction = Assert.Single(hub.StartedTransactions);
        Assert.Empty(transaction.FinishCalls);
        Assert.False(trace.IsCompleted);
        Assert.False(Assert.Single(hub.PushedScopes).IsDisposed);

        handlerCompletion.SetResult(null);
        await trace;

        Assert.Equal(SpanStatus.Ok, Assert.Single(transaction.FinishCalls).Status);
        Assert.True(Assert.Single(hub.PushedScopes).IsDisposed);
    }

    // ------------------------------------------------------------------ cancellation

    [Fact]
    public async Task Handle_FinishesTheSpanAsCancelled_WhenTheRequestIsCancelled()
    {
        // A cancelled request is not a fault: it must land as Cancelled rather than InternalError,
        // otherwise a client that walks away turns into an error-rate spike.
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var hub = new SentryTestHub();

        await Assert.ThrowsAsync<OperationCanceledException>(() => TraceAsync(
            hub,
            () => Task.FromException<SingleResponse<string>>(new OperationCanceledException(cancellation.Token)),
            cancellation.Token));

        Assert.Equal(SpanStatus.Cancelled, Assert.Single(Assert.Single(hub.StartedTransactions).FinishCalls).Status);
        // ... and a walk-away is not an issue to page anyone about either.
        Assert.Empty(hub.CapturedEvents);
    }

    [Fact]
    public async Task Handle_FinishesTheSpanAsCancelled_WhenTheHandlerThrowsTaskCanceledException()
    {
        // await-ing a cancelled Task surfaces TaskCanceledException, a subclass of
        // OperationCanceledException; the catch filter has to treat it the same way.
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var hub = new SentryTestHub();

        await Assert.ThrowsAsync<TaskCanceledException>(() => TraceAsync(
            hub,
            () => Task.FromException<SingleResponse<string>>(new TaskCanceledException()),
            cancellation.Token));

        Assert.Equal(SpanStatus.Cancelled, Assert.Single(Assert.Single(hub.StartedTransactions).FinishCalls).Status);
        Assert.Empty(hub.CapturedEvents);
    }

    [Fact]
    public async Task Handle_FinishesTheSpanAsCancelled_WhenAVoidRequestIsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var hub = new SentryTestHub();

        await Assert.ThrowsAsync<OperationCanceledException>(() => TraceVoidAsync(
            hub,
            () => Task.FromException(new OperationCanceledException(cancellation.Token)),
            cancellation.Token));

        Assert.Equal(SpanStatus.Cancelled, Assert.Single(Assert.Single(hub.StartedTransactions).FinishCalls).Status);
        Assert.Empty(hub.CapturedEvents);
    }

    [Fact]
    public async Task Handle_TreatsOperationCanceledAsAFailure_WhenTheCallersTokenWasNotCancelled()
    {
        // The catch filter only calls it a cancellation when the caller's token is actually cancelled.
        // A handler that throws OperationCanceledException for its own reasons (an internal timeout,
        // a library that misuses the type) is a real error and must still be reported.
        var hub = new SentryTestHub();
        var failure = new OperationCanceledException("the handler gave up on its own");

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => TraceAsync(hub, () => Task.FromException<SingleResponse<string>>(failure)));

        Assert.Equal(SpanStatus.InternalError, Assert.Single(Assert.Single(hub.StartedTransactions).FinishCalls).Status);
        Assert.Same(failure, Assert.Single(hub.CapturedEvents).Event.Exception);
    }

    [Fact]
    public async Task Handle_TreatsAnUnexpectedExceptionAsAFailure_WhenTheCallersTokenIsAlsoCancelled()
    {
        // The other half of the filter: it keys off the exception TYPE as well as the token. A handler
        // that fails for a real reason while the request happens to be cancelling — a disposed
        // DbContext, a dropped socket during shutdown — is still an incident, and widening the filter to
        // "anything thrown once the token is cancelled" would quietly launder those into green spans.
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var hub = new SentryTestHub();
        var failure = new SentryHandlerFailure();

        await Assert.ThrowsAsync<SentryHandlerFailure>(() => TraceAsync(
            hub,
            () => Task.FromException<SingleResponse<string>>(failure),
            cancellation.Token));

        Assert.Equal(SpanStatus.InternalError, Assert.Single(Assert.Single(hub.StartedTransactions).FinishCalls).Status);
        Assert.Same(failure, Assert.Single(hub.CapturedEvents).Event.Exception);
    }

    // ------------------------------------------------------------------ HttpResponseException mapping

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, SpanStatus.InvalidArgument)]
    [InlineData(HttpStatusCode.Unauthorized, SpanStatus.Unauthenticated)]
    [InlineData(HttpStatusCode.Forbidden, SpanStatus.PermissionDenied)]
    [InlineData(HttpStatusCode.NotFound, SpanStatus.NotFound)]
    [InlineData(HttpStatusCode.Conflict, SpanStatus.AlreadyExists)]
    [InlineData(HttpStatusCode.PreconditionFailed, SpanStatus.FailedPrecondition)]
    [InlineData(HttpStatusCode.RequestTimeout, SpanStatus.DeadlineExceeded)]
    [InlineData(HttpStatusCode.RequestedRangeNotSatisfiable, SpanStatus.OutOfRange)]
    [InlineData(HttpStatusCode.TooManyRequests, SpanStatus.ResourceExhausted)]
    [InlineData(HttpStatusCode.NotImplemented, SpanStatus.Unimplemented)]
    [InlineData(HttpStatusCode.ServiceUnavailable, SpanStatus.Unavailable)]
    // Below 400 the fallback still has to pick the non-fault status: an exception carrying a redirect is
    // odd, but reporting it as InternalError would invent an outage out of a 302.
    [InlineData(HttpStatusCode.Redirect, SpanStatus.UnknownError)]
    // 4xx without a specific mapping: still a client problem, so UnknownError rather than InternalError.
    [InlineData(HttpStatusCode.PaymentRequired, SpanStatus.UnknownError)]
    [InlineData(HttpStatusCode.UnprocessableEntity, SpanStatus.UnknownError)]
    [InlineData((HttpStatusCode)499, SpanStatus.UnknownError)]
    // 5xx without a specific mapping falls through to InternalError.
    [InlineData(HttpStatusCode.InternalServerError, SpanStatus.InternalError)]
    [InlineData(HttpStatusCode.BadGateway, SpanStatus.InternalError)]
    [InlineData(HttpStatusCode.GatewayTimeout, SpanStatus.InternalError)]
    [InlineData((HttpStatusCode)599, SpanStatus.InternalError)]
    public async Task Handle_FinishesTheSpanWithTheMappedStatus_WhenTheHandlerThrowsHttpResponseException(
        HttpStatusCode statusCode,
        SpanStatus expectedStatus)
    {
        var hub = new SentryTestHub();
        var failure = new HttpResponseException(new ErrorResponse(statusCode, ["nope"]));

        await Assert.ThrowsAsync<HttpResponseException>(
            () => TraceAsync(hub, () => Task.FromException<SingleResponse<string>>(failure)));

        Assert.Equal(expectedStatus, Assert.Single(Assert.Single(hub.StartedTransactions).FinishCalls).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Conflict, false)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData((HttpStatusCode)499, false)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.NotImplemented, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData((HttpStatusCode)599, true)]
    public async Task Handle_CapturesTheExceptionOnlyForServerSideHttpResponseExceptions(
        HttpStatusCode statusCode,
        bool expectCaptured)
    {
        // Noise control: a 404 or a validation 400 is an expected outcome, not an incident. Capturing
        // those would drown the Sentry issue stream, while dropping 5xx would hide real outages.
        var hub = new SentryTestHub();
        var failure = new HttpResponseException(new ErrorResponse(statusCode, ["nope"]));

        await Assert.ThrowsAsync<HttpResponseException>(
            () => TraceAsync(hub, () => Task.FromException<SingleResponse<string>>(failure)));

        if (expectCaptured)
            Assert.Same(failure, Assert.Single(hub.CapturedEvents).Event.Exception);
        else
            Assert.Empty(hub.CapturedEvents);
    }

    [Fact]
    public async Task Handle_FinishesTheSpanAsNotFoundWithoutCapturing_WhenTheHandlerThrowsNotFoundException()
    {
        // NotFoundException and BadRequestException are the two exception types the README tells handlers
        // to throw, and they reach the tracer as SUBCLASSES of HttpResponseException. Narrowing the catch
        // to the exact type (or ordering the generic catch first) would send every "no such order" to the
        // issue stream as an unhandled error.
        var hub = new SentryTestHub();
        var failure = new NotFoundException("no such order");

        var thrown = await Assert.ThrowsAsync<NotFoundException>(
            () => TraceAsync(hub, () => Task.FromException<SingleResponse<string>>(failure)));

        Assert.Same(failure, thrown);
        Assert.Equal(SpanStatus.NotFound, Assert.Single(Assert.Single(hub.StartedTransactions).FinishCalls).Status);
        Assert.Empty(hub.CapturedEvents);
    }

    [Fact]
    public async Task Handle_FinishesTheSpanAsInvalidArgumentWithoutCapturing_WhenTheHandlerThrowsBadRequestException()
    {
        var hub = new SentryTestHub();
        var failure = new BadRequestException("quantity must be positive");

        var thrown = await Assert.ThrowsAsync<BadRequestException>(
            () => TraceAsync(hub, () => Task.FromException<SingleResponse<string>>(failure)));

        Assert.Same(failure, thrown);
        Assert.Equal(SpanStatus.InvalidArgument, Assert.Single(Assert.Single(hub.StartedTransactions).FinishCalls).Status);
        Assert.Empty(hub.CapturedEvents);
    }

    [Fact]
    public async Task Handle_CapturesTheRequestTypeAndErrorLevel_WhenAnHttpResponseExceptionIsServerSide()
    {
        var hub = new SentryTestHub();
        var failure = new HttpResponseException(new ErrorResponse(HttpStatusCode.InternalServerError, ["boom"]));

        await Assert.ThrowsAsync<HttpResponseException>(
            () => TraceAsync(hub, () => Task.FromException<SingleResponse<string>>(failure)));

        var captured = Assert.Single(hub.CapturedEvents);
        Assert.Same(failure, captured.Event.Exception);
        Assert.Equal(SentryLevel.Error, captured.Scope.Level);
        Assert.True(captured.Scope.Extra.TryGetValue("RequestType", out var requestType));
        Assert.Equal(nameof(SentryTracedRequest), requestType);
    }

    [Fact]
    public async Task Handle_RethrowsTheHttpResponseException_AfterFinishingTheSpan()
    {
        // The exception middleware turns HttpResponseException into the HTTP response, so swallowing
        // it here would turn a 404 into a 200.
        var hub = new SentryTestHub();
        var failure = new HttpResponseException(new ErrorResponse(HttpStatusCode.NotFound, ["missing"]));

        var thrown = await Assert.ThrowsAsync<HttpResponseException>(
            () => TraceAsync(hub, () => Task.FromException<SingleResponse<string>>(failure)));

        Assert.Same(failure, thrown);
        Assert.Equal(SpanStatus.NotFound, Assert.Single(Assert.Single(hub.StartedTransactions).FinishCalls).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, SpanStatus.NotFound, false)]
    [InlineData(HttpStatusCode.BadRequest, SpanStatus.InvalidArgument, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, SpanStatus.Unavailable, true)]
    [InlineData(HttpStatusCode.InternalServerError, SpanStatus.InternalError, true)]
    public async Task Handle_MapsTheStatusAndCapturesOnlyServerErrors_ForVoidRequests(
        HttpStatusCode statusCode,
        SpanStatus expectedStatus,
        bool expectCaptured)
    {
        // The void pipeline is a separate behavior with its own call into the tracer; commands must
        // get exactly the same treatment as queries.
        var hub = new SentryTestHub();
        var failure = new HttpResponseException(new ErrorResponse(statusCode, ["nope"]));

        await Assert.ThrowsAsync<HttpResponseException>(
            () => TraceVoidAsync(hub, () => Task.FromException(failure)));

        Assert.Equal(expectedStatus, Assert.Single(Assert.Single(hub.StartedTransactions).FinishCalls).Status);

        if (expectCaptured)
            Assert.Same(failure, Assert.Single(hub.CapturedEvents).Event.Exception);
        else
            Assert.Empty(hub.CapturedEvents);
    }

    // ------------------------------------------------------------------ unexpected exceptions

    [Fact]
    public async Task Handle_FinishesTheSpanAsInternalError_WhenTheHandlerThrowsAnUnexpectedException()
    {
        var hub = new SentryTestHub();

        await Assert.ThrowsAsync<SentryHandlerFailure>(
            () => TraceAsync(hub, () => Task.FromException<SingleResponse<string>>(new SentryHandlerFailure())));

        Assert.Equal(SpanStatus.InternalError, Assert.Single(Assert.Single(hub.StartedTransactions).FinishCalls).Status);
    }

    [Fact]
    public async Task Handle_CapturesTheExceptionWithTheRequestTypeAndErrorLevel_WhenTheHandlerThrows()
    {
        // The request type is the only breadcrumb that says which handler blew up once the exception
        // has travelled up through the pipeline, so it is attached as an extra.
        var hub = new SentryTestHub();
        var failure = new SentryHandlerFailure();

        await Assert.ThrowsAsync<SentryHandlerFailure>(
            () => TraceAsync(hub, () => Task.FromException<SingleResponse<string>>(failure)));

        var captured = Assert.Single(hub.CapturedEvents);
        Assert.Same(failure, captured.Event.Exception);
        Assert.Equal(SentryLevel.Error, captured.Scope.Level);
        Assert.True(captured.Scope.Extra.TryGetValue("RequestType", out var requestType));
        Assert.Equal(nameof(SentryTracedRequest), requestType);
    }

    [Fact]
    public async Task Handle_RethrowsTheOriginalException_AfterCapturingIt()
    {
        // Capturing must not become handling: the caller still has to see the failure.
        var hub = new SentryTestHub();
        var failure = new SentryHandlerFailure();

        var thrown = await Assert.ThrowsAsync<SentryHandlerFailure>(
            () => TraceAsync(hub, () => Task.FromException<SingleResponse<string>>(failure)));

        Assert.Same(failure, thrown);
        Assert.Single(hub.CapturedEvents);
    }

    [Fact]
    public async Task Handle_TracesTheFailure_WhenTheHandlerThrowsBeforeReturningATask()
    {
        // A handler that validates its arguments throws before its first await, so the delegate throws
        // synchronously instead of returning a faulted task. That path must be inside the try as well.
        var hub = new SentryTestHub();
        var failure = new SentryHandlerFailure();

        await Assert.ThrowsAsync<SentryHandlerFailure>(
            () => TraceAsync(hub, () => throw failure));

        Assert.Equal(SpanStatus.InternalError, Assert.Single(Assert.Single(hub.StartedTransactions).FinishCalls).Status);
        Assert.Same(failure, Assert.Single(hub.CapturedEvents).Event.Exception);
        Assert.True(Assert.Single(hub.PushedScopes).IsDisposed);
    }

    [Fact]
    public async Task Handle_FinishesTheSpanAsInternalErrorAndCaptures_WhenAVoidHandlerThrows()
    {
        var hub = new SentryTestHub();
        var failure = new SentryHandlerFailure();

        var thrown = await Assert.ThrowsAsync<SentryHandlerFailure>(
            () => TraceVoidAsync(hub, () => Task.FromException(failure)));

        Assert.Same(failure, thrown);
        Assert.Equal(SpanStatus.InternalError, Assert.Single(Assert.Single(hub.StartedTransactions).FinishCalls).Status);
        var captured = Assert.Single(hub.CapturedEvents);
        Assert.Same(failure, captured.Event.Exception);
        Assert.True(captured.Scope.Extra.TryGetValue("RequestType", out var requestType));
        Assert.Equal(nameof(SentryTracedVoidRequest), requestType);
    }

    // ------------------------------------------------------------------ AddMediatorSentry

    [Fact]
    public void AddMediatorSentry_ThrowsArgumentNullException_WhenServicesIsNull()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => SentryServiceCollectionExtensions.AddMediatorSentry(null!));

        Assert.Equal("services", exception.ParamName);
    }

    [Fact]
    public void AddMediatorSentry_RegistersBothOpenGenericBehaviors()
    {
        // Registered open, so one call covers every request type; a closed registration per request
        // would mean commands (IRequest) silently go untraced.
        var services = new ServiceCollection();

        services.AddMediatorSentry();

        Assert.Single(services, descriptor =>
            descriptor.ServiceType == typeof(IPipelineBehavior<,>)
            && descriptor.ImplementationType == typeof(SentryBehavior<,>)
            && descriptor.Lifetime == ServiceLifetime.Transient);
        Assert.Single(services, descriptor =>
            descriptor.ServiceType == typeof(IPipelineBehavior<>)
            && descriptor.ImplementationType == typeof(SentryBehavior<>)
            && descriptor.Lifetime == ServiceLifetime.Transient);
    }

    [Fact]
    public void AddMediatorSentry_ResolvesOneBehaviorPerPipeline_WhenCalledTwice()
    {
        // Composition roots call it from more than one place. Asserted on what the container HANDS the
        // mediator rather than on the descriptor list, because that is where the damage would show:
        // two resolved instances mean every request is wrapped in two nested spans and every failure
        // is reported to Sentry twice.
        var services = new ServiceCollection();

        services.AddMediatorSentry();
        services.AddMediatorSentry();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.IsType<SentryBehavior<SentryTracedRequest, SingleResponse<string>>>(
            Assert.Single(scope.ServiceProvider.GetServices<IPipelineBehavior<SentryTracedRequest, SingleResponse<string>>>()));
        Assert.IsType<SentryBehavior<SentryTracedVoidRequest>>(
            Assert.Single(scope.ServiceProvider.GetServices<IPipelineBehavior<SentryTracedVoidRequest>>()));
    }

    [Fact]
    public void AddMediatorSentry_ReturnsTheSameServiceCollection()
    {
        var services = new ServiceCollection();

        Assert.Same(services, services.AddMediatorSentry());
    }

    [Fact]
    public async Task AddMediatorSentry_LeavesTheVoidPipelineWorking_WhenNoHubIsRegistered()
    {
        // AddMediatorSentry deliberately registers no IHub, so the behavior has to resolve with its
        // optional constructor argument left unfilled and then stay out of the way.
        var services = new ServiceCollection();
        services.AddMediator().AddMediatorSentry();
        services.AddTransient<IRequestHandler<SentryVoidRequest>, SentryPipelineHost<object>.VoidHandler>();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var request = new SentryVoidRequest();

        // Asserted first: "the request was handled" is also true of a container in which
        // AddMediatorSentry registered nothing at all, so on its own it would not test the resolution
        // this case is about — the behavior has to be IN the pipeline and still do nothing.
        Assert.IsType<SentryBehavior<SentryVoidRequest>>(
            Assert.Single(scope.ServiceProvider.GetServices<IPipelineBehavior<SentryVoidRequest>>()));

        await scope.ServiceProvider.GetRequiredService<ISender>().Send<SentryVoidRequest>(request);

        Assert.True(request.Handled);
    }

    [Fact]
    public async Task AddMediatorSentry_TracesTheRequest_WhenAHubIsRegistered()
    {
        var hub = new SentryTestHub();
        var services = new ServiceCollection();
        services.AddMediator().AddMediatorSentry();
        services.AddSingleton<IHub>(hub);
        services.AddTransient<IRequestHandler<SentryEchoRequest, SingleResponse<string>>, SentryPipelineHost<object>.EchoHandler>();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var response = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<SentryEchoRequest, SingleResponse<string>>(new SentryEchoRequest { Value = "x" });

        Assert.Equal("x", response.Result);
        var transaction = Assert.Single(hub.StartedTransactions);
        Assert.Equal(nameof(SentryEchoRequest), transaction.Name);
        Assert.Equal(SpanStatus.Ok, Assert.Single(transaction.FinishCalls).Status);
    }

    [Fact]
    public async Task AddMediatorSentry_TracesVoidRequests_WhenAHubIsRegistered()
    {
        var hub = new SentryTestHub();
        var services = new ServiceCollection();
        services.AddMediator().AddMediatorSentry();
        services.AddSingleton<IHub>(hub);
        services.AddTransient<IRequestHandler<SentryVoidRequest>, SentryPipelineHost<object>.VoidHandler>();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var request = new SentryVoidRequest();

        await scope.ServiceProvider.GetRequiredService<ISender>().Send<SentryVoidRequest>(request);

        Assert.True(request.Handled);
        var transaction = Assert.Single(hub.StartedTransactions);
        Assert.Equal(nameof(SentryVoidRequest), transaction.Name);
        Assert.Equal(SpanStatus.Ok, Assert.Single(transaction.FinishCalls).Status);
    }

    [Fact]
    public async Task AddMediatorSentry_NamesTheTransactionAfterTheConcreteRequest_WhenItIsDispatchedByRuntimeType()
    {
        // ISender.Send(object) — how queue consumers and dynamic dispatch send — goes through a cached
        // per-type wrapper rather than the generic overload. If that path lost the concrete type, every
        // such request would land in Sentry under one meaningless transaction name and become
        // impossible to tell apart.
        var hub = new SentryTestHub();
        var services = new ServiceCollection();
        services.AddMediator().AddMediatorSentry();
        services.AddSingleton<IHub>(hub);
        services.AddTransient<IRequestHandler<SentryEchoRequest, SingleResponse<string>>, SentryPipelineHost<object>.EchoHandler>();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var response = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send((object)new SentryEchoRequest { Value = "boxed" });

        Assert.Equal("boxed", Assert.IsType<SingleResponse<string>>(response).Result);
        Assert.Equal(nameof(SentryEchoRequest), Assert.Single(hub.StartedTransactions).Name);
    }

    // ------------------------------------------------------------------ composed with validation

    [Fact]
    public async Task AddMediatorSentry_FinishesTheSpanAsInvalidArgumentWithoutCapturing_WhenValidationFailsInsideTheSpan()
    {
        // The composition the README prescribes: Sentry registered first, so its span wraps validation.
        // That makes a validation failure a 400 HttpResponseException the tracer has to classify — and
        // the whole point of the noise control is that a rejected form post is a traced InvalidArgument
        // span, never a Sentry issue. These two behaviors are covered separately everywhere else.
        var hub = new SentryTestHub();
        var services = new ServiceCollection();
        services.AddMediator().AddMediatorSentry().AddMediatorValidation();
        services.AddSingleton<IHub>(hub);
        services.AddTransient<IValidator<SentryValidatedRequest>, SentryPipelineHost<object>.RejectingValidator>();
        services.AddTransient<IRequestHandler<SentryValidatedRequest, SingleResponse<string>>, SentryPipelineHost<object>.ValidatedHandler>();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var request = new SentryValidatedRequest();

        var thrown = await Assert.ThrowsAsync<HttpResponseException>(() => scope.ServiceProvider
            .GetRequiredService<ISender>()
            .Send<SentryValidatedRequest, SingleResponse<string>>(request));

        Assert.Equal(new[] { SentryPipelineHost<object>.RejectingValidator.Message }, thrown.Errors);
        Assert.False(request.Handled);
        var transaction = Assert.Single(hub.StartedTransactions);
        Assert.Equal(nameof(SentryValidatedRequest), transaction.Name);
        Assert.Equal(SpanStatus.InvalidArgument, Assert.Single(transaction.FinishCalls).Status);
        Assert.Empty(hub.CapturedEvents);
    }

    [Fact]
    public async Task AddMediatorSentry_StartsNoTransaction_WhenValidationIsRegisteredOutsideTheSpanAndFails()
    {
        // The mirror image, and the reason the README calls out registration order: validation first
        // means a rejected request never reaches the Sentry behavior, so it produces no span at all
        // rather than an InvalidArgument one. Pinned because it is the observable difference between
        // the two orderings — everywhere else the order is only asserted as a list of behavior types.
        var hub = new SentryTestHub();
        var services = new ServiceCollection();
        services.AddMediator().AddMediatorValidation().AddMediatorSentry();
        services.AddSingleton<IHub>(hub);
        services.AddTransient<IValidator<SentryValidatedRequest>, SentryPipelineHost<object>.RejectingValidator>();
        services.AddTransient<IRequestHandler<SentryValidatedRequest, SingleResponse<string>>, SentryPipelineHost<object>.ValidatedHandler>();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        await Assert.ThrowsAsync<HttpResponseException>(() => scope.ServiceProvider
            .GetRequiredService<ISender>()
            .Send<SentryValidatedRequest, SingleResponse<string>>(new SentryValidatedRequest()));

        Assert.Empty(hub.StartedTransactions);
        Assert.Empty(hub.CapturedEvents);
    }

    [Fact]
    public async Task AddMediatorSentry_FinishesTheSpanAsOk_WhenValidationPassesInsideTheSpan()
    {
        // The other side of the same composition: a request that passes validation must still finish Ok.
        // Without this, "no capture on a 400" could equally be produced by a span that never ran.
        var hub = new SentryTestHub();
        var services = new ServiceCollection();
        services.AddMediator().AddMediatorSentry().AddMediatorValidation();
        services.AddSingleton<IHub>(hub);
        services.AddTransient<IValidator<SentryValidatedRequest>, SentryPipelineHost<object>.RejectingValidator>();
        services.AddTransient<IRequestHandler<SentryValidatedRequest, SingleResponse<string>>, SentryPipelineHost<object>.ValidatedHandler>();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var request = new SentryValidatedRequest { Value = "valid" };

        var response = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<SentryValidatedRequest, SingleResponse<string>>(request);

        Assert.Equal("valid", response.Result);
        Assert.True(request.Handled);
        Assert.Equal(SpanStatus.Ok, Assert.Single(Assert.Single(hub.StartedTransactions).FinishCalls).Status);
        Assert.Empty(hub.CapturedEvents);
    }

    // ------------------------------------------------------------------ helpers

    private static Task<SingleResponse<string>> TraceAsync(
        SentryTestHub hub,
        RequestHandlerDelegate<SingleResponse<string>> next,
        CancellationToken cancellationToken = default)
    {
        return new SentryBehavior<SentryTracedRequest, SingleResponse<string>>(hub)
            .Handle(new SentryTracedRequest(), next, cancellationToken);
    }

    private static Task TraceVoidAsync(
        SentryTestHub hub,
        RequestHandlerDelegate next,
        CancellationToken cancellationToken = default)
    {
        return new SentryBehavior<SentryTracedVoidRequest>(hub)
            .Handle(new SentryTracedVoidRequest(), next, cancellationToken);
    }

    /// <summary>A freshly allocated completed task, so reference-identity assertions mean something.</summary>
    private static Task<T> CompletedTask<T>(T result)
    {
        var completion = new TaskCompletionSource<T>();
        completion.SetResult(result);
        return completion.Task;
    }

    private static Task CompletedTask()
    {
        return CompletedTask<object?>(null);
    }
}

// Requests used by the behavior-level tests. They deliberately have no handler: those tests invoke the
// behavior directly with a delegate, and leaving the handler out keeps the assembly-wide handler scans
// other test files run from registering anything for them.
public sealed class SentryTracedRequest : IRequest<SingleResponse<string>>
{
}

public sealed class SentryTracedVoidRequest : IRequest
{
}

public sealed class SentryEchoRequest : IRequest<SingleResponse<string>>
{
    public string Value { get; set; } = string.Empty;
}

public sealed class SentryVoidRequest : IRequest
{
    public bool Handled { get; set; }
}

public sealed class SentryValidatedRequest : IRequest<SingleResponse<string>>
{
    public string Value { get; set; } = string.Empty;

    public bool Handled { get; set; }
}

/// <summary>
/// Handlers and validators live nested in an open generic so <c>ContainsGenericParameters</c> is true and
/// the handler/validator scans every other test file triggers skip them; the DI tests register them
/// explicitly instead.
/// </summary>
public static class SentryPipelineHost<TMarker>
{
    public sealed class EchoHandler : IRequestHandler<SentryEchoRequest, SingleResponse<string>>
    {
        public Task<SingleResponse<string>> Handle(SentryEchoRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new SingleResponse<string>(request.Value));
        }
    }

    public sealed class VoidHandler : IRequestHandler<SentryVoidRequest>
    {
        public Task Handle(SentryVoidRequest request, CancellationToken cancellationToken)
        {
            request.Handled = true;
            return Task.CompletedTask;
        }
    }

    public sealed class ValidatedHandler : IRequestHandler<SentryValidatedRequest, SingleResponse<string>>
    {
        public Task<SingleResponse<string>> Handle(SentryValidatedRequest request, CancellationToken cancellationToken)
        {
            request.Handled = true;
            return Task.FromResult(new SingleResponse<string>(request.Value));
        }
    }

    /// <summary>Rejects a <see cref="SentryValidatedRequest"/> whose <c>Value</c> is empty.</summary>
    public sealed class RejectingValidator : AbstractValidator<SentryValidatedRequest>
    {
        public const string Message = "sentry-value-is-required";

        public RejectingValidator() => RuleFor(request => request.Value).NotEmpty().WithMessage(Message);
    }
}

internal sealed class SentryHandlerFailure() : Exception("Sentry test handler failed.");

/// <summary>One capture: the event Sentry would send, and the scope the behavior configured for it.</summary>
internal sealed record SentryCapturedEvent(SentryEvent Event, Scope Scope);

/// <summary>One <c>Finish</c> call on a span.</summary>
internal sealed record SentrySpanFinish(SpanStatus? Status, Exception? Exception);

/// <summary>The disposable <c>PushScope()</c> hands back, so tests can see whether it was popped.</summary>
internal sealed class SentryScopeToken(Action? onDispose = null) : IDisposable
{
    public int DisposeCount { get; private set; }

    public bool IsDisposed => DisposeCount > 0;

    public void Dispose()
    {
        DisposeCount++;
        onDispose?.Invoke();
    }
}

/// <summary>
/// A hand-written span/transaction that records what the tracer did to it. Sentry's own span types
/// need a live hub and would report into it, hiding the very calls under test.
/// </summary>
internal sealed class SentryTestSpan(string operation) : ITransactionTracer
{
    private readonly List<SentryTestSpan> children = [];
    private readonly List<SentrySpanFinish> finishCalls = [];
    private readonly Dictionary<string, object?> data = new();
    private readonly Dictionary<string, object?> extra = new();
    private readonly Dictionary<string, string> tags = new();

    public IReadOnlyList<SentryTestSpan> Children => children;

    public IReadOnlyList<SentrySpanFinish> FinishCalls => finishCalls;

    // --- the surface the tracer actually touches ---------------------------------------------

    public string Operation { get; set; } = operation;

    public string? Description { get; set; }

    public SpanStatus? Status { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsFinished { get; private set; }

    public ISpan StartChild(string childOperation)
    {
        var child = new SentryTestSpan(childOperation);
        children.Add(child);
        return child;
    }

    // Sentry treats a span finished without an explicit status as Ok, so mirror that here rather than
    // recording "no status" and failing a test over an equivalent call.
    public void Finish() => Complete(Status ?? SpanStatus.Ok, exception: null);

    public void Finish(SpanStatus status) => Complete(status, exception: null);

    public void Finish(Exception exception) => Complete(SpanStatus.InternalError, exception);

    public void Finish(Exception exception, SpanStatus status) => Complete(status, exception);

    private void Complete(SpanStatus status, Exception? exception)
    {
        Status = status;
        IsFinished = true;
        EndTimestamp = DateTimeOffset.UtcNow;
        finishCalls.Add(new SentrySpanFinish(status, exception));
    }

    // --- the rest of ITransactionTracer: inert, or unsupported where nothing should call it ----

    public IReadOnlyCollection<ISpan> Spans => children;

    public ISpan? GetLastActiveSpan() => children.LastOrDefault(static span => !span.IsFinished);

    public SpanId SpanId => default;

    public SpanId? ParentSpanId => null;

    public SentryId TraceId => default;

    public string? Origin => null;

    public bool? IsSampled => true;

    public bool? IsParentSampled { get; set; }

    public TransactionNameSource NameSource => TransactionNameSource.Custom;

    // Nullable to match ITransactionData.Platform, whose setter accepts null.
    public string? Platform { get; set; } = string.Empty;

    public DateTimeOffset StartTimestamp { get; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? EndTimestamp { get; private set; }

    public IReadOnlyDictionary<string, object?> Data => data;

    public void SetData(string key, object? value) => data[key] = value;

    public IReadOnlyDictionary<string, object?> Extra => extra;

    public void SetExtra(string key, object? value) => extra[key] = value;

    public IReadOnlyDictionary<string, string> Tags => tags;

    public void SetTag(string key, string value) => tags[key] = value;

    public void UnsetTag(string key) => tags.Remove(key);

    public IReadOnlyDictionary<string, SentryMeasurement> Measurements => new Dictionary<string, SentryMeasurement>();

    public void SetMeasurement(string name, SentryMeasurement measurement) => throw new NotSupportedException();

    public SentryTraceHeader GetTraceHeader() => throw new NotSupportedException();

    public SentryLevel? Level { get; set; }

    public string? Release { get; set; }

    public string? Distribution { get; set; }

    public string? Environment { get; set; }

    public string? TransactionName { get; set; }

    public IReadOnlyList<string> Fingerprint { get; set; } = Array.Empty<string>();

    public IReadOnlyCollection<Breadcrumb> Breadcrumbs => Array.Empty<Breadcrumb>();

    public void AddBreadcrumb(Breadcrumb breadcrumb) => throw new NotSupportedException();

    // Real (empty) instances rather than throwing accessors: the span is handed to Sentry's own Scope,
    // and a stub that threw on an incidental read would fail tests for a reason unrelated to tracing.
    public SentryRequest Request { get; set; } = new();

    public SentryContexts Contexts { get; set; } = new();

    public SentryUser User { get; set; } = new();

    public SdkVersion Sdk { get; } = new();

    public void Dispose()
    {
    }
}

/// <summary>
/// A minimal <see cref="IHub"/> that records the handful of calls the behavior makes and refuses the
/// rest, so a regression that starts talking to Sentry some other way fails loudly instead of silently.
/// </summary>
internal sealed class SentryTestHub : IHub
{
    private readonly Scope currentScope = new(new SentryOptions());

    /// <summary>The span <c>GetSpan()</c> reports, i.e. the ambient transaction an HTTP request would have.</summary>
    public SentryTestSpan? ActiveSpan { get; set; }

    public Scope CurrentScope => currentScope;

    public List<SentryTestSpan> StartedTransactions { get; } = [];

    public List<SentryScopeToken> PushedScopes { get; } = [];

    public List<SentryCapturedEvent> CapturedEvents { get; } = [];

    /// <summary>
    /// Every recorded call in the order it was made. Several of the tracer's guarantees are about
    /// ORDER — push the scope before writing the transaction onto it, capture before popping it — and
    /// a "did it happen" assertion cannot tell a correct sequence from a broken one.
    /// </summary>
    public List<string> Operations { get; } = [];

    // --- what the tracer uses ------------------------------------------------------------------

    public ISpan? GetSpan() => ActiveSpan;

    public ITransactionTracer StartTransaction(ITransactionContext context, IReadOnlyDictionary<string, object?> customSamplingContext)
    {
        var transaction = new SentryTestSpan(context.Operation) { Name = context.Name };
        StartedTransactions.Add(transaction);
        Operations.Add("StartTransaction");
        return transaction;
    }

    public IDisposable PushScope()
    {
        var token = new SentryScopeToken(() => Operations.Add("DisposeScope"));
        PushedScopes.Add(token);
        Operations.Add("PushScope");
        return token;
    }

    public void ConfigureScope(Action<Scope> configureScope)
    {
        Operations.Add("ConfigureScope");
        configureScope?.Invoke(currentScope);
    }

    public void ConfigureScope<TArg>(Action<Scope, TArg> configureScope, TArg arg)
    {
        Operations.Add("ConfigureScope");
        configureScope?.Invoke(currentScope, arg);
    }

    public SentryId CaptureEvent(SentryEvent evt, Action<Scope> configureScope) => Capture(evt, configureScope);

    public SentryId CaptureEvent(SentryEvent evt, SentryHint? hint, Action<Scope> configureScope) => Capture(evt, configureScope);

    private SentryId Capture(SentryEvent evt, Action<Scope>? configureScope)
    {
        // A fresh scope per capture: the assertions are about what the behavior configured, not about
        // whatever else happened to be on the hub's scope at the time.
        var scope = new Scope(new SentryOptions());
        configureScope?.Invoke(scope);
        CapturedEvents.Add(new SentryCapturedEvent(evt, scope));
        Operations.Add("CaptureEvent");
        return default;
    }

    public bool IsEnabled => true;

    public SentryId LastEventId => default;

    // --- everything else is outside the traced path --------------------------------------------

    public SentryStructuredLogger Logger => throw new NotSupportedException();

    public SentryMetricEmitter Metrics => throw new NotSupportedException();

    public bool IsSessionActive => throw new NotSupportedException();

    public void BindException(Exception exception, ISpan span) => throw new NotSupportedException();

    public SentryTraceHeader? GetTraceHeader() => throw new NotSupportedException();

    public BaggageHeader? GetBaggage() => throw new NotSupportedException();

    public W3CTraceparentHeader? GetTraceparentHeader() => throw new NotSupportedException();

    public TransactionContext ContinueTrace(string? traceHeader, string? baggageHeader, string? name = null, string? operation = null)
        => throw new NotSupportedException();

    public TransactionContext ContinueTrace(SentryTraceHeader? traceHeader, BaggageHeader? baggageHeader, string? name = null, string? operation = null)
        => throw new NotSupportedException();

    public void StartSession() => throw new NotSupportedException();

    public void PauseSession() => throw new NotSupportedException();

    public void ResumeSession() => throw new NotSupportedException();

    public void EndSession(SessionEndStatus status = default) => throw new NotSupportedException();

    public SentryId CaptureEvent(SentryEvent evt, Scope? scope = null, SentryHint? hint = null) => throw new NotSupportedException();

    public SentryId CaptureFeedback(SentryFeedback feedback, out CaptureFeedbackResult result, Action<Scope>? configureScope, SentryHint? hint = null)
        => throw new NotSupportedException();

    public SentryId CaptureFeedback(SentryFeedback feedback, out CaptureFeedbackResult result, Scope? scope = null, SentryHint? hint = null)
        => throw new NotSupportedException();

    public void CaptureTransaction(SentryTransaction transaction) => throw new NotSupportedException();

    public void CaptureTransaction(SentryTransaction transaction, Scope? scope, SentryHint? hint) => throw new NotSupportedException();

    public void CaptureSession(SessionUpdate sessionUpdate) => throw new NotSupportedException();

    public SentryId CaptureCheckIn(
        string monitorSlug,
        CheckInStatus status,
        SentryId? sentryId = null,
        TimeSpan? duration = null,
        Scope? scope = null,
        Action<SentryMonitorOptions>? configureMonitorOptions = null)
        => throw new NotSupportedException();

    public bool CaptureEnvelope(SentryEnvelope envelope) => throw new NotSupportedException();

    public Task FlushAsync(TimeSpan timeout) => throw new NotSupportedException();

    public void SetTag(string key, string value) => throw new NotSupportedException();

    public void UnsetTag(string key) => throw new NotSupportedException();

    public void BindClient(ISentryClient client) => throw new NotSupportedException();

    public IDisposable PushScope<TState>(TState state) => throw new NotSupportedException();

    public Task ConfigureScopeAsync(Func<Scope, Task> configureScope) => throw new NotSupportedException();

    public Task ConfigureScopeAsync<TArg>(Func<Scope, TArg, Task> configureScope, TArg arg) => throw new NotSupportedException();
}
