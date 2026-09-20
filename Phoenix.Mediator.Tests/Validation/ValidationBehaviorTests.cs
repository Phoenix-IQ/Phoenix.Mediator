using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Exceptions;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Sentry;
using Phoenix.Mediator.Tests.Infrastructure;
using Phoenix.Mediator.Validation;
using Phoenix.Mediator.Web;
using Phoenix.Mediator.Web.Middlewares;
using Phoenix.Mediator.Wrappers;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Phoenix.Mediator.Tests;

/// <summary>
/// The FluentValidation pipeline behavior — <c>ValidationBehavior&lt;TRequest, TResponse&gt;</c>,
/// <c>ValidationBehavior&lt;TRequest&gt;</c>, the <c>ValidationGuard</c> they share, and
/// <c>AddMediatorValidation(...)</c> that wires them up.
/// <para>
/// This is the layer that decides whether invalid input reaches a handler at all, so every case below is
/// either "the request was rejected as a 400 carrying the right messages" or "the request was let through
/// exactly as it would have been without validation". Anything in between is a silent data-integrity bug.
/// </para>
/// <para>
/// Validators and handlers live inside <see cref="ValHost{TMarker}"/>: types nested in an open generic
/// carry its type parameter, so the assembly scans other test files run never pick them up and never start
/// validating these requests in someone else's test.
/// </para>
/// </summary>
public sealed class ValidationBehaviorTests
{
    private const string FirstMessage = "val-first-error";
    private const string SecondMessage = "val-second-error";

    // ----------------------------------------------------------------------------------------------
    // ValidationBehavior<TRequest, TResponse> — the response pipeline, driven through the real mediator.
    // ----------------------------------------------------------------------------------------------

    // The behavior is registered as an open generic, so it sits in front of EVERY request whether or not
    // that request has a validator. With none registered it must be completely transparent.
    [Fact]
    public async Task Handle_NoValidatorsRegistered_RunsTheHandlerAndReturnsItsResponse()
    {
        var recorder = new ValRecorder();
        using var provider = CreateProvider(recorder);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var response = await sender.Send<ValRequest, SingleResponse<string>>(new ValRequest { Name = "kept" });

        Assert.Equal("kept", response.Result);
        Assert.Equal(1, recorder.Count("handler"));
    }

    [Fact]
    public async Task Handle_ValidatorWithOneFailingRule_ThrowsBadRequestCarryingTheRuleMessage()
    {
        var recorder = new ValRecorder();
        using var provider = CreateProvider(recorder, new ValHost<object>.NameRequiredValidator());
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<HttpResponseException>(() =>
            sender.Send<ValRequest, SingleResponse<string>>(new ValRequest { Name = string.Empty }));

        Assert.Equal(HttpStatusCode.BadRequest, exception.HttpStatusCode);
        Assert.Equal(new[] { ValHost<object>.NameRequiredValidator.Message }, exception.Errors);
    }

    // The whole point of validating in the pipeline: a rejected request must never touch the handler, so a
    // handler can assume its input is already valid and skip re-checking it.
    [Fact]
    public async Task Handle_ValidatorWithOneFailingRule_DoesNotRunTheHandler()
    {
        var recorder = new ValRecorder();
        using var provider = CreateProvider(recorder, new ValHost<object>.NameRequiredValidator());
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await Assert.ThrowsAsync<HttpResponseException>(() =>
            sender.Send<ValRequest, SingleResponse<string>>(new ValRequest { Name = string.Empty }));

        Assert.DoesNotContain("handler", recorder.Entries);
    }

    // A caller fixing one field at a time needs every problem in one response, not the first one found.
    [Fact]
    public async Task Handle_ValidatorWithSeveralFailingRules_ReportsEveryMessage()
    {
        var recorder = new ValRecorder();
        using var provider = CreateProvider(recorder, new ValHost<object>.TwoRuleValidator());
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<HttpResponseException>(() =>
            sender.Send<ValRequest, SingleResponse<string>>(new ValRequest()));

        Assert.Equal(2, exception.Errors.Count);
        Assert.Contains(ValHost<object>.TwoRuleValidator.NameMessage, exception.Errors);
        Assert.Contains(ValHost<object>.TwoRuleValidator.EmailMessage, exception.Errors);
    }

    // Splitting rules across several validators (a shared base-field validator plus a command-specific one)
    // must still produce ONE 400, not the first validator's failures only.
    [Fact]
    public async Task Handle_SeveralValidators_AggregatesEveryMessageIntoOneBadRequestInValidatorOrder()
    {
        var recorder = new ValRecorder();
        using var provider = CreateProvider(
            recorder,
            FailingValidator<ValRequest>(FirstMessage),
            FailingValidator<ValRequest>(SecondMessage));
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<HttpResponseException>(() =>
            sender.Send<ValRequest, SingleResponse<string>>(new ValRequest()));

        Assert.Equal(HttpStatusCode.BadRequest, exception.HttpStatusCode);
        Assert.Equal(new[] { FirstMessage, SecondMessage }, exception.Errors);
    }

    // Short-circuiting on the first failing validator would hide the rest of the problems, so the guard has
    // to keep going after it has already collected errors.
    [Fact]
    public async Task Handle_SeveralValidators_RunsEveryValidatorEvenAfterTheFirstFails()
    {
        var recorder = new ValRecorder();
        using var provider = CreateProvider(
            recorder,
            RecordingValidator<ValRequest>(recorder, "first", FirstMessage),
            RecordingValidator<ValRequest>(recorder, "second", SecondMessage));
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await Assert.ThrowsAsync<HttpResponseException>(() =>
            sender.Send<ValRequest, SingleResponse<string>>(new ValRequest()));

        Assert.Equal(new[] { "first", "second" }, recorder.Entries);
    }

    [Fact]
    public async Task Handle_ValidatorPasses_ReturnsTheHandlerResponseUnchanged()
    {
        var recorder = new ValRecorder();
        using var provider = CreateProvider(recorder, new ValHost<object>.PassingValidator());
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var response = await sender.Send<ValRequest, SingleResponse<string>>(new ValRequest { Name = "untouched" });

        Assert.Equal("untouched", response.Result);
        Assert.Equal(1, recorder.Count("handler"));
    }

