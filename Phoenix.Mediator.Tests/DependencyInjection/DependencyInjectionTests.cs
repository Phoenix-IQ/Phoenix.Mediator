using System.Net;
using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Exceptions;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Sentry;
using Phoenix.Mediator.Tests.Infrastructure;
using Phoenix.Mediator.Validation;
using Xunit;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Phoenix.Mediator.Tests;

/// <summary>
/// Registration and resolution: what the container does when the mediator, a handler, a handler's own
/// dependency, an assembly or an option value is missing, null or registered invalidly.
/// <para>
/// Every case here is one a real app meets at startup or on its first request. The contract these tests
/// hold the library to is that each failure is loud and names the thing that is missing — a silent
/// mis-registration turns into "the handler never runs" or "nothing is validated" in production.
/// </para>
/// </summary>
public sealed class DiRegistrationTests
{
    // ------------------------------------------------------------------
    // AddMediator was never called
    // ------------------------------------------------------------------

    // AddMediatorHandlers is the "scan only" entry point. Documented as not registering the mediator:
    // an app that calls it alone gets handlers it can never send anything to.
    [Fact]
    public void AddMediatorHandlers_RegistersHandlers_ButNotTheMediatorItself()
    {
        var services = new ServiceCollection();
        services.AddMediatorHandlers(new FakeAssembly(typeof(DiPingHandler)));

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.IsType<DiPingHandler>(scope.ServiceProvider.GetService<IRequestHandler<DiPingRequest, string>>());
        Assert.Null(scope.ServiceProvider.GetService<ISender>());
        Assert.Null(scope.ServiceProvider.GetService<global::Phoenix.Mediator.Mediator.Mediator>());
    }

