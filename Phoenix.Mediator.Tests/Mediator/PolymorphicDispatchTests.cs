using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Tests.Infrastructure;
using Phoenix.Mediator.Web;
using Phoenix.Mediator.Wrappers;
using Xunit;

namespace Phoenix.Mediator.Tests;

/// <summary>
/// Sending a request through a variable whose static type is not the request's own type — an abstract base record,
/// an interface — and the response-type inference that <c>Send&lt;TResponse&gt;(IRequest&lt;TResponse&gt;)</c> adds.
/// <para>
/// Every type here is prefixed <c>Poly</c> and every handler is dependency-free: the whole assembly is scanned by other
/// tests, and each request below has exactly one handler, so those scans stay valid.
/// </para>
/// </summary>
public sealed class PolymorphicDispatchTests
{
    // ---------------------------------------------------------------------------------------------
    // A base class with no handler of its own: the handler is registered for the runtime type.
    // ---------------------------------------------------------------------------------------------

    // The regression: finding 9 was fixed for interfaces only, so a command declared as its abstract base record still
    // looked up IRequestHandler<PolyCommand> and failed.
    [Fact]
    public async Task SendVoid_ThroughAnAbstractBaseRecord_ReachesTheRuntimeTypesHandler()
    {
        using var scope = new SendScope();
        var probe = new SendProbe();
        PolyCommand command = new PolyDeleteCommand(probe);

        await scope.Sender.Send(command);

        Assert.Equal(new[] { "delete" }, probe.Entries);
    }

    [Fact]
    public async Task SendTyped_ThroughAnAbstractBaseRecord_ReachesTheRuntimeTypesHandler()
    {
        using var scope = new SendScope();
        PolyQuery query = new PolyGetQuery(7);

        var response = await scope.Sender.Send<PolyQuery, string>(query);

        Assert.Equal("get 7", response);
    }

    // What an endpoint that accepts several commands of one family does. SendAsApiResult binds to its void overload
    // with the base type, which used to fail the same way.
    [Fact]
    public async Task SendAsApiResult_ThroughAnAbstractBaseRecord_ReturnsTheEmptyResponseStatus()
    {
        using var scope = new SendScope();
        var probe = new SendProbe();
        PolyCommand command = new PolyDeleteCommand(probe);

        var result = await scope.Sender.SendAsApiResult(command);

        Assert.Equal(StatusCodes.Status204NoContent, ResultExecution.StatusCode(result));
        Assert.Equal(new[] { "delete" }, probe.Entries);
    }

    // The usual reason to hold commands as their base type: a batch of different commands, sent one by one.
    [Fact]
    public async Task Send_EachCommandOfAListDeclaredAsTheBase_ReachesItsOwnHandler()
    {
        using var scope = new SendScope();
        var probe = new SendProbe();
        List<PolyCommand> commands = [new PolyDeleteCommand(probe), new PolyArchiveCommand(probe), new PolyDeleteCommand(probe)];

        foreach (var command in commands)
            await scope.Sender.Send(command);

        Assert.Equal(new[] { "delete", "archive", "delete" }, probe.Entries);
    }

    // The fallback is only for a base type that has no handler. When the base type has one, sending through it keeps
    // running that handler: SenderApiTests pins the response case, this is the void one.
    [Fact]
    public async Task SendVoid_ThroughABaseTypeWithItsOwnHandler_StillRunsTheBaseHandler()
    {
        using var scope = new SendScope();
        var probe = new SendProbe();
        PolyHandledBaseCommand command = new PolyHandledDerivedCommand(probe);

        await scope.Sender.Send(command);

        Assert.Equal(new[] { "base handler: PolyHandledDerivedCommand" }, probe.Entries);
    }