    // A validator that found nothing must contribute nothing: if passing results leaked empty entries into
    // the error list, every request with a passing validator would get a 400 with blank messages.
    [Fact]
    public async Task Handle_OnePassingAndOneFailingValidator_ReportsOnlyTheRealFailure()
    {
        var recorder = new ValRecorder();
        using var provider = CreateProvider(
            recorder,
            PassingValidator<ValRequest>(),
            FailingValidator<ValRequest>(FirstMessage),
            PassingValidator<ValRequest>());
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<HttpResponseException>(() =>
            sender.Send<ValRequest, SingleResponse<string>>(new ValRequest()));

        Assert.Equal(new[] { FirstMessage }, exception.Errors);
    }

    // A fire-and-forget MustAsync would let the handler run while the rule was still deciding, so a request
    // that a database-backed rule rejects (duplicate email, missing tenant) would be processed anyway.
    [Fact]
    public async Task Handle_AsyncRule_IsAwaitedBeforeTheRequestIsRejected()
    {
        var recorder = new ValRecorder();
        using var provider = CreateProvider(recorder, new ValHost<object>.AsyncRuleValidator(recorder));
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<HttpResponseException>(() =>
            sender.Send<ValRequest, SingleResponse<string>>(new ValRequest { Name = "anything" }));

        Assert.Equal(new[] { ValHost<object>.AsyncRuleValidator.Message }, exception.Errors);
        Assert.Equal(new[] { "async-rule" }, recorder.Entries);
    }

    // Rules that hit the database or another service must be cancellable with the request; a token that stops
    // at the behavior leaves those calls running after the client has gone.
    [Fact]
    public async Task Handle_CancellationToken_ReachesTheValidator()
    {
        var recorder = new ValRecorder();
        var captured = CancellationToken.None;
        var validator = new ValHost<object>.StubValidator<ValRequest>((_, token) =>
        {
            captured = token;
            return Task.FromResult(new ValidationResult());
        });

        using var provider = CreateProvider(recorder, validator);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        using var cancellation = new CancellationTokenSource();

        await sender.Send<ValRequest, SingleResponse<string>>(new ValRequest(), cancellation.Token);

        Assert.Equal(cancellation.Token, captured);
    }

    // A rule that blows up (a null dependency, a broken query) is a server fault, not a caller mistake:
    // turning it into a 400 would tell the client their input was wrong and hide the real failure.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handle_ValidatorThrowsNonValidationException_PropagatesUnchanged(bool throwsSynchronously)
    {
        var recorder = new ValRecorder();
        var boom = new InvalidOperationException("val-validator-boom");
        var validator = new ValHost<object>.StubValidator<ValRequest>((_, _) => throwsSynchronously
            ? throw boom
            : Task.FromException<ValidationResult>(boom));

        using var provider = CreateProvider(recorder, validator);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sender.Send<ValRequest, SingleResponse<string>>(new ValRequest()));