    // AddMediator() on its own is the whole "opt-in" contract in one test: no behavior is wired up until
    // a companion package is added, and no assembly is scanned until one is passed. A default that
    // scanned the entry assembly, or that registered validation, would change what every app runs.
    [Fact]
    public void AddMediator_WithoutArguments_RegistersNoHandlersAndNoPipelineBehaviors()
    {
        var services = new ServiceCollection();
        services.AddMediator();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.DoesNotContain(services, static descriptor =>
            descriptor.ServiceType.IsGenericType
            && (descriptor.ServiceType.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)
                || descriptor.ServiceType.GetGenericTypeDefinition() == typeof(IRequestHandler<>)));
        Assert.Empty(scope.ServiceProvider.GetServices<IPipelineBehavior<DiPingRequest, string>>());
        Assert.Empty(scope.ServiceProvider.GetServices<IPipelineBehavior<DiPingCommand>>());
    }

    // ------------------------------------------------------------------
    // The handler is missing
    // ------------------------------------------------------------------

    [Fact]
    public async Task Send_TypedOverload_ThrowsAndNamesTheMissingHandlerService_WhenNoHandlerIsRegistered()
    {
        await using var provider = BuildProvider(static services => services.AddMediator());
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.Send<DiUnhandledRequest, string>(new DiUnhandledRequest()));

        Assert.Contains("IRequestHandler", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(DiUnhandledRequest), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_VoidOverload_ThrowsAndNamesTheMissingHandlerService_WhenNoHandlerIsRegistered()
    {
        await using var provider = BuildProvider(static services => services.AddMediator());
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.Send(new DiUnhandledCommand()));

        Assert.Contains("IRequestHandler", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(DiUnhandledCommand), exception.Message, StringComparison.Ordinal);
    }

    // The object overload resolves the handler through a generated wrapper rather than through the call
    // site's type arguments, so the request type it asks the container for has to be named too — a
    // wrapper built for the wrong type would still fail, just for a request nobody sent.
    [Fact]
    public async Task Send_ObjectOverload_ThrowsAndNamesTheMissingHandlerService_WhenNoHandlerIsRegistered()
    {
        await using var provider = BuildProvider(static services => services.AddMediator());
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.Send((object)new DiUnhandledRequest()));

        Assert.Contains("IRequestHandler", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(DiUnhandledRequest), exception.Message, StringComparison.Ordinal);
    }

    // Registering the handler CLASS without its IRequestHandler<> interface is the classic "I registered
    // it, why is it not found" mistake: the mediator only ever asks for the interface.
    [Fact]
    public async Task Send_ThrowsWhenTheHandlerIsRegisteredOnlyAsItsConcreteType()
    {
        await using var provider = BuildProvider(static services =>
        {
            services.AddMediator();
            services.AddTransient<DiPingHandler>();
        });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.Send<DiPingRequest, string>(new DiPingRequest()));

        // Names the interface, not the class: the failure has to be "no IRequestHandler<>", not some
        // unrelated resolution error that happens to share the exception type.
        Assert.Contains("IRequestHandler", exception.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // The request itself is not a mediator request
    // ------------------------------------------------------------------

    [Fact]
    public async Task Send_ObjectOverload_ThrowsArgumentException_ForATypeThatImplementsNeitherRequestInterface()
    {
        await using var provider = BuildProvider(static services => services.AddMediator());
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => sender.Send(new DiNotARequest()));

        Assert.Contains(nameof(DiNotARequest), exception.Message, StringComparison.Ordinal);
        Assert.Contains("IRequest", exception.Message, StringComparison.Ordinal);
    }

    // The per-type wrapper cache is static and shared by every container in the process. A failed lookup
    // must not be cached, and must not stop the same mediator from serving real requests afterwards.
    [Fact]
    public async Task Send_ObjectOverload_KeepsWorkingAfterARejectedRequestType()
    {
        await using var provider = BuildProvider(static services => services.AddMediator(new FakeAssembly(typeof(DiPingHandler))));
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await Assert.ThrowsAsync<ArgumentException>(() => sender.Send(new DiNotARequest()));
        await Assert.ThrowsAsync<ArgumentException>(() => sender.Send(new DiNotARequest()));

        Assert.Equal("pong", await sender.Send((object)new DiPingRequest()));
    }

    // ------------------------------------------------------------------
    // Send(null) on each of the three overloads
    // ------------------------------------------------------------------

    [Fact]
    public async Task Send_ObjectOverload_ThrowsArgumentNullException_WhenTheRequestIsNull()
    {
        await using var provider = BuildProvider(static services => services.AddMediator());
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => sender.Send((object)null!));

        Assert.Equal("request", exception.ParamName);
    }

    // A null variable declared as IRequest<T>/IRequest dispatches by runtime type, which routes into the
    // object overload — so this path does get the guard, and reports the same parameter name.
    [Fact]
    public async Task Send_TypedOverload_ThrowsArgumentNullException_WhenTheStaticTypeIsTheRequestInterface()
    {
        await using var provider = BuildProvider(static services => services.AddMediator());
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => sender.Send<IRequest<string>, string>(null!));

        Assert.Equal("request", exception.ParamName);
    }

    [Fact]
    public async Task Send_VoidOverload_ThrowsArgumentNullException_WhenTheStaticTypeIsTheRequestInterface()
    {
        await using var provider = BuildProvider(static services => services.AddMediator());
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        // Typed null: a bare null literal also converts to IRequest<IRequest>, which makes the call ambiguous with
        // Send<TResponse>(IRequest<TResponse>). Real calls pass a typed request and bind as before.
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => sender.Send<IRequest>((IRequest)null!));

        Assert.Equal("request", exception.ParamName);
    }

    // Documents a gap rather than a virtue: with a CONCRETE type argument the strongly typed overload has
    // no null guard, so null reaches the handler and blows up inside application code instead of at the
    // mediator boundary. If a guard is ever added, this test is the one that should be updated.
    [Fact]
    public async Task Send_TypedOverload_PassesNullToTheHandler_WhenTheStaticTypeIsConcrete()
    {
        var probe = new DiNullProbe();
        await using var provider = BuildProvider(services =>
        {
            services.AddMediator();
            services.AddSingleton(probe);
            services.AddTransient<IRequestHandler<DiNullProbeRequest, string>, DiHost<object>.NullProbeHandler>();
        });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var response = await sender.Send<DiNullProbeRequest, string>(null!);

        Assert.Equal("handled", response);
        Assert.True(probe.RequestWasNull);
    }

    [Fact]
    public async Task Send_VoidOverload_PassesNullToTheHandler_WhenTheStaticTypeIsConcrete()
    {
        var probe = new DiNullProbe();
        await using var provider = BuildProvider(services =>
        {
            services.AddMediator();
            services.AddSingleton(probe);
            services.AddTransient<IRequestHandler<DiNullProbeCommand>, DiHost<object>.NullProbeCommandHandler>();
        });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        // Typed null, for the reason given in Send_VoidOverload_ThrowsArgumentNullException_WhenTheStaticTypeIsTheRequestInterface.
        await sender.Send<DiNullProbeCommand>((DiNullProbeCommand)null!);

        Assert.True(probe.CommandWasNull);
    }

    // ------------------------------------------------------------------
    // The handler's own dependencies
    // ------------------------------------------------------------------

    [Fact]
    public async Task Send_ThrowsAndNamesBothTypes_WhenTheHandlersOwnDependencyIsNotRegistered()
    {
        await using var provider = BuildProvider(static services =>
        {
            services.AddMediator();
            services.AddTransient<IRequestHandler<DiDependentRequest, string>, DiHost<object>.MissingDependencyHandler>();
        });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.Send<DiDependentRequest, string>(new DiDependentRequest()));

        Assert.Contains(nameof(DiUnregisteredDependency), exception.Message, StringComparison.Ordinal);
        Assert.Contains("MissingDependencyHandler", exception.Message, StringComparison.Ordinal);
    }

    // The same mistake caught at startup instead of on the first request: this is why Development turns
    // ValidateOnBuild on. Both type names have to appear, or the report says a handler is broken without
    // saying which constructor argument the app forgot to register.
    [Fact]
    public void BuildServiceProvider_WithValidateOnBuild_ReportsAHandlerWhoseDependencyIsMissing()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMediator();
        services.AddTransient<IRequestHandler<DiDependentRequest, string>, DiHost<object>.MissingDependencyHandler>();

        var exception = Assert.Throws<AggregateException>(() => services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }));

        Assert.Contains(exception.InnerExceptions, inner =>
            inner.Message.Contains("MissingDependencyHandler", StringComparison.Ordinal)
            && inner.Message.Contains(nameof(DiUnregisteredDependency), StringComparison.Ordinal));
    }

    // Mediator is scoped precisely so handlers can take scoped dependencies (DbContext, current user).
    // If it ever became a singleton this is the test that would catch it.
    [Fact]
    public async Task Send_ResolvesHandlerDependenciesFromTheCallingScope()
    {
        await using var provider = BuildProvider(static services =>
        {
            services.AddMediator();
            services.AddScoped<DiScopedProbe>();
            services.AddTransient<IRequestHandler<DiScopedRequest, string>, DiHost<object>.ScopedProbeHandler>();
        });

        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();

        var first = await firstScope.ServiceProvider.GetRequiredService<ISender>().Send<DiScopedRequest, string>(new DiScopedRequest());
        var firstAgain = await firstScope.ServiceProvider.GetRequiredService<ISender>().Send<DiScopedRequest, string>(new DiScopedRequest());
        var second = await secondScope.ServiceProvider.GetRequiredService<ISender>().Send<DiScopedRequest, string>(new DiScopedRequest());

        Assert.Equal(first, firstAgain);
        Assert.NotEqual(first, second);
    }

    // ------------------------------------------------------------------
    // Scope rules
    // ------------------------------------------------------------------

    [Fact]
    public void ISender_CannotBeResolvedFromTheRootProvider_WhenScopeValidationIsOn()
    {
        using var provider = BuildProvider(static services => services.AddMediator());

        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ISender>());

        Assert.Contains("scoped", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nameof(ISender), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Mediator_CannotBeResolvedFromTheRootProvider_WhenScopeValidationIsOn()
    {
        using var provider = BuildProvider(static services => services.AddMediator());

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<global::Phoenix.Mediator.Mediator.Mediator>());

        Assert.Contains("scoped", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ISender_IsTheSameInstanceWithinOneScope()
    {
        using var provider = BuildProvider(static services => services.AddMediator());
        using var scope = provider.CreateScope();

        Assert.Same(scope.ServiceProvider.GetRequiredService<ISender>(), scope.ServiceProvider.GetRequiredService<ISender>());
    }

    [Fact]
    public void ISender_IsADifferentInstanceInEveryScope()
    {
        using var provider = BuildProvider(static services => services.AddMediator());
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();

        Assert.NotSame(
            firstScope.ServiceProvider.GetRequiredService<ISender>(),
            secondScope.ServiceProvider.GetRequiredService<ISender>());
    }

    // ISender forwards to the scope's Mediator. Two instances would mean two option snapshots and two
    // wrapper lookups per request, and IMediatorOptionsAccessor would read a different object than the
    // one that handled the request.
    [Fact]
    public void ISender_AndTheConcreteMediator_AreTheSameInstanceWithinAScope()
    {
        using var provider = BuildProvider(static services => services.AddMediator());
        using var scope = provider.CreateScope();

        Assert.Same(
            scope.ServiceProvider.GetRequiredService<global::Phoenix.Mediator.Mediator.Mediator>(),
            scope.ServiceProvider.GetRequiredService<ISender>());
    }

    [Theory]
    [InlineData(typeof(ISender))]
    [InlineData(typeof(global::Phoenix.Mediator.Mediator.Mediator))]
    public void AddMediator_RegistersTheMediatorAsScoped(Type serviceType)
    {
        var services = new ServiceCollection();
        services.AddMediator();

        var descriptor = Assert.Single(services, d => d.ServiceType == serviceType);

        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    // ISender is registered with TryAdd, so an app that decorates or replaces it keeps its own
    // implementation — the extension point the IMediatorOptionsAccessor docs point at. The concrete
    // Mediator still has to be registered, otherwise a decorator has nothing to forward to.
    [Fact]
    public void AddMediator_KeepsAnISenderRegisteredBeforeIt()
    {
        using var provider = BuildProvider(static services =>
        {
            services.AddScoped<ISender, DiHost<object>.ReplacementSender>();
            services.AddMediator();
        });
        using var scope = provider.CreateScope();

        Assert.IsType<DiHost<object>.ReplacementSender>(scope.ServiceProvider.GetRequiredService<ISender>());
        // GetService, not GetRequiredService: the point is that the concrete mediator is still REGISTERED,
        // so a decorator has something to forward to.
        Assert.NotNull(scope.ServiceProvider.GetService<global::Phoenix.Mediator.Mediator.Mediator>());
    }

    // A singleton that takes ISender captures the first scope's mediator forever, which then serves every
    // later request with a stale scope. Scope validation must reject it rather than let it work by luck.
    // The assertion looks for "scoped", which only the captive-dependency message carries: had ISender not
    // been registered at all, the message would be "Unable to resolve service for type ...".
    [Fact]
    public void Singleton_DependingOnISender_IsRejectedAsACaptiveDependency()
    {
        using var provider = BuildProvider(static services =>
        {
            services.AddMediator();
            services.AddSingleton<DiHost<object>.SenderCapturingRootService>();
        });

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<DiHost<object>.SenderCapturingRootService>());

        Assert.Contains("scoped", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nameof(ISender), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildServiceProvider_WithValidateOnBuild_ReportsASingletonCapturingISender()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMediator();
        services.AddSingleton<DiHost<object>.SenderCapturingRootService>();

        var exception = Assert.Throws<AggregateException>(() => services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }));

        Assert.Contains(exception.InnerExceptions, inner =>
            inner.Message.Contains("SenderCapturingRootService", StringComparison.Ordinal)
            && inner.Message.Contains("scoped", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------
    // Null / empty arguments: AddMediator (4 overloads)
    // ------------------------------------------------------------------

    [Fact]
    public void AddMediator_ThrowsWhenServicesIsNull()
    {
        var services = (IServiceCollection)null!;

        var exception = Assert.Throws<ArgumentNullException>(() => { services.AddMediator(); });

        Assert.Equal("services", exception.ParamName);
    }

    [Fact]
    public void AddMediator_WithConfigureOptions_ThrowsWhenServicesIsNull()
    {
        var services = (IServiceCollection)null!;

        var exception = Assert.Throws<ArgumentNullException>(() => { services.AddMediator(static _ => { }); });

        Assert.Equal("services", exception.ParamName);
    }

    [Fact]
    public void AddMediator_WithAssemblies_ThrowsWhenServicesIsNull()
    {
        var services = (IServiceCollection)null!;

        var exception = Assert.Throws<ArgumentNullException>(() => { services.AddMediator(typeof(DiRegistrationTests).Assembly); });

        Assert.Equal("services", exception.ParamName);
    }

    [Fact]
    public void AddMediator_WithConfigureOptionsAndAssemblies_ThrowsWhenServicesIsNull()
    {
        var services = (IServiceCollection)null!;

        var exception = Assert.Throws<ArgumentNullException>(
            () => { services.AddMediator(static _ => { }, typeof(DiRegistrationTests).Assembly); });

        Assert.Equal("services", exception.ParamName);
    }

    [Fact]
    public void AddMediator_ThrowsWhenConfigureOptionsIsNull()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentNullException>(() => { services.AddMediator((Action<MediatorOptions>)null!); });

        Assert.Equal("configureOptions", exception.ParamName);
    }

    [Fact]
    public void AddMediator_WithAssemblies_ThrowsWhenConfigureOptionsIsNull()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentNullException>(
            () => { services.AddMediator((Action<MediatorOptions>)null!, typeof(DiRegistrationTests).Assembly); });

        Assert.Equal("configureOptions", exception.ParamName);
    }

    // The guard runs before anything is registered, so a caller that catches the exception (a test host,
    // a composition root probing for optional modules) is not left with a half-configured container.
    [Fact]
    public void AddMediator_ThrowsWhenTheAssemblyArrayIsNull()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentNullException>(() => { services.AddMediator((Assembly[])null!); });

        Assert.Equal("assemblies", exception.ParamName);
        Assert.Empty(services);
    }

    [Fact]
    public void AddMediator_WithConfigureOptions_ThrowsWhenTheAssemblyArrayIsNull()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentNullException>(
            () => { services.AddMediator(static _ => { }, (Assembly[])null!); });

        Assert.Equal("assemblies", exception.ParamName);
        Assert.Empty(services);
    }

    // Passing no assemblies is legal — it registers the mediator for an app that wires its handlers by
    // hand — so it must not throw and must not scan anything.
    [Fact]
    public void AddMediator_WithAnEmptyAssemblyArray_RegistersTheMediatorWithoutScanning()
    {
        var services = new ServiceCollection();
        services.AddMediator(Array.Empty<Assembly>());

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISender>());
        Assert.DoesNotContain(services, static descriptor =>
            descriptor.ServiceType == typeof(IRequestHandler<DiPingRequest, string>));
    }

    // The registry filters null entries, so one stray null in a params array cannot take the app down.
    [Fact]
    public void AddMediator_IgnoresNullEntriesInTheAssemblyArray()
    {
        var services = new ServiceCollection();

        services.AddMediator(new Assembly[] { null! });

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISender>());
    }

    // ...and filtering the null must not cost the array its real entries. `Assembly.GetEntryAssembly()`
    // returns null in a test host and in some single-file layouts, so an app really does pass arrays like
    // this one; silently dropping the rest would leave every handler unregistered.
    [Fact]
    public async Task AddMediator_ScansTheNonNullAssemblies_WhenTheArrayAlsoContainsNull()
    {
        await using var provider = BuildProvider(static services =>
            services.AddMediator(new Assembly[] { null!, new FakeAssembly(typeof(DiPingHandler)) }));
        using var scope = provider.CreateScope();

        Assert.Equal("pong", await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<DiPingRequest, string>(new DiPingRequest()));
    }

    // The fourth overload is the one the README's "configure + scan" snippet uses, and it is the only one
    // that has to do both jobs in a single call.
    [Fact]
    public async Task AddMediator_WithConfigureOptionsAndAssemblies_AppliesTheOptionsAndRegistersTheHandlers()
    {
        await using var provider = BuildProvider(static services => services.AddMediator(
            static options => options.EmptyResponseStatusCode = EmptyResponseStatusCode.Ok,
            new FakeAssembly(typeof(DiPingHandler))));
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        Assert.Equal("pong", await sender.Send<DiPingRequest, string>(new DiPingRequest()));
        Assert.Equal(
            EmptyResponseStatusCode.Ok,
            Assert.IsAssignableFrom<IMediatorOptionsAccessor>(sender).Options.EmptyResponseStatusCode);
    }

    // ------------------------------------------------------------------
    // Null / empty arguments: AddMediatorHandlers, AddMediatorValidation, AddMediatorSentry
    // ------------------------------------------------------------------

    [Fact]
    public void AddMediatorHandlers_ThrowsWhenServicesIsNull()
    {
        var services = (IServiceCollection)null!;

        var exception = Assert.Throws<ArgumentNullException>(
            () => { services.AddMediatorHandlers(typeof(DiRegistrationTests).Assembly); });

        Assert.Equal("services", exception.ParamName);
    }

    // Scanning nothing would be a silent no-op, so this one is an error rather than a shrug.
    [Fact]
    public void AddMediatorHandlers_ThrowsArgumentException_WhenNoAssemblyIsProvided()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentException>(() => { services.AddMediatorHandlers(); });

        Assert.Equal("assemblies", exception.ParamName);
        Assert.Contains("At least one assembly", exception.Message, StringComparison.Ordinal);
    }

    // A null array reports the same ArgumentException, not ArgumentNullException: Assert.Throws is exact,
    // so this pins which of the two a caller has to catch.
    [Fact]
    public void AddMediatorHandlers_ThrowsArgumentException_WhenTheAssemblyArrayIsNull()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentException>(() => { services.AddMediatorHandlers(null!); });

        Assert.Equal("assemblies", exception.ParamName);
    }

    [Fact]
    public void AddMediatorValidation_ThrowsWhenServicesIsNull()
    {
        var services = (IServiceCollection)null!;

        var exception = Assert.Throws<ArgumentNullException>(() => { services.AddMediatorValidation(); });

        Assert.Equal("services", exception.ParamName);
    }

    [Fact]
    public void AddMediatorValidation_ThrowsWhenTheAssemblyArrayIsNull()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentNullException>(() => { services.AddMediatorValidation(null!); });

        Assert.Equal("assemblies", exception.ParamName);
    }

    // Same tolerance as AddMediator: a null entry is skipped, and the assemblies beside it are still
    // scanned. Without the filter, `AddMediatorValidation(a, null, b)` would throw at startup.
    [Fact]
    public void AddMediatorValidation_IgnoresNullEntriesInTheAssemblyArray()
    {
        var services = new ServiceCollection();

        services.AddMediatorValidation(new Assembly[] { null!, new FakeAssembly(typeof(DiNameValidator)) });

        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IValidator<DiValidatedRequest>));
    }

    // Documented: no assemblies registers the behavior only. The behavior still has to be resolvable,
    // otherwise hand-registered validators would never run.
    [Fact]
    public void AddMediatorValidation_WithNoAssemblies_RegistersTheBehaviorOnly()
    {
        var services = new ServiceCollection();
        services.AddMediator().AddMediatorValidation();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.IsType<ValidationBehavior<DiValidatedRequest, string>>(
            Assert.Single(scope.ServiceProvider.GetServices<IPipelineBehavior<DiValidatedRequest, string>>()));
        Assert.Empty(scope.ServiceProvider.GetServices<IValidator<DiValidatedRequest>>());
    }

    [Fact]
    public void AddMediatorSentry_ThrowsWhenServicesIsNull()
    {
        var services = (IServiceCollection)null!;

        var exception = Assert.Throws<ArgumentNullException>(() => { services.AddMediatorSentry(); });

        Assert.Equal("services", exception.ParamName);
    }

    // ------------------------------------------------------------------
    // MediatorOptions validation
    // ------------------------------------------------------------------

    [Fact]
    public void MediatorOptions_DefaultToNoContent_WhenNothingIsConfigured()
    {
        using var provider = BuildProvider(static services => services.AddMediator());

        Assert.Equal(
            EmptyResponseStatusCode.NoContent,
            provider.GetRequiredService<IOptions<MediatorOptions>>().Value.EmptyResponseStatusCode);
    }

    // The endpoint helpers cast this enum straight to an int for the OpenAPI success response
    // (`new ResponseDto((int)emptyResponseStatusCode, null)`). Dropping the explicit values — reordering
    // the members, or letting them default to 0 and 1 — would still pass the options validator and would
    // advertise "0" as the status code of every command endpoint.
    [Fact]
    public void EmptyResponseStatusCode_MembersCarryTheHttpStatusCodesTheyName()
    {
        Assert.Equal(200, (int)EmptyResponseStatusCode.Ok);
        Assert.Equal(204, (int)EmptyResponseStatusCode.NoContent);
    }

    [Theory]
    [InlineData(EmptyResponseStatusCode.Ok)]
    [InlineData(EmptyResponseStatusCode.NoContent)]
    public void MediatorOptions_AcceptBothDocumentedStatusCodes(EmptyResponseStatusCode configured)
    {
        using var provider = BuildProvider(services => services.AddMediator(options => options.EmptyResponseStatusCode = configured));

        Assert.Equal(configured, provider.GetRequiredService<IOptions<MediatorOptions>>().Value.EmptyResponseStatusCode);
    }

    // Anything else would make SendAsApiResult write a status the API contract never promised, so the
    // option is rejected instead of being silently coerced.
    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    [InlineData(400)]
    [InlineData(500)]
    public void MediatorOptions_RejectAnyOtherStatusCode(int statusCode)
    {
        using var provider = BuildProvider(services =>
            services.AddMediator(options => options.EmptyResponseStatusCode = (EmptyResponseStatusCode)statusCode));

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<MediatorOptions>>().Value);

        Assert.Contains("200 OK or 204 No Content", Assert.Single(exception.Failures), StringComparison.Ordinal);
    }

    // The validator is registered with TryAddEnumerable rather than OptionsBuilder.Validate(), which would
    // append one validator per AddMediator call and repeat the same failure three times.
    [Fact]
    public void MediatorOptions_ReportExactlyOneFailure_WhenAddMediatorIsCalledSeveralTimes()
    {
        using var provider = BuildProvider(static services =>
        {
            services.AddMediator();
            services.AddMediator(static options => options.EmptyResponseStatusCode = (EmptyResponseStatusCode)201);
            services.AddMediator(new FakeAssembly(typeof(DiPingHandler)));
        });

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<MediatorOptions>>().Value);

        Assert.Single(exception.Failures);
    }

    [Fact]
    public void AddMediator_RegistersTheOptionsValidatorOnce_WhenCalledRepeatedly()
    {
        var services = new ServiceCollection();
        services.AddMediator();
        services.AddMediator(static _ => { });
        services.AddMediator(new FakeAssembly(typeof(DiPingHandler)));

        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IValidateOptions<MediatorOptions>));
    }

    // Configure delegates run in registration order, so the last AddMediator call decides.
    [Fact]
    public void MediatorOptions_LastConfigurationWins_WhenAddMediatorIsCalledTwice()
    {
        using var provider = BuildProvider(static services =>
        {
            services.AddMediator(static options => options.EmptyResponseStatusCode = EmptyResponseStatusCode.NoContent);
            services.AddMediator(static options => options.EmptyResponseStatusCode = EmptyResponseStatusCode.Ok);
        });

        Assert.Equal(
            EmptyResponseStatusCode.Ok,
            provider.GetRequiredService<IOptions<MediatorOptions>>().Value.EmptyResponseStatusCode);
    }

    // The Web helpers read the options off the sender through this interface; if the mediator stopped
    // implementing it, empty responses would silently fall back to 204 and ignore the app's configuration.
    [Fact]
    public void Mediator_ExposesTheConfiguredOptionsThroughTheOptionsAccessor()
    {
        using var provider = BuildProvider(static services =>
            services.AddMediator(static options => options.EmptyResponseStatusCode = EmptyResponseStatusCode.Ok));
        using var scope = provider.CreateScope();

        var accessor = Assert.IsAssignableFrom<IMediatorOptionsAccessor>(scope.ServiceProvider.GetRequiredService<ISender>());

        Assert.Equal(EmptyResponseStatusCode.Ok, accessor.Options.EmptyResponseStatusCode);
    }

    // An invalid option does not stop the mediator from being constructed: the mediator holds IOptions<T>
    // and reads .Value lazily, so the failure surfaces the first time the accessor is read — which is on
    // the first request that maps an empty response, not at startup. Worth knowing when reading a bug
    // report that says "it started fine".
    [Fact]
    public void MediatorOptions_AreValidatedWhenRead_NotWhenTheMediatorIsResolved()
    {
        using var provider = BuildProvider(static services =>
            services.AddMediator(static options => options.EmptyResponseStatusCode = (EmptyResponseStatusCode)201));
        using var scope = provider.CreateScope();

        var accessor = Assert.IsAssignableFrom<IMediatorOptionsAccessor>(scope.ServiceProvider.GetRequiredService<ISender>());

        Assert.Throws<OptionsValidationException>(() => accessor.Options);
    }

    // ------------------------------------------------------------------
    // Idempotence
    // ------------------------------------------------------------------

    [Fact]
    public void AddMediator_RegistersTheMediatorServicesOnce_WhenCalledRepeatedly()
    {
        var services = new ServiceCollection();
        services.AddMediator();
        services.AddMediator();
        services.AddMediator(new FakeAssembly(typeof(DiPingHandler)));

        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(ISender));
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(global::Phoenix.Mediator.Mediator.Mediator));
    }

    // The JSON post-configure is what keeps [FromRoute]/[FromQuery] members out of the body. Registering
    // it twice would wrap the resolver twice per AddMediator call.
    [Fact]
    public void AddMediator_RegistersTheJsonPostConfigureOnce_WhenCalledRepeatedly()
    {
        var services = new ServiceCollection();
        services.AddMediator();
        services.AddMediator();
        services.AddMediator();

        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IPostConfigureOptions<HttpJsonOptions>));
    }

    // The assembly registry is a singleton INSTANCE, and MapEndpoints resolves it to learn which
    // assemblies to search for endpoint groups. A second instance would win that lookup (the container
    // returns the last descriptor for a service type) and it would hold an empty assembly set — so every
    // endpoint group would quietly go undiscovered. Identified by shape rather than by name because the
    // registry type is internal: it is the only service AddMediator registers from its own assembly as a
    // pre-built instance.
    [Fact]
    public void AddMediator_KeepsASingleAssemblyRegistryInstance_WhenCalledRepeatedly()
    {
        var services = new ServiceCollection();
        services.AddMediator();
        services.AddMediator(new FakeAssembly("first", typeof(DiPingHandler)));
        services.AddMediator(static _ => { }, new FakeAssembly("second", typeof(DiRecordHandler)));

        Assert.Single(services, static descriptor =>
            descriptor.ImplementationInstance is not null
            && descriptor.ServiceType.Assembly == typeof(ISender).Assembly);
    }

    [Fact]
    public void AddMediator_DoesNotDuplicateHandlerRegistrations_WhenTheSameAssemblyIsPassedTwice()
    {
        var assembly = new FakeAssembly(typeof(DiPingHandler));
        var services = new ServiceCollection();

        services.AddMediator(assembly);
        services.AddMediator(assembly);

        var descriptor = Assert.Single(services, static d => d.ServiceType == typeof(IRequestHandler<DiPingRequest, string>));
        Assert.Equal(typeof(DiPingHandler), descriptor.ImplementationType);
    }

    [Fact]
    public void AddMediatorValidation_RegistersEachBehaviorOnce_WhenCalledRepeatedly()
    {
        var services = new ServiceCollection();
        services.AddMediatorValidation();
        services.AddMediatorValidation();
        services.AddMediatorValidation(new FakeAssembly(typeof(DiNameValidator)));

        Assert.Single(services, static descriptor =>
            descriptor.ServiceType == typeof(IPipelineBehavior<,>) && descriptor.ImplementationType == typeof(ValidationBehavior<,>));
        Assert.Single(services, static descriptor =>
            descriptor.ServiceType == typeof(IPipelineBehavior<>) && descriptor.ImplementationType == typeof(ValidationBehavior<>));
    }

    [Fact]
    public void AddMediatorSentry_RegistersEachBehaviorOnce_WhenCalledRepeatedly()
    {
        var services = new ServiceCollection();
        services.AddMediatorSentry();
        services.AddMediatorSentry();
        services.AddMediatorSentry();

        Assert.Single(services, static descriptor =>
            descriptor.ServiceType == typeof(IPipelineBehavior<,>) && descriptor.ImplementationType == typeof(SentryBehavior<,>));
        Assert.Single(services, static descriptor =>
            descriptor.ServiceType == typeof(IPipelineBehavior<>) && descriptor.ImplementationType == typeof(SentryBehavior<>));
    }

    // The startup diagnostics run once per host. Two hosted-service registrations would print every
    // validator warning twice.
    [Fact]
    public void AddMediatorValidation_RegistersTheStartupDiagnosticsOnce_WhenCalledRepeatedly()
    {
        var services = new ServiceCollection();
        services.AddMediatorValidation();
        services.AddMediatorValidation();
        services.AddMediatorValidation();

        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    // The assembly registry stops a second AddMediatorValidation(assembly) from scanning it again. FluentValidation 12
    // skips validators it has already registered, so the registry saves the scan rather than a duplicate validator.
    [Fact]
    public void AddMediatorValidation_DoesNotRescanTheSameAssembly()
    {
        var assembly = new DiCountingAssembly(typeof(DiNameValidator));
        var services = new ServiceCollection();

        services.AddMediatorValidation(assembly);
        services.AddMediatorValidation(assembly);

        Assert.Equal(1, assembly.GetTypesCallCount);
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IValidator<DiValidatedRequest>));
    }

    // ------------------------------------------------------------------
    // Handler scan semantics — all through FakeAssembly so the real assembly is untouched
    // ------------------------------------------------------------------

    [Fact]
    public void AddMediatorHandlers_SkipsOpenGenericHandlers_ButKeepsClosedOnes()
    {
        var services = new ServiceCollection();

        services.AddMediatorHandlers(new FakeAssembly(typeof(DiOpenGenericHandler<>), typeof(DiPingHandler)));

        Assert.DoesNotContain(services, static descriptor => descriptor.ImplementationType == typeof(DiOpenGenericHandler<>));
        Assert.Contains(services, static descriptor => descriptor.ImplementationType == typeof(DiPingHandler));
    }

    // The consequence of that skip, which the descriptor assertion above cannot show: an open generic
    // handler's service type is a partially open interface, and the container rejects it while BUILDING
    // the provider. Registering one would take down every app whose assembly contains a generic handler,
    // before a single request is served.
    [Fact]
    public async Task AddMediator_BuildsAWorkingContainer_WhenTheScannedAssemblyHasAnOpenGenericHandler()
    {
        await using var provider = BuildProvider(static services =>
            services.AddMediator(new FakeAssembly(typeof(DiOpenGenericHandler<>), typeof(DiPingHandler))));
        using var scope = provider.CreateScope();

        Assert.Equal("pong", await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<DiPingRequest, string>(new DiPingRequest()));
    }

    // An abstract base handler is a design element, not a registration: the container cannot construct it.
    // Its concrete subclass, on the other hand, must be registered — the base class is where shared handler
    // plumbing lives, and skipping the whole hierarchy would unregister the real handler with it.
    // If the abstract guard were dropped, both types would claim the same interface and the scan's
    // duplicate check would throw instead.
    [Fact]
    public void AddMediatorHandlers_SkipsAnAbstractHandler_ButRegistersItsConcreteSubclass()
    {
        var services = new ServiceCollection();

        services.AddMediatorHandlers(new FakeAssembly(
            typeof(DiHost<object>.AbstractHandler),
            typeof(DiHost<object>.ConcreteSubclassHandler)));

        var descriptor = Assert.Single(services, static d => d.ServiceType == typeof(IRequestHandler<DiAbstractRequest, string>));
        Assert.Equal(typeof(DiHost<object>.ConcreteSubclassHandler), descriptor.ImplementationType);
    }

    [Fact]
    public void AddMediatorHandlers_SkipsInterfacesThatExtendIRequestHandler()
    {
        var services = new ServiceCollection();

        services.AddMediatorHandlers(new FakeAssembly(typeof(DiHost<object>.IHandlerContract)));

        // Nothing at all: an interface cannot be activated, so registering it under the handler interface
        // it extends would fail at the first Send with "no implementation type".
        Assert.Empty(services);
    }

    [Fact]
    public void AddMediatorHandlers_RegistersNothing_ForAnAssemblyWithoutHandlers()
    {
        var services = new ServiceCollection();

        services.AddMediatorHandlers(new FakeAssembly(typeof(DiNotARequest), typeof(DiPingRequest)));

        Assert.Empty(services);
    }

    // One class may serve several requests; the scan must register it under every IRequestHandler<>
    // interface it implements, not just the first one reflection happens to return.
    [Fact]
    public async Task AddMediatorHandlers_RegistersAHandlerForEveryRequestItHandles()
    {
        await using var provider = BuildProvider(static services =>
            services.AddMediator(new FakeAssembly(typeof(DiMultiHandler))));
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        Assert.Equal("multi", await sender.Send<DiMultiQuery, string>(new DiMultiQuery()));
        // The void interface is registered by a separate branch of the scan, and its send has no return
        // value to check — so resolve it as well and confirm it is the same class.
        await sender.Send(new DiMultiCommand());
        Assert.IsType<DiMultiHandler>(scope.ServiceProvider.GetRequiredService<IRequestHandler<DiMultiCommand>>());
    }

    // Two handlers for one request used to be resolved by scan order, silently. Both names have to be in
    // the message or there is no way to know which file to delete.
    [Fact]
    public void AddMediatorHandlers_ThrowsAndNamesBothTypes_WhenTwoTypesHandleTheSameRequest()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddMediatorHandlers(
            new FakeAssembly(typeof(DiHost<object>.FirstDuplicateHandler), typeof(DiHost<object>.SecondDuplicateHandler))));

        Assert.Contains("FirstDuplicateHandler", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SecondDuplicateHandler", exception.Message, StringComparison.Ordinal);
    }

    // The realistic version of the conflict: one handler per project, both projects passed to the same
    // AddMediator call. The scan tracks what it registered across the whole call, not per assembly.
    [Fact]
    public void AddMediatorHandlers_ThrowsAndNamesBothTypes_WhenTwoAssembliesHandleTheSameRequest()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddMediatorHandlers(
            new FakeAssembly("first", typeof(DiHost<object>.FirstDuplicateHandler)),
            new FakeAssembly("second", typeof(DiHost<object>.SecondDuplicateHandler))));

        Assert.Contains("FirstDuplicateHandler", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SecondDuplicateHandler", exception.Message, StringComparison.Ordinal);
    }

    // A limitation, pinned so a change to it is deliberate: the conflict check lives inside ONE scan, so
    // two separate calls do not see each other and TryAdd keeps whichever handler was registered first.
    // An app that composes modules ("module A adds its handlers, module B adds its own") therefore still
    // gets the silent win-by-order that the same-call check exists to prevent.
    [Fact]
    public void AddMediatorHandlers_DoesNotDetectAConflictAcrossSeparateCalls_AndKeepsTheFirstHandler()
    {
        var services = new ServiceCollection();

        services.AddMediatorHandlers(new FakeAssembly("first", typeof(DiHost<object>.FirstDuplicateHandler)));
        services.AddMediatorHandlers(new FakeAssembly("second", typeof(DiHost<object>.SecondDuplicateHandler)));

        var descriptor = Assert.Single(services, static d => d.ServiceType == typeof(IRequestHandler<DiDuplicateRequest, string>));
        Assert.Equal(typeof(DiHost<object>.FirstDuplicateHandler), descriptor.ImplementationType);
    }

    [Fact]
    public void AddMediator_ThrowsWhenTwoTypesHandleTheSameRequest()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddMediator(
            new FakeAssembly(typeof(DiHost<object>.FirstDuplicateHandler), typeof(DiHost<object>.SecondDuplicateHandler))));

        Assert.Contains(nameof(DiDuplicateRequest), exception.Message, StringComparison.Ordinal);
    }

    // The guard is about two DIFFERENT types. Seeing the same type twice (duplicate entries, or the same
    // handler reachable through two scanned assemblies) is not a conflict and must not throw.
    [Fact]
    public void AddMediatorHandlers_DoesNotThrow_WhenTheSameHandlerTypeIsListedTwice()
    {
        var services = new ServiceCollection();

        services.AddMediatorHandlers(new FakeAssembly(typeof(DiPingHandler), typeof(DiPingHandler)));

        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IRequestHandler<DiPingRequest, string>));
    }

    [Fact]
    public void AddMediatorHandlers_DoesNotThrow_WhenTwoAssembliesYieldTheSameHandlerType()
    {
        var services = new ServiceCollection();

        services.AddMediatorHandlers(
            new FakeAssembly("first", typeof(DiPingHandler)),
            new FakeAssembly("second", typeof(DiPingHandler)));

        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IRequestHandler<DiPingRequest, string>));
    }

    // Transient: a handler must not outlive the request, and must not be shared between two requests
    // running at the same time.
    [Fact]
    public void AddMediatorHandlers_RegistersHandlersAsTransient()
    {
        var services = new ServiceCollection();

        services.AddMediatorHandlers(new FakeAssembly(typeof(DiPingHandler)));

        var descriptor = Assert.Single(services, static d => d.ServiceType == typeof(IRequestHandler<DiPingRequest, string>));
        Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
    }

    // Documented override point: register the handler you want BEFORE the scan and TryAdd keeps it,
    // lifetime and all. Without this there is no way to replace a handler that ships in a library.
    [Fact]
    public async Task AddMediator_KeepsAHandlerRegisteredBeforeTheScan()
    {
        await using var provider = BuildProvider(static services =>
        {
            services.AddSingleton<IRequestHandler<DiOverriddenRequest, string>, DiHost<object>.ExplicitHandler>();
            services.AddMediator(new FakeAssembly(typeof(DiHost<object>.ScannedHandler)));
        });
        using var scope = provider.CreateScope();

        Assert.Equal("explicit", await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<DiOverriddenRequest, string>(new DiOverriddenRequest()));
    }

    [Fact]
    public void AddMediator_DoesNotReplaceAHandlerRegisteredBeforeTheScan()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRequestHandler<DiOverriddenRequest, string>, DiHost<object>.ExplicitHandler>();

        services.AddMediator(new FakeAssembly(typeof(DiHost<object>.ScannedHandler)));

        var descriptor = Assert.Single(services, static d => d.ServiceType == typeof(IRequestHandler<DiOverriddenRequest, string>));
        Assert.Equal(typeof(DiHost<object>.ExplicitHandler), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    // Internal handlers are registered like public ones — the same tolerance validators get. An app that
    // keeps its handlers internal would otherwise find none of them registered. (A scan that switched to
    // GetExportedTypes would leave this assembly's internal handler out.)
    [Fact]
    public void AddMediatorHandlers_RegistersHandlersRegardlessOfVisibility()
    {
        var services = new ServiceCollection();

        services.AddMediatorHandlers(new FakeAssembly(typeof(DiInternalHandler)));

        Assert.Contains(services, static descriptor => descriptor.ImplementationType == typeof(DiInternalHandler));
    }

    // An assembly that references something undeployed throws ReflectionTypeLoadException from GetTypes().
    // Startup has to survive it with whatever loaded, instead of taking the whole app down.
    [Fact]
    public async Task AddMediatorHandlers_RegistersTheLoadableHandlers_WhenTheAssemblyOnlyPartiallyLoads()
    {
        await using var provider = BuildProvider(static services =>
        {
            services.AddMediator();
            services.AddMediatorHandlers(new PartiallyLoadableFakeAssembly(typeof(DiPingHandler)));
        });
        using var scope = provider.CreateScope();

        Assert.Equal("pong", await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<DiPingRequest, string>(new DiPingRequest()));
    }

    // ...but only THAT failure is absorbed. A load error of any other kind (a missing file, a bad image
    // format) means the deployment is broken in a way "register what loaded" cannot paper over, so it has
    // to reach the caller rather than leave the app running with an arbitrary subset of its handlers.
    [Fact]
    public void AddMediatorHandlers_PropagatesAnAssemblyFailureThatIsNotAPartialTypeLoad()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<BadImageFormatException>(
            () => services.AddMediatorHandlers(new DiUnreadableAssembly()));

        // The original exception, not one wrapped in "could not scan assembly": the loader message is the
        // only thing that says which file is broken.
        Assert.Contains("Simulated unreadable assembly", exception.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // The assembly registry
    // ------------------------------------------------------------------

    [Fact]
    public void AddMediator_DoesNotRescanAnAssemblyItHasAlreadySeen()
    {
        var assembly = new DiCountingAssembly(typeof(DiPingHandler));
        var services = new ServiceCollection();

        services.AddMediator(assembly);
        services.AddMediator(assembly);
        services.AddMediator(static _ => { }, assembly);

        Assert.Equal(1, assembly.GetTypesCallCount);
    }

    [Fact]
    public void AddMediator_ScansAnAssemblyOnce_WhenItIsListedTwiceInTheSameCall()
    {
        var assembly = new DiCountingAssembly(typeof(DiPingHandler));
        var services = new ServiceCollection();

        services.AddMediator(assembly, assembly);

        Assert.Equal(1, assembly.GetTypesCallCount);
    }

    [Fact]
    public void AddMediator_AccumulatesAssembliesAcrossCalls()
    {
        var services = new ServiceCollection();

        services.AddMediator(new FakeAssembly("first", typeof(DiPingHandler)));
        services.AddMediator(new FakeAssembly("second", typeof(DiRecordHandler)));

        Assert.Contains(services, static descriptor => descriptor.ImplementationType == typeof(DiPingHandler));
        Assert.Contains(services, static descriptor => descriptor.ImplementationType == typeof(DiRecordHandler));
    }

    // AddMediatorHandlers does not consult the registry: it is the explicit "scan this now" call. Passing
    // an assembly AddMediator already scanned must therefore be harmless rather than a duplicate
    // registration — TryAdd keeps the first descriptor.
    [Fact]
    public void AddMediatorHandlers_RescansWithoutDuplicating_WhenTheAssemblyWasAlreadyScannedByAddMediator()
    {
        var assembly = new DiCountingAssembly(typeof(DiPingHandler));
        var services = new ServiceCollection();

        services.AddMediator(assembly);
        services.AddMediatorHandlers(assembly);

        Assert.Equal(2, assembly.GetTypesCallCount);
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IRequestHandler<DiPingRequest, string>));
    }

    // ------------------------------------------------------------------
    // Health checks and a fully wired container
    // ------------------------------------------------------------------

    // MapEndpoints maps /health by default, which needs the services AddMediator registers here. Without
    // them the app throws "Unable to find the required services" while mapping its endpoints.
    [Fact]
    public void AddMediator_RegistersTheHealthCheckServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMediator();

        using var provider = services.BuildServiceProvider(validateScopes: true);

        Assert.Contains(services, static descriptor => descriptor.ServiceType == typeof(HealthCheckService));
        Assert.NotNull(provider.GetService<HealthCheckService>());
    }

    // Not throwing IS the contract: a Development host turns ValidateScopes and ValidateOnBuild on, so a
    // registration the container cannot construct stops the app before it serves a single request.
    [Fact]
    public async Task AddMediator_BuildsUnderDevelopmentValidation_WithTheWholeStackRegistered()
    {
        var assembly = new FakeAssembly(
            typeof(DiPingHandler),
            typeof(DiPingCommandHandler),
            typeof(DiRecordHandler),
            typeof(DiValidatedRequestHandler),
            typeof(DiNameValidator));

        var builder = TestApps.CreateBuilder(Environments.Development);
        builder.Services.AddMediator(assembly).AddMediatorSentry().AddMediatorValidation(assembly);

        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();

        Assert.Equal("pong", await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<DiPingRequest, string>(new DiPingRequest()));
    }

    // ------------------------------------------------------------------
    // Registration order
    // ------------------------------------------------------------------

    // Behaviors are TryAddEnumerable'd open generics, so the order the container hands them back is the
    // order they were registered — first registered runs outermost.
    [Fact]
    public void PipelineBehaviors_AreResolvedInRegistrationOrder_SentryBeforeValidation()
    {
        using var provider = BuildProvider(static services => services
            .AddMediator()
            .AddMediatorSentry()
            .AddMediatorValidation());
        using var scope = provider.CreateScope();

        Assert.Collection(
            scope.ServiceProvider.GetServices<IPipelineBehavior<DiValidatedRequest, string>>(),
            behavior => Assert.IsType<SentryBehavior<DiValidatedRequest, string>>(behavior),
            behavior => Assert.IsType<ValidationBehavior<DiValidatedRequest, string>>(behavior));
    }

    [Fact]
    public void PipelineBehaviors_AreResolvedInRegistrationOrder_ValidationBeforeSentry()
    {
        using var provider = BuildProvider(static services => services
            .AddMediator()
            .AddMediatorValidation()
            .AddMediatorSentry());
        using var scope = provider.CreateScope();

        Assert.Collection(
            scope.ServiceProvider.GetServices<IPipelineBehavior<DiValidatedRequest, string>>(),
            behavior => Assert.IsType<ValidationBehavior<DiValidatedRequest, string>>(behavior),
            behavior => Assert.IsType<SentryBehavior<DiValidatedRequest, string>>(behavior));
    }

    [Fact]
    public void PipelineBehaviors_AreResolvedInRegistrationOrder_ForVoidRequests()
    {
        using var provider = BuildProvider(static services => services
            .AddMediator()
            .AddMediatorSentry()
            .AddMediatorValidation());
        using var scope = provider.CreateScope();

        Assert.Collection(
            scope.ServiceProvider.GetServices<IPipelineBehavior<DiPingCommand>>(),
            behavior => Assert.IsType<SentryBehavior<DiPingCommand>>(behavior),
            behavior => Assert.IsType<ValidationBehavior<DiPingCommand>>(behavior));
    }

    // The whole registration path for validation in one test: the scan has to find the validator, register
    // it under IValidator<TRequest> with a lifetime resolvable from the request scope, and the behavior has
    // to be handed it. Any one of those breaking means the app validates nothing and returns 200 for input
    // it should have rejected.
    [Fact]
    public async Task AddMediatorValidation_RunsAValidatorFoundByTheAssemblyScan()
    {
        var assembly = new FakeAssembly(typeof(DiValidatedRequestHandler), typeof(DiNameValidator));
        await using var provider = BuildProvider(services => services.AddMediator(assembly).AddMediatorValidation(assembly));
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<HttpResponseException>(
            () => sender.Send<DiValidatedRequest, string>(new DiValidatedRequest()));

        Assert.Equal(HttpStatusCode.BadRequest, exception.HttpStatusCode);
        Assert.Equal("ok", await sender.Send<DiValidatedRequest, string>(new DiValidatedRequest { Name = "ok" }));
    }

    // The companion packages are documented as "call after AddMediator", but composing a container from
    // several modules makes the order accidental. It must not change what works.
    [Fact]
    public async Task AddMediatorValidation_BeforeAddMediator_StillValidates()
    {
        await using var provider = BuildProvider(static services =>
        {
            services.AddMediatorValidation();
            services.AddScoped<IValidator<DiValidatedRequest>, DiNameValidator>();
            services.AddMediator(new FakeAssembly(typeof(DiValidatedRequestHandler)));
        });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<HttpResponseException>(
            () => sender.Send<DiValidatedRequest, string>(new DiValidatedRequest()));

        Assert.Equal(HttpStatusCode.BadRequest, exception.HttpStatusCode);
        Assert.Equal("ok", await sender.Send<DiValidatedRequest, string>(new DiValidatedRequest { Name = "ok" }));
    }

    // ------------------------------------------------------------------
    // AddMediatorValidation takes its assemblies from AddMediator
    // ------------------------------------------------------------------

    // The app names its assemblies once, in AddMediator; validators usually sit next to their handlers.
    [Fact]
    public async Task AddMediatorValidation_WithoutAssemblies_RunsTheValidatorsInTheAssembliesGivenToAddMediator()
    {
        var assembly = new FakeAssembly(typeof(DiValidatedRequestHandler), typeof(DiNameValidator));
        await using var provider = BuildProvider(services => services.AddMediator(assembly).AddMediatorValidation());
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<HttpResponseException>(
            () => sender.Send<DiValidatedRequest, string>(new DiValidatedRequest()));

        Assert.Equal(HttpStatusCode.BadRequest, exception.HttpStatusCode);
    }

    // Composing a container from modules makes the order accidental: the assemblies AddMediator gets afterwards count too.
    [Fact]
    public async Task AddMediatorValidation_BeforeAddMediator_RunsTheValidatorsInItsAssemblies()
    {
        await using var provider = BuildProvider(static services =>
        {
            services.AddMediatorValidation();
            services.AddMediator(new FakeAssembly(typeof(DiValidatedRequestHandler), typeof(DiNameValidator)));
        });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<HttpResponseException>(
            () => sender.Send<DiValidatedRequest, string>(new DiValidatedRequest()));

        Assert.Equal(HttpStatusCode.BadRequest, exception.HttpStatusCode);
    }

    // A later AddMediator call — another module adding its assembly — brings its validators along.
    [Fact]
    public void AddMediatorValidation_RegistersTheValidatorsOfALaterAddMediatorCall()
    {
        var services = new ServiceCollection();

        services.AddMediator(new FakeAssembly("first", typeof(DiValidatedRequestHandler))).AddMediatorValidation();
        services.AddMediator(new FakeAssembly("second", typeof(DiNameValidator)));

        var descriptor = Assert.Single(services, static d => d.ServiceType == typeof(IValidator<DiValidatedRequest>));
        Assert.Equal(typeof(DiNameValidator), descriptor.ImplementationType);
    }

    // Assemblies passed to AddMediatorValidation are for validators kept outside the handlers' assemblies: scanned in
    // addition to AddMediator's, not instead of them.
    [Fact]
    public void AddMediatorValidation_WithAssemblies_ScansThemAsWellAsTheOnesGivenToAddMediator()
    {
        var services = new ServiceCollection();

        services
            .AddMediator(new FakeAssembly("handlers", typeof(DiValidatedRequestHandler), typeof(DiNameValidator)))
            .AddMediatorValidation(new FakeAssembly("validators", typeof(DiHost<object>.NameLengthValidator)));

        Assert.Equal(
            new[] { typeof(DiNameValidator), typeof(DiHost<object>.NameLengthValidator) },
            services.Where(static d => d.ServiceType == typeof(IValidator<DiValidatedRequest>)).Select(static d => d.ImplementationType));
    }

    // Every app that passed the same assemblies to both calls keeps working, and each assembly is still scanned for
    // validators only once: one GetTypes for the handler scan, one for the validator scan.
    [Fact]
    public void AddMediatorValidation_ScansAnAssemblyGivenToBothCallsOnce()
    {
        var assembly = new DiCountingAssembly(typeof(DiValidatedRequestHandler), typeof(DiNameValidator));
        var services = new ServiceCollection();

        services.AddMediator(assembly).AddMediatorValidation(assembly);

        Assert.Equal(2, assembly.GetTypesCallCount);
        Assert.Single(services, static d => d.ServiceType == typeof(IValidator<DiValidatedRequest>));
    }

    // OnMediatorAssemblies is how a companion package learns AddMediator's assemblies: those given so far, then each new one.
    [Fact]
    public void OnMediatorAssemblies_SeesTheAssembliesGivenBeforeAndAfterSubscribing()
    {
        var first = new FakeAssembly("first", typeof(DiPingHandler));
        var second = new FakeAssembly("second", typeof(DiRecordHandler));
        var seen = new List<Assembly>();
        var services = new ServiceCollection();

        services.AddMediator(first);
        services.OnMediatorAssemblies((_, assemblies) => seen.AddRange(assemblies));
        services.AddMediator(second);
        services.AddMediator(first);

        Assert.Equal(new Assembly[] { first, second }, seen);
    }

    [Fact]
    public void OnMediatorAssemblies_TheSameCallbackTwice_IsCalledOncePerAssembly()
    {
        var calls = 0;
        Action<IServiceCollection, IReadOnlyList<Assembly>> callback = (_, assemblies) => calls += assemblies.Count;
        var services = new ServiceCollection();

        services.OnMediatorAssemblies(callback);
        services.OnMediatorAssemblies(callback);
        services.AddMediator(new FakeAssembly(typeof(DiPingHandler)));

        Assert.Equal(1, calls);
    }

    // AddMediatorHandlers is the scan-only call: as with endpoint discovery, its assemblies are not handed on.
    [Fact]
    public void OnMediatorAssemblies_IsNotCalledForAssembliesGivenOnlyToAddMediatorHandlers()
    {
        var calls = 0;
        var services = new ServiceCollection();

        services.OnMediatorAssemblies((_, _) => calls++);
        services.AddMediatorHandlers(new FakeAssembly(typeof(DiPingHandler)));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task AddMediatorSentry_BeforeAddMediator_StillProducesAWorkingPipeline()
    {
        await using var provider = BuildProvider(static services =>
        {
            services.AddMediatorSentry();
            services.AddMediator(new FakeAssembly(typeof(DiPingHandler)));
        });
        using var scope = provider.CreateScope();

        Assert.IsType<SentryBehavior<DiPingRequest, string>>(
            Assert.Single(scope.ServiceProvider.GetServices<IPipelineBehavior<DiPingRequest, string>>()));
        Assert.Equal("pong", await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send<DiPingRequest, string>(new DiPingRequest()));
    }

    private static ServiceProvider BuildProvider(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        return services.BuildServiceProvider(validateScopes: true);
    }
}