    // A void send stays a void send when it falls back to the runtime type, even if that type also implements
    // IRequest<T> — whose wrapper is the one the shared cache holds for it.
    [Fact]
    public async Task SendVoid_ThroughABaseRecord_ReachesTheVoidHandlerOfARuntimeTypeThatAlsoHasAResponse()
    {
        using var scope = new SendScope();
        var probe = new SendProbe();
        PolyNotifyingCommand command = new PolyNotifyAndCountCommand(probe);

        await scope.Sender.Send(command);

        Assert.Equal(new[] { "void handler" }, probe.Entries);
    }

    // What the README says about a base class with a handler of its own, pinned as it is: the base class's handler runs
    // when the base class is the type argument; Send(query) without type arguments goes by the runtime type, and a
    // subclass without a handler of its own fails there (SenderApiTests explains why that is deliberate).
    [Fact]
    public async Task Send_ThroughABaseWithItsOwnHandler_RunsItOnlyWhenTheBaseIsTheTypeArgument()
    {
        using var scope = new SendScope();
        SendBaseRequest request = new SendDerivedRequest();

        var explicitBase = await scope.Sender.Send<SendBaseRequest, SingleResponse<string>>(request);
        var inferred = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Sender.Send(request));

        Assert.Equal(nameof(SendDerivedRequest), explicitBase.Result);
        Assert.Contains(nameof(SendDerivedRequest), inferred.Message);
    }

    // No handler for the base type, none for the runtime type either: the error names the runtime type, which is the
    // one that needs a handler.
    [Fact]
    public async Task Send_ThroughABaseWhoseRuntimeTypeHasNoHandlerEither_NamesTheRuntimeType()
    {
        using var scope = new SendScope();
        PolyCommand command = new PolyOrphanCommand();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Sender.Send(command));

        Assert.Contains($"'{nameof(PolyOrphanCommand)}'", exception.Message);
        Assert.Contains($"IRequestHandler<{nameof(PolyOrphanCommand)}>", exception.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // The missing-handler message.
    // ---------------------------------------------------------------------------------------------

    // The container's own message named IRequestHandler`2[...] and nothing to do about it. This one names the handler
    // interface the way it is written in C#, says where to put it, and lists what AddMediator scanned.
    [Fact]
    public async Task Send_WithoutAHandler_SaysWhatIsMissingInCSharpTermsAndWhatWasScanned()
    {
        using var scope = new SendScope();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Sender.Send(new PolyUnhandledQuery()));

        Assert.StartsWith("No handler is registered for request 'PolyUnhandledQuery' (Phoenix.Mediator.Tests)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("nothing implements IRequestHandler<PolyUnhandledQuery, String>", exception.Message);
        Assert.Contains("AddMediator(...)", exception.Message);
        Assert.Contains(typeof(PolymorphicDispatchTests).Assembly.GetName().Name!, exception.Message);
    }

    [Fact]
    public async Task Send_WithoutAHandlerAndWithoutScannedAssemblies_SaysNoAssembliesWerePassed()
    {
        var services = new ServiceCollection();
        services.AddMediator();
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sender.Send(new PolyUnhandledQuery()));

        Assert.Contains("No assemblies were passed to AddMediator(...)", exception.Message);
    }

    // A handler that exists but cannot be built is a different problem, and the container's own message — naming the
    // missing dependency — is the useful one there. Only an unregistered handler gets the new message.
    [Fact]
    public async Task Send_WithAHandlerWhoseDependencyIsMissing_KeepsTheContainersMessage()
    {
        using var scope = new SendScope(services => services.AddTransient<IRequestHandler<PolyDependentQuery, string>, PolyHost<object>.DependentHandler>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Sender.Send(new PolyDependentQuery()));

        Assert.Contains(nameof(PolyMissingDependency), exception.Message);
        Assert.DoesNotContain("No handler is registered", exception.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // Send<TResponse>(IRequest<TResponse>): the response type inferred from the request.
    // ---------------------------------------------------------------------------------------------

    // The local's declared type is the assertion: without the inferring overload this call binds to Send(object).
    [Fact]
    public async Task Send_WithoutTypeArguments_ReturnsAValueTypeResponseTyped()
    {
        using var scope = new SendScope();

        int count = await scope.Sender.Send(new PolyCountQuery(3));

        Assert.Equal(3, count);
    }

    [Fact]
    public async Task Send_WithoutTypeArguments_DispatchesARequestDeclaredAsTheInterface()
    {
        using var scope = new SendScope();
        IRequest<string> query = new PolyGetQuery(4);

        string response = await scope.Sender.Send(query);

        Assert.Equal("get 4", response);
    }

    [Fact]
    public async Task Send_WithoutTypeArguments_DispatchesARequestDeclaredAsItsAbstractBase()
    {
        using var scope = new SendScope();
        PolyQuery query = new PolyGetQuery(5);

        string response = await scope.Sender.Send(query);

        Assert.Equal("get 5", response);
    }

    // The wrapper cache keeps one wrapper per request type, built for the first IRequest<> it finds. A type that
    // implements two can be sent as either, so the other must not be cast to the wrong wrapper.
    [Fact]
    public async Task Send_ARequestImplementingTwoResponseTypes_ReachesTheHandlerForTheOneItWasSentAs()
    {
        using var scope = new SendScope();
        var query = new PolyTwoFacedQuery();

        string asText = await scope.Sender.Send((IRequest<string>)query);
        int asNumber = await scope.Sender.Send((IRequest<int>)query);

        Assert.Equal("text", asText);
        Assert.Equal(42, asNumber);
    }

    [Fact]
    public async Task Send_WithoutTypeArguments_ThrowsArgumentNullExceptionForANullRequest()
    {
        using var scope = new SendScope();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => scope.Sender.Send((IRequest<string>)null!));

        Assert.Equal("request", exception.ParamName);
    }

    // The overload is a default interface member so decorators and test doubles written against the three original
    // members keep compiling. Their Send(object) is what the default forwards to.
    [Fact]
    public async Task Send_OnAnISenderThatOnlyImplementsTheOriginalMembers_ForwardsToItsObjectOverload()
    {
        using var scope = new SendScope();
        var sender = new PolyObjectOnlySender(scope.Sender);

        string response = await ((ISender)sender).Send(new PolyGetQuery(6));

        Assert.Equal("get 6", response);
        Assert.Equal(1, sender.ObjectSends);
    }

    [Fact]
    public async Task SendAsApiResult_WithoutTypeArguments_MapsTheResponseToJson()
    {
        using var scope = new SendScope();

        var executed = await ResultExecution.ExecuteAsync(await scope.Sender.SendAsApiResult(new PolyGetQuery(8)));

        Assert.Equal(StatusCodes.Status200OK, executed.StatusCode);
        Assert.Equal("\"get 8\"", executed.Body);
    }
}