        Assert.Same(boom, thrown);
        Assert.DoesNotContain("handler", recorder.Entries);
    }

    // Real validators inject repositories and current-user accessors. If the container could not build them,
    // the first request that used one would fail with "A suitable constructor could not be located".
    [Fact]
    public async Task Handle_ValidatorWithConstructorDependency_IsResolvedFromDependencyInjection()
    {
        var recorder = new ValRecorder();
        using var provider = CreateProvider(recorder, services =>
        {
            services.AddSingleton(new ValHost<object>.MessageSource("val-message-from-di"));
            services.AddScoped<IValidator<ValRequest>, ValHost<object>.DependencyValidator>();
        });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<HttpResponseException>(() =>
            sender.Send<ValRequest, SingleResponse<string>>(new ValRequest()));

        Assert.Equal(new[] { "val-message-from-di" }, exception.Errors);
    }

    // Validators are looked up per request type. A validator for another request must not bleed into this one,
    // or a rule written for one command would start rejecting unrelated traffic. The other request is then sent
    // from the SAME container: without that, a registration that silently did nothing would make the first half
    // of this test pass for the wrong reason.
    [Fact]
    public async Task Handle_ValidatorForAnotherRequestType_IsNotRunForThisRequestButStillRunsForItsOwn()
    {
        var recorder = new ValRecorder();
        using var provider = CreateProvider(recorder, services =>
            services.AddSingleton(FailingValidator<ValVoidRequest>("val-other-request-error")));
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var response = await sender.Send<ValRequest, SingleResponse<string>>(new ValRequest { Name = "allowed" });

        Assert.Equal("allowed", response.Result);
        Assert.Equal(1, recorder.Count("handler"));

        var exception = await Assert.ThrowsAsync<HttpResponseException>(() =>
            sender.Send<ValVoidRequest>(new ValVoidRequest()));

        Assert.Equal(new[] { "val-other-request-error" }, exception.Errors);
        Assert.Equal(1, recorder.Count("handler"));
    }

    // Rules read the request's own fields, so the behavior has to hand the validator the instance the caller
    // sent rather than a copy or a default. A copy would also silently discard anything a validator normalized.
    [Fact]
    public async Task Handle_Validator_ReceivesTheRequestInstanceTheCallerSent()
    {
        var recorder = new ValRecorder();
        ValRequest? captured = null;
        var validator = new ValHost<object>.StubValidator<ValRequest>((request, _) =>
        {
            captured = request;
            return Task.FromResult(new ValidationResult());
        });
        var sent = new ValRequest { Name = "val-instance" };

        using var provider = CreateProvider(recorder, validator);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await sender.Send<ValRequest, SingleResponse<string>>(sent);

        Assert.Same(sent, captured);
    }

    // SendAsApiResult(sender, request) — what every generated endpoint calls — goes through the boxed
    // Send(object) overload, not the generic one. If validation only ran on the generic path, every HTTP
    // endpoint in an app would accept invalid input while the unit tests kept passing.
    [Fact]
    public async Task Send_BoxedRequest_IsValidatedTheSameAsTheGenericOverload()
    {
        var recorder = new ValRecorder();
        using var provider = CreateProvider(recorder, new ValHost<object>.NameRequiredValidator());
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<HttpResponseException>(() =>
            sender.Send((object)new ValRequest { Name = string.Empty }));

        Assert.Equal(HttpStatusCode.BadRequest, exception.HttpStatusCode);
        Assert.Equal(new[] { ValHost<object>.NameRequiredValidator.Message }, exception.Errors);
        Assert.DoesNotContain("handler", recorder.Entries);
    }

    [Fact]
    public async Task Send_BoxedVoidRequest_IsValidatedTheSameAsTheGenericOverload()
    {
        var recorder = new ValRecorder();
        using var provider = CreateVoidProvider(recorder, FailingValidator<ValVoidRequest>(FirstMessage));
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<HttpResponseException>(() =>
            sender.Send((object)new ValVoidRequest()));

        Assert.Equal(new[] { FirstMessage }, exception.Errors);
        Assert.DoesNotContain("handler", recorder.Entries);
    }

    // A request held in a variable typed as the INTERFACE (a factory return value, an item from a
    // List&lt;IRequest&lt;T&gt;&gt;) binds TRequest to the interface, which the mediator redirects to the runtime
    // type. That redirect must land back in the same pipeline, or polymorphic dispatch would skip validation.
    [Fact]
    public async Task Send_InterfaceTypedRequest_IsStillValidated()
    {
        var recorder = new ValRecorder();
        using var provider = CreateProvider(recorder, new ValHost<object>.NameRequiredValidator());
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        IRequest<SingleResponse<string>> request = new ValRequest { Name = string.Empty };

        var exception = await Assert.ThrowsAsync<HttpResponseException>(() =>
            sender.Send<IRequest<SingleResponse<string>>, SingleResponse<string>>(request));

        Assert.Equal(new[] { ValHost<object>.NameRequiredValidator.Message }, exception.Errors);
        Assert.DoesNotContain("handler", recorder.Entries);
    }

    // The reason the scan registers validators as Scoped: a real validator injects a DbContext or a current-user
    // accessor. That only works if the behavior consuming it is NOT a singleton — a captive dependency throws
    // "Cannot consume scoped service" the first time the request is sent, under scope validation.
    [Fact]
    public async Task Handle_ValidatorWithScopedDependency_IsResolvedUnderScopeValidation()
    {
        var recorder = new ValRecorder();
        using var provider = CreateProvider(recorder, services =>
        {
            services.AddScoped(static _ => new ValHost<object>.MessageSource("val-scoped-message"));
            services.AddScoped<IValidator<ValRequest>, ValHost<object>.DependencyValidator>();
        });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<HttpResponseException>(() =>
            sender.Send<ValRequest, SingleResponse<string>>(new ValRequest()));

        Assert.Equal(new[] { "val-scoped-message" }, exception.Errors);
    }

    // The two documented ways to register validators are additive, not exclusive: an app that scans an assembly
    // and also registers one by hand must get both, not whichever route ran last.
    [Fact]
    public async Task Handle_ScannedAndHandRegisteredValidators_BothRunForTheSameRequest()
    {
        var recorder = new ValRecorder();
        using var provider = CreateProvider(recorder, services =>
        {
            services.AddMediatorValidation(ScannedRequestValidators());
            services.AddSingleton(FailingValidator<ValRequest>(FirstMessage));
        });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<HttpResponseException>(() =>
            sender.Send<ValRequest, SingleResponse<string>>(new ValRequest()));

        Assert.Equal(new[] { ValHost<object>.ScannedValidator.Message, FirstMessage }, exception.Errors);
    }

    // The exception message is what lands in logs and in Sentry, so it has to name every failure rather than
    // just saying "BadRequest".
    [Fact]
    public async Task Handle_ValidationFailure_ExceptionMessageJoinsEveryError()
    {
        var recorder = new ValRecorder();
        using var provider = CreateProvider(
            recorder,
            FailingValidator<ValRequest>(FirstMessage),
            FailingValidator<ValRequest>(SecondMessage));
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<HttpResponseException>(() =>
            sender.Send<ValRequest, SingleResponse<string>>(new ValRequest()));

        Assert.Contains(FirstMessage, exception.Message);
        Assert.Contains(SecondMessage, exception.Message);
    }

    // The ErrorResponse is what the middleware and ToApiResult render from; if the status or the messages were
    // lost here, the HTTP response would not match the failure.
    [Fact]
    public async Task Handle_ValidationFailure_ErrorResponseCarriesBadRequestAndEveryMessage()
    {
        var recorder = new ValRecorder();
        using var provider = CreateProvider(
            recorder,
            FailingValidator<ValRequest>(FirstMessage),
            FailingValidator<ValRequest>(SecondMessage));
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<HttpResponseException>(() =>
            sender.Send<ValRequest, SingleResponse<string>>(new ValRequest()));

        Assert.Equal(HttpStatusCode.BadRequest, exception.ErrorResponse.HttpStatusCode);
        Assert.Equal(new[] { FirstMessage, SecondMessage }, exception.ErrorResponse.Errors);
    }

    // ----------------------------------------------------------------------------------------------
    // ValidationBehavior<TRequest> — the void pipeline. It is a separate type with its own copy of the
    // call into the guard, so every case above has to be proved for it independently.
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Handle_VoidRequestWithNoValidators_RunsTheHandler()
    {
        var recorder = new ValRecorder();
        using var provider = CreateProvider(recorder);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await sender.Send<ValVoidRequest>(new ValVoidRequest("anything"));

        Assert.Equal(1, recorder.Count("handler"));
    }

    [Fact]
    public async Task Handle_VoidRequestWithPassingValidator_RunsTheHandler()
    {
        var recorder = new ValRecorder();
        using var provider = CreateVoidProvider(recorder, PassingValidator<ValVoidRequest>());
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await sender.Send<ValVoidRequest>(new ValVoidRequest("anything"));

        Assert.Equal(1, recorder.Count("handler"));
    }

    [Fact]
    public async Task Handle_VoidRequestWithFailingValidator_ThrowsBadRequestAndSkipsTheHandler()
    {
        var recorder = new ValRecorder();
        using var provider = CreateVoidProvider(recorder, FailingValidator<ValVoidRequest>(FirstMessage));
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<HttpResponseException>(() =>
            sender.Send<ValVoidRequest>(new ValVoidRequest()));

        Assert.Equal(HttpStatusCode.BadRequest, exception.HttpStatusCode);
        Assert.Equal(new[] { FirstMessage }, exception.Errors);
        Assert.DoesNotContain("handler", recorder.Entries);
    }

    // A command declared as a record rather than a class must be validated the same way — the behavior binds
    // on IRequest, not on how the request type was written.
    [Fact]
    public async Task Handle_VoidRequestWithFailingRule_CarriesTheRuleMessage()
    {
        var recorder = new ValRecorder();
        using var provider = CreateVoidProvider(recorder, new ValHost<object>.VoidNameRequiredValidator());
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<HttpResponseException>(() =>
            sender.Send<ValVoidRequest>(new ValVoidRequest(string.Empty)));

        Assert.Equal(new[] { ValHost<object>.VoidNameRequiredValidator.Message }, exception.Errors);
    }

    [Fact]
    public async Task Handle_VoidRequestWithSeveralValidators_AggregatesEveryMessageInValidatorOrder()
    {
        var recorder = new ValRecorder();
        using var provider = CreateVoidProvider(
            recorder,
            FailingValidator<ValVoidRequest>(FirstMessage),
            PassingValidator<ValVoidRequest>(),
            FailingValidator<ValVoidRequest>(SecondMessage));
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<HttpResponseException>(() =>
            sender.Send<ValVoidRequest>(new ValVoidRequest()));

        Assert.Equal(new[] { FirstMessage, SecondMessage }, exception.Errors);
    }

    [Fact]
    public async Task Handle_VoidRequestCancellationToken_ReachesTheValidator()
    {
        var recorder = new ValRecorder();
        var captured = CancellationToken.None;
        var validator = new ValHost<object>.StubValidator<ValVoidRequest>((_, token) =>
        {
            captured = token;
            return Task.FromResult(new ValidationResult());
        });

        using var provider = CreateVoidProvider(recorder, validator);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        using var cancellation = new CancellationTokenSource();

        await sender.Send<ValVoidRequest>(new ValVoidRequest(), cancellation.Token);

        Assert.Equal(cancellation.Token, captured);
    }

    [Fact]
    public async Task Handle_VoidRequestValidatorThrows_PropagatesUnchanged()
    {
        var recorder = new ValRecorder();
        var boom = new InvalidOperationException("val-void-validator-boom");
        var validator = new ValHost<object>.StubValidator<ValVoidRequest>((_, _) => throw boom);

        using var provider = CreateVoidProvider(recorder, validator);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sender.Send<ValVoidRequest>(new ValVoidRequest()));

        Assert.Same(boom, thrown);
        Assert.DoesNotContain("handler", recorder.Entries);
    }

    // ----------------------------------------------------------------------------------------------
    // The behaviors used directly, with no container in the way: this is where "next is called exactly
    // once" and "next is never called" can be observed precisely.
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Handle_BehaviorWithNoValidators_InvokesNextExactlyOnceAndReturnsItsValue()
    {
        var behavior = new ValidationBehavior<ValRequest, SingleResponse<string>>(Array.Empty<IValidator<ValRequest>>());
        var response = new SingleResponse<string>("from-next");
        var calls = 0;

        var actual = await behavior.Handle(new ValRequest(), () =>
        {
            calls++;
            return Task.FromResult(response);
        }, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Same(response, actual);
    }

    [Fact]
    public async Task Handle_BehaviorWithFailingValidator_DoesNotInvokeNext()
    {
        var behavior = new ValidationBehavior<ValRequest, SingleResponse<string>>(
            new[] { FailingValidator<ValRequest>(FirstMessage) });
        var calls = 0;

        await Assert.ThrowsAsync<HttpResponseException>(() => behavior.Handle(new ValRequest(), () =>
        {
            calls++;
            return Task.FromResult(new SingleResponse<string>("unreachable"));
        }, CancellationToken.None));

        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public async Task Handle_BehaviorWithSeveralFailingValidators_ReportsOneMessagePerValidatorInOrder(int validatorCount)
    {
        var messages = Enumerable.Range(0, validatorCount).Select(static index => $"val-error-{index}").ToArray();
        var behavior = new ValidationBehavior<ValRequest, SingleResponse<string>>(
            messages.Select(static message => FailingValidator<ValRequest>(message)).ToArray());

        var exception = await Assert.ThrowsAsync<HttpResponseException>(() => behavior.Handle(
            new ValRequest(),
            static () => Task.FromResult(new SingleResponse<string>("unreachable")),
            CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadRequest, exception.HttpStatusCode);
        Assert.Equal(messages, exception.Errors);
    }

    [Fact]
    public async Task Handle_VoidBehaviorWithNoValidators_InvokesNextExactlyOnce()
    {
        var behavior = new ValidationBehavior<ValVoidRequest>(Array.Empty<IValidator<ValVoidRequest>>());
        var calls = 0;

        await behavior.Handle(new ValVoidRequest(), () =>
        {
            calls++;
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Handle_VoidBehaviorWithFailingValidator_DoesNotInvokeNext()
    {
        var behavior = new ValidationBehavior<ValVoidRequest>(
            new[] { FailingValidator<ValVoidRequest>(FirstMessage) });
        var calls = 0;

        await Assert.ThrowsAsync<HttpResponseException>(() => behavior.Handle(new ValVoidRequest(), () =>
        {
            calls++;
            return Task.CompletedTask;
        }, CancellationToken.None));

        Assert.Equal(0, calls);
    }

    // The behavior must take the async path: a validator whose synchronous Validate is reachable would run
    // async rules synchronously and FluentValidation throws when that happens.
    [Fact]
    public async Task Handle_BehaviorWithValidator_NeverCallsTheSynchronousValidateOverload()
    {
        // The stub's synchronous Validate throws NotSupportedException; reaching the assertions proves it was
        // never called.
        var behavior = new ValidationBehavior<ValRequest, SingleResponse<string>>(
            new[] { PassingValidator<ValRequest>() });

        var actual = await behavior.Handle(
            new ValRequest(),
            static () => Task.FromResult(new SingleResponse<string>("ok")),
            CancellationToken.None);

        Assert.Equal("ok", actual.Result);
    }

    // Validation sits in front of every request, so it also sits in front of every failure a handler raises.
    // A catch here that rebuilt the exception would turn a handler's 404 (or 409, or 401) into "bad request"
    // and tell the caller to fix input that was never the problem.
    [Fact]
    public async Task Handle_NextThrowsHttpResponseException_PropagatesItUntouched()
    {
        var behavior = new ValidationBehavior<ValRequest, SingleResponse<string>>(
            new[] { PassingValidator<ValRequest>() });
        var notFound = new HttpResponseException(new ErrorResponse(HttpStatusCode.NotFound, new[] { "val-missing" }));

        var thrown = await Assert.ThrowsAsync<HttpResponseException>(() => behavior.Handle(
            new ValRequest(),
            () => Task.FromException<SingleResponse<string>>(notFound),
            CancellationToken.None));

        Assert.Same(notFound, thrown);
        Assert.Equal(HttpStatusCode.NotFound, thrown.HttpStatusCode);
    }

    // A validator that observes the cancellation token and gives up is reporting "the caller went away", not
    // "your input is wrong". Reporting it as a 400 would log a client mistake for every abandoned request and
    // stop the request-timeout middleware from seeing the cancellation it is waiting for.
    [Fact]
    public async Task Handle_ValidatorObservesCancellation_ThrowsCancellationRatherThanBadRequest()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        IValidator<ValRequest> validator = new ValHost<object>.StubValidator<ValRequest>((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new ValidationResult());
        });
        var behavior = new ValidationBehavior<ValRequest, SingleResponse<string>>(new[] { validator });
        var calls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => behavior.Handle(new ValRequest(), () =>
        {
            calls++;
            return Task.FromResult(new SingleResponse<string>("unreachable"));
        }, cancellation.Token));

        Assert.Equal(0, calls);
    }

    // The behavior is registered as transient, but nothing stops a host from pooling or reusing an instance.
    // Caching the first verdict on the instance would let the second, invalid request straight through.
    [Fact]
    public async Task Handle_BehaviorInstanceReused_ValidatesEveryCall()
    {
        var runs = 0;
        IValidator<ValRequest> validator = new ValHost<object>.StubValidator<ValRequest>((_, _) =>
        {
            runs++;
            return Task.FromResult(new ValidationResult());
        });
        var behavior = new ValidationBehavior<ValRequest, SingleResponse<string>>(new[] { validator });

        await behavior.Handle(new ValRequest(), Next, CancellationToken.None);
        await behavior.Handle(new ValRequest(), Next, CancellationToken.None);

        Assert.Equal(2, runs);

        static Task<SingleResponse<string>> Next() => Task.FromResult(new SingleResponse<string>("ok"));
    }

    // ----------------------------------------------------------------------------------------------
    // AddMediatorValidation
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void AddMediatorValidation_NullServices_Throws()
    {
        // Fully qualified: .NET 10 ships its own Microsoft.Extensions.DependencyInjection.ValidationServiceCollectionExtensions,
        // so the short name is ambiguous on that target framework.
        var exception = Assert.Throws<ArgumentNullException>(() =>
            global::Phoenix.Mediator.Validation.ValidationServiceCollectionExtensions.AddMediatorValidation(null!));

        Assert.Equal("services", exception.ParamName);
    }

    [Fact]
    public void AddMediatorValidation_NullAssemblies_Throws()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            services.AddMediatorValidation((Assembly[])null!));

        Assert.Equal("assemblies", exception.ParamName);
    }

    // Both branches of the method return: the early one that only registers the behaviors, and the one that
    // also runs the assembly scan. Composition roots chain off the result, so either returning something else
    // breaks the chain at compile time for consumers and silently reconfigures a different collection here.
    [Fact]
    public void AddMediatorValidation_WithAndWithoutAssemblies_ReturnsTheSameServiceCollection()
    {
        var services = new ServiceCollection();

        Assert.Same(services, services.AddMediatorValidation());
        Assert.Same(services, services.AddMediatorValidation(ScannedRequestValidators()));
    }

    // Documented as a supported shape: the behavior alone, for an app that registers its validators by hand.
    [Fact]
    public void AddMediatorValidation_WithoutAssemblies_RegistersBothBehaviorsAndNoValidators()
    {
        var services = new ServiceCollection();

        services.AddMediator().AddMediatorValidation();

        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IPipelineBehavior<,>)
            && descriptor.ImplementationType == typeof(ValidationBehavior<,>));
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IPipelineBehavior<>)
            && descriptor.ImplementationType == typeof(ValidationBehavior<>));
        Assert.DoesNotContain(services, static descriptor => descriptor.ServiceType.IsGenericType
            && descriptor.ServiceType.GetGenericTypeDefinition() == typeof(IValidator<>));
    }

    // Composition roots call the package's Add* methods from more than one place. A second behavior copy would
    // run every validator twice and report every message twice.
    [Fact]
    public void AddMediatorValidation_CalledTwice_RegistersOneBehaviorPair()
    {
        var services = new ServiceCollection();

        services.AddMediator().AddMediatorValidation();
        services.AddMediatorValidation();

        Assert.Single(services, static descriptor => descriptor.ImplementationType == typeof(ValidationBehavior<,>));
        Assert.Single(services, static descriptor => descriptor.ImplementationType == typeof(ValidationBehavior<>));
    }

    // FluentValidation's scan uses plain Add, not TryAdd, so without the assembly registry a repeated call
    // would register every validator a second time and duplicate every message in the 400.
    [Fact]
    public void AddMediatorValidation_CalledTwiceWithTheSameAssembly_RegistersItsValidatorsOnce()
    {
        var services = new ServiceCollection();
        var assembly = ScannedRequestValidators();

        services.AddMediatorValidation(assembly);
        services.AddMediatorValidation(assembly);

        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IValidator<ValRequest>));
    }

    [Fact]
    public void AddMediatorValidation_SameAssemblyRepeatedInOneCall_RegistersItsValidatorsOnce()
    {
        var services = new ServiceCollection();
        var assembly = ScannedRequestValidators();

        services.AddMediatorValidation(assembly, assembly);

        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IValidator<ValRequest>));
    }

    [Fact]
    public void AddMediatorValidation_SeveralAssemblies_RegistersValidatorsFromEach()
    {
        var services = new ServiceCollection();

        services.AddMediatorValidation(ScannedRequestValidators(), ScannedVoidRequestValidators());

        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IValidator<ValRequest>));
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IValidator<ValVoidRequest>));
    }

    // The registry must only skip assemblies it has already scanned — a later call adding a new assembly
    // (a plugin module, a second bounded context) still has to register its validators.
    [Fact]
    public void AddMediatorValidation_LaterCallWithANewAssembly_RegistersTheNewValidators()
    {
        var services = new ServiceCollection();

        services.AddMediatorValidation(ScannedRequestValidators());
        services.AddMediatorValidation(ScannedVoidRequestValidators());

        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IValidator<ValRequest>));
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IValidator<ValVoidRequest>));
    }

    // Scoped is FluentValidation's own default for an assembly scan, which is what lets a validator depend on
    // a DbContext. Registering them as singletons instead would capture scoped dependencies.
    [Fact]
    public void AddMediatorValidation_ScannedValidator_UsesFluentValidationsScopedLifetime()
    {
        var services = new ServiceCollection();

        services.AddMediatorValidation(ScannedRequestValidators());

        var descriptor = Assert.Single(services, static d => d.ServiceType == typeof(IValidator<ValRequest>));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        Assert.Same(typeof(ValHost<object>.ScannedValidator), descriptor.ImplementationType);
    }

    // End to end: what the scan registered is what the behavior resolves and runs.
    [Fact]
    public async Task AddMediatorValidation_ScannedValidator_IsRunByTheBehavior()
    {
        var recorder = new ValRecorder();
        using var provider = CreateProvider(recorder, services =>
            services.AddMediatorValidation(ScannedRequestValidators()));
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<HttpResponseException>(() =>
            sender.Send<ValRequest, SingleResponse<string>>(new ValRequest { Name = "anything" }));

        Assert.Equal(new[] { ValHost<object>.ScannedValidator.Message }, exception.Errors);
        Assert.DoesNotContain("handler", recorder.Entries);
    }

    // Transient, not singleton: the behavior consumes IEnumerable&lt;IValidator&lt;T&gt;&gt;, and the scan
    // registers those as Scoped. A singleton behavior would capture the first scope's validators, which is
    // either a scope-validation failure at startup or a validator holding a disposed DbContext in production.
    [Fact]
    public void AddMediatorValidation_RegistersBothBehaviorsAsTransient()
    {
        var services = new ServiceCollection();

        services.AddMediator().AddMediatorValidation();

        Assert.Equal(
            ServiceLifetime.Transient,
            Assert.Single(services, static descriptor => descriptor.ImplementationType == typeof(ValidationBehavior<,>)).Lifetime);
        Assert.Equal(
            ServiceLifetime.Transient,
            Assert.Single(services, static descriptor => descriptor.ImplementationType == typeof(ValidationBehavior<>)).Lifetime);
    }

    // Assemblies are often collected into an array by the composition root, and an entry can come back null
    // (a plugin that failed to load, a typeof(...) on a conditionally compiled marker). Handing that straight
    // to FluentValidation's scan would crash the whole app at startup over one missing module.
    [Fact]
    public void AddMediatorValidation_NullAssemblyAmongTheAssemblies_IsSkippedAndTheRestAreScanned()
    {
        var services = new ServiceCollection();
        var assemblies = new Assembly[] { null!, ScannedRequestValidators() };

        services.AddMediatorValidation(assemblies);

        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IValidator<ValRequest>));
    }

    // AddMediatorValidation(assembly) and AddMediator(assembly) scan for different things. Registering handlers
    // here too would quietly make `AddMediatorValidation(assembly)` alone look like a working setup, right up
    // until an app that only called AddMediator() with different assemblies lost its handlers.
    [Fact]
    public void AddMediatorValidation_AssemblyAlsoHoldingHandlers_RegistersValidatorsOnly()
    {
        var services = new ServiceCollection();
        var assembly = new FakeAssembly(
            "Phoenix.Mediator.Tests.ValHandlersAndValidators",
            typeof(ValHost<object>.ScannedValidator),
            typeof(ValHost<object>.RequestHandler));

        services.AddMediatorValidation(assembly);

        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IValidator<ValRequest>));
        Assert.DoesNotContain(services, static descriptor =>
            descriptor.ServiceType == typeof(IRequestHandler<ValRequest, SingleResponse<string>>));
    }

    // ----------------------------------------------------------------------------------------------
    // Behavior ordering against Sentry. Registration order IS the documented knob for whether the Sentry
    // span wraps validation, so both directions have to hold.
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void AddMediatorSentry_RegisteredBeforeValidation_PutsSentryOutsideValidation()
    {
        var services = new ServiceCollection();
        services.AddMediator().AddMediatorSentry().AddMediatorValidation();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.Collection(
            scope.ServiceProvider.GetServices<IPipelineBehavior<ValRequest, SingleResponse<string>>>(),
            behavior => Assert.IsType<SentryBehavior<ValRequest, SingleResponse<string>>>(behavior),
            behavior => Assert.IsType<ValidationBehavior<ValRequest, SingleResponse<string>>>(behavior));
    }

    [Fact]
    public void AddMediatorValidation_RegisteredBeforeSentry_PutsSentryInsideValidation()
    {
        var services = new ServiceCollection();
        services.AddMediator().AddMediatorValidation().AddMediatorSentry();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.Collection(
            scope.ServiceProvider.GetServices<IPipelineBehavior<ValRequest, SingleResponse<string>>>(),
            behavior => Assert.IsType<ValidationBehavior<ValRequest, SingleResponse<string>>>(behavior),
            behavior => Assert.IsType<SentryBehavior<ValRequest, SingleResponse<string>>>(behavior));
    }

    [Fact]
    public void AddMediatorSentry_RegisteredBeforeValidation_OrdersTheVoidBehaviorsTheSameWay()
    {
        var services = new ServiceCollection();
        services.AddMediator().AddMediatorSentry().AddMediatorValidation();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.Collection(
            scope.ServiceProvider.GetServices<IPipelineBehavior<ValVoidRequest>>(),
            behavior => Assert.IsType<SentryBehavior<ValVoidRequest>>(behavior),
            behavior => Assert.IsType<ValidationBehavior<ValVoidRequest>>(behavior));
    }

    [Fact]
    public void AddMediatorValidation_RegisteredBeforeSentry_OrdersTheVoidBehaviorsTheSameWay()
    {
        var services = new ServiceCollection();
        services.AddMediator().AddMediatorValidation().AddMediatorSentry();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.Collection(
            scope.ServiceProvider.GetServices<IPipelineBehavior<ValVoidRequest>>(),
            behavior => Assert.IsType<ValidationBehavior<ValVoidRequest>>(behavior),
            behavior => Assert.IsType<SentryBehavior<ValVoidRequest>>(behavior));
    }

    // The four cases above read the registration order off the container. These two prove what that order
    // actually buys, with a behavior of this file's own standing in for Sentry's: registered FIRST it wraps
    // validation, so it still observes the 400 and can trace/log it. That is the whole point of the documented
    // "register before AddMediatorValidation" advice — reversing the order loses every validation failure.
    [Fact]
    public async Task Handle_BehaviorRegisteredBeforeValidation_ObservesTheValidationFailure()
    {
        var recorder = new ValRecorder();
        using var provider = CreateProviderWithRecordingBehavior(recorder, registerBehaviorFirst: true);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await Assert.ThrowsAsync<HttpResponseException>(() =>
            sender.Send<ValRequest, SingleResponse<string>>(new ValRequest()));

        Assert.Equal(new[] { "outer-entered", "outer-saw-failure" }, recorder.Entries);
    }

    [Fact]
    public async Task Handle_BehaviorRegisteredAfterValidation_NeverRunsForARejectedRequest()
    {
        var recorder = new ValRecorder();
        using var provider = CreateProviderWithRecordingBehavior(recorder, registerBehaviorFirst: false);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await Assert.ThrowsAsync<HttpResponseException>(() =>
            sender.Send<ValRequest, SingleResponse<string>>(new ValRequest()));

        Assert.Empty(recorder.Entries);
    }

    // ----------------------------------------------------------------------------------------------
    // How the failure actually reaches the client. The exception is only useful if the two renderers turn
    // it into the documented { "errors": [...] } 400 body.
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ToApiResult_ValidationErrorResponse_WritesTheDocumentedErrorsBody()
    {
        var exception = await CaptureValidationFailure(FirstMessage, SecondMessage);

        var executed = await ResultExecution.ExecuteAsync(exception.ErrorResponse.ToApiResult());

        Assert.Equal(400, executed.StatusCode);
        Assert.Contains("application/json", executed.ContentType!);
        Assert.Equal(
            new[] { FirstMessage, SecondMessage },
            executed.Json.GetProperty("errors").EnumerateArray().Select(static error => error.GetString()!).ToArray());
    }

    [Fact]
    public async Task ExceptionHandlingMiddleware_ValidationFailure_Writes400WithEveryMessage()
    {
        var recorder = new ValRecorder();
        using var provider = CreateProvider(
            recorder,
            FailingValidator<ValRequest>(FirstMessage),
            FailingValidator<ValRequest>(SecondMessage));
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Response.Body = new MemoryStream();

        RequestDelegate next = async _ =>
        {
            await sender.Send<ValRequest, SingleResponse<string>>(new ValRequest());
        };

        var middleware = new ExceptionHandlingMiddleware(
            next,
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            new ConfigurationBuilder().Build());

        await middleware.InvokeAsync(context);

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        var errors = document.RootElement.GetProperty("errors").EnumerateArray()
            .Select(static error => error.GetString()!)
            .ToArray();

        Assert.Equal(new[] { FirstMessage, SecondMessage }, errors);
        Assert.True(document.RootElement.TryGetProperty("traceId", out _));
    }

    // ----------------------------------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------------------------------

    private static async Task<HttpResponseException> CaptureValidationFailure(params string[] messages)
    {
        var behavior = new ValidationBehavior<ValRequest, SingleResponse<string>>(
            messages.Select(static message => FailingValidator<ValRequest>(message)).ToArray());

        return await Assert.ThrowsAsync<HttpResponseException>(() => behavior.Handle(
            new ValRequest(),
            static () => Task.FromResult(new SingleResponse<string>("unreachable")),
            CancellationToken.None));
    }

    /// <summary>
    /// A fake assembly holding exactly one closed validator. The real test assembly only ever exposes the
    /// OPEN <c>ValHost&lt;&gt;</c> nested types, which FluentValidation's scan skips, so handing the closed
    /// type to a fake assembly is the only way to exercise the scan without registering it globally.
    /// </summary>
    private static Assembly ScannedRequestValidators()
        => new FakeAssembly("Phoenix.Mediator.Tests.ValScanned", typeof(ValHost<object>.ScannedValidator));

    private static Assembly ScannedVoidRequestValidators()
        => new FakeAssembly("Phoenix.Mediator.Tests.ValScannedVoid", typeof(ValHost<object>.ScannedVoidValidator));

    private static IValidator<TRequest> FailingValidator<TRequest>(params string[] messages)
        => new ValHost<object>.StubValidator<TRequest>((_, _) => Task.FromResult(Invalid(messages)));

    private static IValidator<TRequest> PassingValidator<TRequest>()
        => new ValHost<object>.StubValidator<TRequest>((_, _) => Task.FromResult(new ValidationResult()));

    private static IValidator<TRequest> RecordingValidator<TRequest>(ValRecorder recorder, string id, params string[] messages)
        => new ValHost<object>.StubValidator<TRequest>((_, _) =>
        {
            recorder.Record(id);
            return Task.FromResult(Invalid(messages));
        });

    private static ValidationResult Invalid(params string[] messages)
        => new(messages.Select(static message => new ValidationFailure("Name", message)));

    private static ServiceProvider CreateProvider(ValRecorder recorder, params IValidator<ValRequest>[] validators)
        => CreateProvider(recorder, services =>
        {
            foreach (var validator in validators)
                services.AddSingleton(validator);
        });

    private static ServiceProvider CreateVoidProvider(ValRecorder recorder, params IValidator<ValVoidRequest>[] validators)
        => CreateProvider(recorder, services =>
        {
            foreach (var validator in validators)
                services.AddSingleton(validator);
        });

    /// <summary>
    /// Mediator + the validation behavior pair + both handlers, with no assembly scanned, so a test controls
    /// exactly which validators exist for its request.
    /// </summary>
    private static ServiceProvider CreateProvider(ValRecorder recorder, Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        services.AddMediator();
        // The handlers live inside the generic host so no other test's assembly scan sees them; register the
        // closed versions by hand.
        services.AddTransient<IRequestHandler<ValRequest, SingleResponse<string>>, ValHost<object>.RequestHandler>();
        services.AddTransient<IRequestHandler<ValVoidRequest>, ValHost<object>.VoidRequestHandler>();
        services.AddMediatorValidation();
        configure(services);

        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>
    /// The same pipeline plus <see cref="ValRecordingBehavior{TRequest,TResponse}"/>, registered either side of
    /// <c>AddMediatorValidation()</c>, and a validator that always fails.
    /// </summary>
    private static ServiceProvider CreateProviderWithRecordingBehavior(ValRecorder recorder, bool registerBehaviorFirst)
    {
        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        services.AddMediator();
        services.AddTransient<IRequestHandler<ValRequest, SingleResponse<string>>, ValHost<object>.RequestHandler>();

        if (registerBehaviorFirst)
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValRecordingBehavior<,>));

        services.AddMediatorValidation();

        if (!registerBehaviorFirst)
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValRecordingBehavior<,>));

        services.AddSingleton(FailingValidator<ValRequest>(FirstMessage));

        return services.BuildServiceProvider(validateScopes: true);
    }
}