// ----------------------------------------------------------------------
// Requests and handlers. Everything at namespace scope is visible to the assembly scans other test files
// run, so: exactly one handler per request, and every handler here has a parameterless constructor.
// ----------------------------------------------------------------------

public sealed class DiPingRequest : IRequest<string> { }

public sealed class DiPingHandler : IRequestHandler<DiPingRequest, string>
{
    public Task<string> Handle(DiPingRequest request, CancellationToken cancellationToken) => Task.FromResult("pong");
}

public sealed class DiPingCommand : IRequest { }

public sealed class DiPingCommandHandler : IRequestHandler<DiPingCommand>
{
    public Task Handle(DiPingCommand request, CancellationToken cancellationToken) => Task.CompletedTask;
}

// A record request, to show the scan treats records exactly like classes.
public sealed record DiRecordRequest(string Value) : IRequest<string>;

public sealed class DiRecordHandler : IRequestHandler<DiRecordRequest, string>
{
    public Task<string> Handle(DiRecordRequest request, CancellationToken cancellationToken) => Task.FromResult(request.Value);
}

public sealed class DiValidatedRequest : IRequest<string>
{
    public string Name { get; set; } = string.Empty;
}

public sealed class DiValidatedRequestHandler : IRequestHandler<DiValidatedRequest, string>
{
    public Task<string> Handle(DiValidatedRequest request, CancellationToken cancellationToken) => Task.FromResult(request.Name);
}