// -------------------------------------------------------------------------------------------------
// A family of commands behind an abstract base with no handler of its own.
// -------------------------------------------------------------------------------------------------

public abstract record PolyCommand : IRequest;

public sealed record PolyDeleteCommand(SendProbe Probe) : PolyCommand;

public sealed record PolyArchiveCommand(SendProbe Probe) : PolyCommand;

/// <summary>Deliberately left without a handler, like its base.</summary>
public sealed record PolyOrphanCommand : PolyCommand;

public sealed class PolyDeleteCommandHandler : IRequestHandler<PolyDeleteCommand>
{
    public Task Handle(PolyDeleteCommand request, CancellationToken cancellationToken)
    {
        request.Probe.Record("delete", cancellationToken);
        return Task.CompletedTask;
    }
}

public sealed class PolyArchiveCommandHandler : IRequestHandler<PolyArchiveCommand>
{
    public Task Handle(PolyArchiveCommand request, CancellationToken cancellationToken)
    {
        request.Probe.Record("archive", cancellationToken);
        return Task.CompletedTask;
    }
}

public abstract record PolyQuery : IRequest<string>;

public sealed record PolyGetQuery(int Id) : PolyQuery;

public sealed class PolyGetQueryHandler : IRequestHandler<PolyGetQuery, string>
{
    public Task<string> Handle(PolyGetQuery request, CancellationToken cancellationToken)
        => Task.FromResult($"get {request.Id}");
}