/// <summary>
/// Stands in for a tracing/logging behavior (Sentry's, an app's own) so a test can see whether it wrapped
/// validation or sat inside it. Open generic on purpose: the container closes it per request type, which is
/// exactly how the shipped behaviors are registered.
/// </summary>
public sealed class ValRecordingBehavior<TRequest, TResponse>(ValRecorder recorder) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        recorder.Record("outer-entered");

        try
        {
            return await next();
        }
        catch (HttpResponseException)
        {
            recorder.Record("outer-saw-failure");
            throw;
        }
    }
}

public sealed class ValRequest : IRequest<SingleResponse<string>>
{
    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;
}

// A record on purpose: validation must not care whether the request was written as a class or a record.
public sealed record ValVoidRequest(string Name = "") : IRequest;

/// <summary>Ordered log of what ran, so a test can prove a handler or validator was (or was not) reached.</summary>
public sealed class ValRecorder
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

    public void Record(string entry)
    {
        lock (entries)
            entries.Add(entry);
    }

    public int Count(string entry) => Enumerable.Count(Entries, recorded => recorded == entry);
}

/// <summary>
/// Handlers, validators and test doubles, nested inside an open generic so they carry its type parameter.
/// That keeps <c>ContainsGenericParameters</c> true for everything in here, so the handler scan, the
/// FluentValidation scan and endpoint discovery all skip them, while the tests can still use the closed
/// <c>ValHost&lt;object&gt;.X</c> versions.
/// </summary>
public static class ValHost<TMarker>
{
    public sealed class RequestHandler(ValRecorder recorder) : IRequestHandler<ValRequest, SingleResponse<string>>
    {
        public Task<SingleResponse<string>> Handle(ValRequest request, CancellationToken cancellationToken)
        {
            recorder.Record("handler");
            return Task.FromResult(new SingleResponse<string>(request.Name));
        }
    }