// Public and parameterless on purpose: an assembly-wide validator scan may register it, and it only ever
// applies to this file's own request type.
public sealed class DiNameValidator : AbstractValidator<DiValidatedRequest>
{
    public DiNameValidator() => RuleFor(request => request.Name).NotEmpty();
}

internal sealed class DiInternalRequest : IRequest<string> { }

// Internal, to prove the handler scan ignores visibility the way the validator scan does.
internal sealed class DiInternalHandler : IRequestHandler<DiInternalRequest, string>
{
    public Task<string> Handle(DiInternalRequest request, CancellationToken cancellationToken) => Task.FromResult("internal");
}

public sealed class DiMultiQuery : IRequest<string> { }

public sealed class DiMultiCommand : IRequest { }

public sealed class DiMultiHandler : IRequestHandler<DiMultiQuery, string>, IRequestHandler<DiMultiCommand>
{
    public Task<string> Handle(DiMultiQuery request, CancellationToken cancellationToken) => Task.FromResult("multi");

    public Task Handle(DiMultiCommand request, CancellationToken cancellationToken) => Task.CompletedTask;
}

// Open generic: the scan must skip it, because a partially open service type is rejected at
// BuildServiceProvider. Declared here so a regression would break every test that scans this assembly.
public sealed record DiOpenGenericRequest<T>(int Id) : IRequest<T>;