// -------------------------------------------------------------------------------------------------
// A void command whose runtime type also answers with a count: only the void handler exists.
// -------------------------------------------------------------------------------------------------

public abstract record PolyNotifyingCommand : IRequest;

public sealed record PolyNotifyAndCountCommand(SendProbe Probe) : PolyNotifyingCommand, IRequest<int>;

public sealed class PolyNotifyAndCountVoidHandler : IRequestHandler<PolyNotifyAndCountCommand>
{
    public Task Handle(PolyNotifyAndCountCommand request, CancellationToken cancellationToken)
    {
        request.Probe.Record("void handler", cancellationToken);
        return Task.CompletedTask;
    }
}

// -------------------------------------------------------------------------------------------------
// A base type that has a handler of its own, and a derived type that does not.
// -------------------------------------------------------------------------------------------------

public record PolyHandledBaseCommand(SendProbe Probe) : IRequest;

public sealed record PolyHandledDerivedCommand(SendProbe Probe) : PolyHandledBaseCommand(Probe);

public sealed class PolyHandledBaseCommandHandler : IRequestHandler<PolyHandledBaseCommand>
{
    public Task Handle(PolyHandledBaseCommand request, CancellationToken cancellationToken)
    {
        request.Probe.Record($"base handler: {request.GetType().Name}", cancellationToken);
        return Task.CompletedTask;
    }
}

// -------------------------------------------------------------------------------------------------
// Inference and the missing-handler message.
// -------------------------------------------------------------------------------------------------

public sealed record PolyCountQuery(int Count) : IRequest<int>;

public sealed class PolyCountQueryHandler : IRequestHandler<PolyCountQuery, int>
{
    public Task<int> Handle(PolyCountQuery request, CancellationToken cancellationToken) => Task.FromResult(request.Count);
}

public sealed class PolyTwoFacedQuery : IRequest<string>, IRequest<int>;

public sealed class PolyTwoFacedTextHandler : IRequestHandler<PolyTwoFacedQuery, string>
{
    public Task<string> Handle(PolyTwoFacedQuery request, CancellationToken cancellationToken) => Task.FromResult("text");
}

public sealed class PolyTwoFacedNumberHandler : IRequestHandler<PolyTwoFacedQuery, int>
{
    public Task<int> Handle(PolyTwoFacedQuery request, CancellationToken cancellationToken) => Task.FromResult(42);
}

/// <summary>Deliberately left without a handler.</summary>
public sealed class PolyUnhandledQuery : IRequest<string>;

public sealed class PolyDependentQuery : IRequest<string>;

public sealed class PolyMissingDependency;

/// <summary>Handlers the assembly scan must not see, because they cannot be built from a plain container.</summary>
public static class PolyHost<TMarker>
{
    public sealed class DependentHandler(PolyMissingDependency dependency) : IRequestHandler<PolyDependentQuery, string>
    {
        public Task<string> Handle(PolyDependentQuery request, CancellationToken cancellationToken)
            => Task.FromResult(dependency.ToString()!);
    }
}

/// <summary>An <see cref="ISender"/> written before <c>Send&lt;TResponse&gt;(IRequest&lt;TResponse&gt;)</c> existed.</summary>
public sealed class PolyObjectOnlySender(ISender inner) : ISender
{
    public int ObjectSends { get; private set; }

    public Task<object?> Send(object request, CancellationToken cancellationToken = default)
    {
        ObjectSends++;
        return inner.Send(request, cancellationToken);
    }

    public Task<TResponse> Send<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest<TResponse>
        => inner.Send<TRequest, TResponse>(request, cancellationToken);

    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest
        => inner.Send(request, cancellationToken);
}