    public sealed class VoidRequestHandler(ValRecorder recorder) : IRequestHandler<ValVoidRequest>
    {
        public Task Handle(ValVoidRequest request, CancellationToken cancellationToken)
        {
            recorder.Record("handler");
            return Task.CompletedTask;
        }
    }

    public sealed class NameRequiredValidator : AbstractValidator<ValRequest>
    {
        public const string Message = "val-name-is-required";

        public NameRequiredValidator() => RuleFor(request => request.Name).NotEmpty().WithMessage(Message);
    }

    public sealed class TwoRuleValidator : AbstractValidator<ValRequest>
    {
        public const string NameMessage = "val-name-rule-failed";
        public const string EmailMessage = "val-email-rule-failed";

        public TwoRuleValidator()
        {
            RuleFor(request => request.Name).NotEmpty().WithMessage(NameMessage);
            RuleFor(request => request.Email).NotEmpty().WithMessage(EmailMessage);
        }
    }

    public sealed class PassingValidator : AbstractValidator<ValRequest>
    {
        public PassingValidator() => RuleFor(request => request.Name).NotNull();
    }

    public sealed class VoidNameRequiredValidator : AbstractValidator<ValVoidRequest>
    {
        public const string Message = "val-void-name-is-required";

        public VoidNameRequiredValidator() => RuleFor(request => request.Name).NotEmpty().WithMessage(Message);
    }