public sealed class DiOpenGenericHandler<T> : IRequestHandler<DiOpenGenericRequest<T>, T>
{
    public Task<T> Handle(DiOpenGenericRequest<T> request, CancellationToken cancellationToken) => Task.FromResult(default(T)!);
}

// Requests with no handler at namespace scope: their handlers live in DiHost<TMarker> (so the scan skips
// them), or they are meant to have none at all.
public sealed class DiUnhandledRequest : IRequest<string> { }

public sealed class DiUnhandledCommand : IRequest { }

public sealed class DiAbstractRequest : IRequest<string> { }

public sealed class DiContractRequest : IRequest<string> { }

public sealed class DiDependentRequest : IRequest<string> { }

public sealed class DiScopedRequest : IRequest<string> { }

public sealed class DiOverriddenRequest : IRequest<string> { }

public sealed class DiDuplicateRequest : IRequest<string> { }

public sealed class DiNullProbeRequest : IRequest<string> { }

public sealed class DiNullProbeCommand : IRequest { }

/// <summary>Implements neither request interface, so the mediator must refuse to dispatch it.</summary>
public sealed class DiNotARequest { }

public sealed class DiUnregisteredDependency
{
    public string Value => "dependency";
}

public sealed class DiScopedProbe
{
    public Guid Id { get; } = Guid.NewGuid();
}

public sealed class DiNullProbe
{
    public bool RequestWasNull { get; set; }

    public bool CommandWasNull { get; set; }
}

/// <summary>
/// Types nested in an open generic carry its type parameter, so <c>ContainsGenericParameters</c> is true and
/// the assembly scan other test files run skips every one of them. Tests hand the closed versions
/// (<c>DiHost&lt;object&gt;.X</c>) to a scan of their own, or register them explicitly.
/// </summary>
public static class DiHost<TMarker>
{
    public sealed class MissingDependencyHandler(DiUnregisteredDependency dependency) : IRequestHandler<DiDependentRequest, string>
    {
        public Task<string> Handle(DiDependentRequest request, CancellationToken cancellationToken) => Task.FromResult(dependency.Value);
    }

    public sealed class ScopedProbeHandler(DiScopedProbe probe) : IRequestHandler<DiScopedRequest, string>
    {
        public Task<string> Handle(DiScopedRequest request, CancellationToken cancellationToken) => Task.FromResult(probe.Id.ToString());
    }

    public sealed class NullProbeHandler(DiNullProbe probe) : IRequestHandler<DiNullProbeRequest, string>
    {
        public Task<string> Handle(DiNullProbeRequest request, CancellationToken cancellationToken)
        {
            probe.RequestWasNull = request is null;
            return Task.FromResult("handled");
        }
    }