    /// <summary>A rule that only finishes after an await, so a caller that does not await it sees no failure.</summary>
    public sealed class AsyncRuleValidator : AbstractValidator<ValRequest>
    {
        public const string Message = "val-async-rule-failed";

        public AsyncRuleValidator(ValRecorder recorder)
        {
            RuleFor(request => request.Name)
                .MustAsync(async (string _, CancellationToken token) =>
                {
                    await Task.Yield();
                    recorder.Record("async-rule");
                    return false;
                })
                .WithMessage(Message);
        }
    }

    public sealed class MessageSource(string message)
    {
        public string Message => message;
    }

    public sealed class DependencyValidator : AbstractValidator<ValRequest>
    {
        public DependencyValidator(MessageSource source)
            => RuleFor(request => request.Name).Must((string _) => false).WithMessage(source.Message);
    }

    /// <summary>Public parameterless constructor on purpose: the assembly-scan tests hand this to a scan.</summary>
    public sealed class ScannedValidator : AbstractValidator<ValRequest>
    {
        public const string Message = "val-scanned-rule-failed";

        public ScannedValidator() => RuleFor(request => request.Name).Must((string _) => false).WithMessage(Message);
    }

    public sealed class ScannedVoidValidator : AbstractValidator<ValVoidRequest>
    {
        public const string Message = "val-scanned-void-rule-failed";

        public ScannedVoidValidator() => RuleFor(request => request.Name).Must((string _) => false).WithMessage(Message);
    }

    /// <summary>
    /// A validator whose result, timing and thrown exceptions a test controls outright.
    /// <para>
    /// The synchronous entry point throws: the pipeline must always take the async path, because a validator
    /// with async rules invoked synchronously makes FluentValidation throw at runtime instead of validating.
    /// </para>
    /// </summary>
    public sealed class StubValidator<TRequest> : AbstractValidator<TRequest>
    {
        private readonly Func<TRequest, CancellationToken, Task<ValidationResult>> validate;

        public StubValidator(Func<TRequest, CancellationToken, Task<ValidationResult>> validate)
            => this.validate = validate;

        public override Task<ValidationResult> ValidateAsync(ValidationContext<TRequest> context, CancellationToken cancellation = default)
            => validate(context.InstanceToValidate, cancellation);

        public override ValidationResult Validate(ValidationContext<TRequest> context)
            => throw new NotSupportedException("The validation pipeline must validate asynchronously.");
    }
}