    public sealed class NullProbeCommandHandler(DiNullProbe probe) : IRequestHandler<DiNullProbeCommand>
    {
        public Task Handle(DiNullProbeCommand request, CancellationToken cancellationToken)
        {
            probe.CommandWasNull = request is null;
            return Task.CompletedTask;
        }
    }

    public sealed class ExplicitHandler : IRequestHandler<DiOverriddenRequest, string>
    {
        public Task<string> Handle(DiOverriddenRequest request, CancellationToken cancellationToken) => Task.FromResult("explicit");
    }

    public sealed class ScannedHandler : IRequestHandler<DiOverriddenRequest, string>
    {
        public Task<string> Handle(DiOverriddenRequest request, CancellationToken cancellationToken) => Task.FromResult("scanned");
    }

    public sealed class FirstDuplicateHandler : IRequestHandler<DiDuplicateRequest, string>
    {
        public Task<string> Handle(DiDuplicateRequest request, CancellationToken cancellationToken) => Task.FromResult("first");
    }

    public sealed class SecondDuplicateHandler : IRequestHandler<DiDuplicateRequest, string>
    {
        public Task<string> Handle(DiDuplicateRequest request, CancellationToken cancellationToken) => Task.FromResult("second");
    }

    /// <summary>A second validator for <see cref="DiValidatedRequest"/>, kept here so no assembly-wide scan picks it up.</summary>
    public sealed class NameLengthValidator : AbstractValidator<DiValidatedRequest>
    {
        public NameLengthValidator() => RuleFor(static request => request.Name).MaximumLength(50);
    }

    public abstract class AbstractHandler : IRequestHandler<DiAbstractRequest, string>
    {
        public abstract Task<string> Handle(DiAbstractRequest request, CancellationToken cancellationToken);
    }

    /// <summary>The handler an app actually ships when it factors shared plumbing into a base class.</summary>
    public sealed class ConcreteSubclassHandler : AbstractHandler
    {
        public override Task<string> Handle(DiAbstractRequest request, CancellationToken cancellationToken) => Task.FromResult("concrete");
    }

    public interface IHandlerContract : IRequestHandler<DiContractRequest, string>
    {
    }

    /// <summary>A singleton that captures the scoped sender — the captive dependency scope validation exists for.</summary>
    public sealed class SenderCapturingRootService(ISender sender)
    {
        public ISender Sender { get; } = sender;
    }

    /// <summary>
    /// An app-supplied <c>ISender</c>, registered before <c>AddMediator</c> — the shape a decorator takes.
    /// </summary>
    public sealed class ReplacementSender : ISender
    {
        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
            => Task.FromResult<object?>(null);

        public Task<TResponse> Send<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest<TResponse>
            => Task.FromResult(default(TResponse)!);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest
            => Task.CompletedTask;
    }
}

/// <summary>
/// A <see cref="FakeAssembly"/> that also counts how often its type list was read, so a test can tell
/// "the registry skipped this assembly" apart from "it rescanned it and TryAdd hid the duplicate".
/// </summary>
public sealed class DiCountingAssembly(params Type[] types) : Assembly
{
    private int getTypesCalls;

    public int GetTypesCallCount => Volatile.Read(ref getTypesCalls);

    public override Type[] GetTypes()
    {
        Interlocked.Increment(ref getTypesCalls);
        return types;
    }

    public override Type[] GetExportedTypes() => types.Where(static type => type.IsVisible).ToArray();

    public override AssemblyName GetName() => new("Phoenix.Mediator.Tests.DiCounting");

    public override string FullName => "Phoenix.Mediator.Tests.DiCounting";
}

/// <summary>
/// An assembly whose types cannot be read at all — the failure mode a corrupt or mismatched-architecture
/// binary produces, as opposed to the partial type load <see cref="PartiallyLoadableFakeAssembly"/> models.
/// </summary>
public sealed class DiUnreadableAssembly : Assembly
{
    public override Type[] GetTypes() => throw new BadImageFormatException("Simulated unreadable assembly.", "DiUnreadableAssembly.dll");

    public override AssemblyName GetName() => new("Phoenix.Mediator.Tests.DiUnreadable");

    public override string FullName => "Phoenix.Mediator.Tests.DiUnreadable";
}
