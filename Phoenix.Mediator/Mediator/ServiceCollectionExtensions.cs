using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Web;
using System.Reflection;

namespace Phoenix.Mediator.Mediator;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMediator(this IServiceCollection services)
    {
        return AddMediatorCore(services, configureOptions: null);
    }

    public static IServiceCollection AddMediator(this IServiceCollection services, Action<MediatorOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        return AddMediatorCore(services, configureOptions);
    }

    private static IServiceCollection AddMediatorCore(IServiceCollection services, Action<MediatorOptions>? configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<MediatorOptions>();
        // TryAddEnumerable rather than OptionsBuilder.Validate(), which appends on every call and would
        // run the check (and repeat its failure message) once per AddMediator call.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<MediatorOptions>, MediatorOptionsValidator>());

        if (configureOptions is not null)
            services.Configure(configureOptions);

        services.AddHealthChecks();
        // Keeps [FromRoute]/[FromQuery]/[FromHeader] request members out of the Minimal API JSON body and its OpenAPI schema.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<JsonOptions>, RequestBodyJsonOptionsSetup>());
        services.GetOrCreateAssemblyRegistry();
        // IMPORTANT: Mediator must be scoped so request handlers can depend on scoped services
        // (e.g. current user, DbContext, HttpContext-related services).
        services.TryAddScoped<Mediator>();
        // Forward to the scope's Mediator so ISender and Mediator resolve to the same instance.
        services.TryAddScoped<ISender>(static provider => provider.GetRequiredService<Mediator>());
        // Lets IPipelineBehavior and IRequestBehavior registrations run in one registration order. A factory rather
        // than an instance: it reads this same collection, and nothing needs it before the first send.
        services.TryAddSingleton(_ => new PipelineBehaviorOrder(services));
        // Off unless MediatorOptions.MissingHandlerHandling says otherwise; registered either way so the option
        // alone turns it on.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, HandlerRegistrationDiagnostics>());

        // Pipeline behaviors are opt-in via companion packages:
        // - Phoenix.Mediator.Sentry      -> services.AddMediatorSentry()
        // - Phoenix.Mediator.Validation  -> services.AddMediatorValidation(assemblies)
        // - your own                     -> services.AddMediatorBehavior(typeof(MyBehavior<>))
        // Register them in the order you want them to run (first registered = OUTERMOST).
        return services;
    }

    /// <summary>
    /// Registers a pipeline behavior — an <see cref="IRequestBehavior{TRequest}"/> (every request, written once), an
    /// <see cref="IPipelineBehavior{TRequest, TResponse}"/> or an <see cref="IPipelineBehavior{TRequest}"/> — either open
    /// generic (<c>typeof(LoggingBehavior&lt;&gt;)</c>) or closed. Behaviors run in registration order, first registered
    /// outermost, whichever interface they implement. Registering the same type twice has no effect.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="behaviorType">The behavior's implementation type.</param>
    /// <param name="lifetime">Its lifetime. Transient by default, like the built-in behaviors.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="behaviorType"/> is abstract, or implements none of the behavior interfaces in a form the
    /// container can close (see the remarks).
    /// </exception>
    /// <remarks>
    /// The container closes an open generic behavior by handing it the service's type arguments in order, so the
    /// class's type parameters must be exactly the interface's arguments. <c>LoggingBehavior&lt;TRequest&gt; :
    /// IRequestBehavior&lt;TRequest&gt;</c> works; <c>Behavior&lt;TRequest&gt; : IPipelineBehavior&lt;TRequest, string&gt;</c>
    /// cannot be registered open, and is rejected here rather than failing on the first request.
    /// </remarks>
    public static IServiceCollection AddMediatorBehavior(this IServiceCollection services, Type behaviorType, ServiceLifetime lifetime = ServiceLifetime.Transient)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(behaviorType);

        if (!behaviorType.IsClass || behaviorType.IsAbstract)
            throw new ArgumentException($"'{behaviorType.FullName}' must be a non-abstract class.", nameof(behaviorType));

        var registered = false;

        foreach (var contract in behaviorType.GetInterfaces())
        {
            if (!contract.IsGenericType)
                continue;

            var definition = contract.GetGenericTypeDefinition();
            if (definition != typeof(IRequestBehavior<>) && definition != typeof(IPipelineBehavior<,>) && definition != typeof(IPipelineBehavior<>))
                continue;

            if (behaviorType.IsGenericTypeDefinition)
            {
                if (!contract.GetGenericArguments().SequenceEqual(behaviorType.GetGenericArguments()))
                    continue;

                services.TryAddEnumerable(ServiceDescriptor.Describe(definition, behaviorType, lifetime));
            }
            else
            {
                EnsureDispatchable(behaviorType, contract);
                services.TryAddEnumerable(ServiceDescriptor.Describe(contract, behaviorType, lifetime));
            }

            registered = true;
        }

        if (!registered)
        {
            throw new ArgumentException(
                $"'{behaviorType.FullName}' does not implement IRequestBehavior<TRequest>, IPipelineBehavior<TRequest, TResponse> or " +
                "IPipelineBehavior<TRequest> in a form that can be registered. An open generic behavior's type parameters must be exactly " +
                "the interface's type arguments, in the same order.",
                nameof(behaviorType));
        }

        return services;
    }

    /// <summary>
    /// A closed behavior only runs for the exact request type it names: the container applies no variance, so
    /// <c>IRequestBehavior&lt;object&gt;</c> is never asked for in place of <c>IRequestBehavior&lt;CreateOrder&gt;</c>. Naming a type
    /// the mediator never dispatches as — an interface (those are dispatched by the runtime type) or something that is not a
    /// request at all — registers a behavior that silently never runs, so it is refused instead.
    /// </summary>
    private static void EnsureDispatchable(Type behaviorType, Type contract)
    {
        var requestType = contract.GetGenericArguments()[0];
        if (!requestType.IsInterface && MediatorRequestTypes.IsRequest(requestType))
            return;

        throw new ArgumentException(
            $"'{behaviorType.FullName}' implements {TypeNames.Display(contract)}, which would never run: a closed behavior only runs " +
            $"for requests dispatched as exactly {TypeNames.Display(requestType)}, and " +
            (requestType.IsInterface ? "requests are never dispatched as an interface." : "that is not a request type.") +
            $" Make it generic instead — {behaviorType.Name}<TRequest> : IRequestBehavior<TRequest> — and constrain TRequest to limit which " +
            "requests it runs for.",
            nameof(behaviorType));
    }

    /// <summary>
    /// Registers Mediator plus request handlers found in the provided assemblies.
    /// </summary>
    public static IServiceCollection AddMediator(this IServiceCollection services, params Assembly[] assemblies)
    {
        return AddMediatorWithAssemblies(services, configureOptions: null, assemblies);
    }

    public static IServiceCollection AddMediator(this IServiceCollection services, Action<MediatorOptions> configureOptions, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        return AddMediatorWithAssemblies(services, configureOptions, assemblies);
    }

    private static IServiceCollection AddMediatorWithAssemblies(IServiceCollection services, Action<MediatorOptions>? configureOptions, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        AddMediatorCore(services, configureOptions);

        if (assemblies.Length > 0)
        {
            var newAssemblies = services.GetOrCreateAssemblyRegistry().AddAssemblies(assemblies.Distinct());

            if (newAssemblies.Length > 0)
            {
                services.AddMediatorHandlers(newAssemblies);
            }
        }

        return services;
    }

    /// <summary>
    /// Scans assemblies for IRequestHandler&lt;TRequest&gt; and IRequestHandler&lt;TRequest,TResponse&gt; implementations and registers them.
    /// <para>
    /// Open generic handlers (e.g. <c>GetByIdHandler&lt;TEntity&gt;</c>) are skipped: the service type would be a
    /// partially open interface, which the DI container rejects at <c>BuildServiceProvider</c>. Register a closed
    /// handler per request type instead.
    /// </para>
    /// <para>
    /// Only assemblies passed to <c>AddMediator(...)</c> (or to <c>MapEndpoints(...)</c>) are scanned for
    /// endpoint groups. Registering handlers through this method alone does not make its endpoint groups discoverable.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">Two handlers in the scanned assemblies handle the same request type.</exception>
    public static IServiceCollection AddMediatorHandlers(this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (assemblies is null || assemblies.Length == 0)
            throw new ArgumentException("At least one assembly must be provided.", nameof(assemblies));

        // Tracks what this scan registered, so two handlers for one request fail loudly instead of
        // TryAdd silently keeping whichever type the reflection order happened to yield first.
        var registeredByScan = new Dictionary<Type, Type>();

        foreach (var assembly in assemblies.Distinct())
        {
            foreach (var type in AssemblyTypeLoader.GetLoadableTypes(assembly))
            {
                if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters)
                    continue;

                var interfaces = type.GetInterfaces();
                foreach (var it in interfaces)
                {
                    if (!it.IsGenericType)
                        continue;

                    var def = it.GetGenericTypeDefinition();
                    if (def == typeof(IRequestHandler<,>) || def == typeof(IRequestHandler<>))
                    {
                        if (registeredByScan.TryGetValue(it, out var alreadyRegistered) && alreadyRegistered != type)
                        {
                            throw new InvalidOperationException(
                                $"Multiple handlers implement '{it}': '{alreadyRegistered.FullName}' and '{type.FullName}'. " +
                                "Remove one, or register the handler you want explicitly before calling AddMediator/AddMediatorHandlers.");
                        }

                        registeredByScan[it] = type;
                        services.TryAddTransient(it, type);
                    }
                }
            }
        }

        return services;
    }

    private static MediatorAssemblyRegistry GetOrCreateAssemblyRegistry(this IServiceCollection services)
    {
        var existingRegistry = services
            .FirstOrDefault(descriptor => descriptor.ServiceType == typeof(MediatorAssemblyRegistry))
            ?.ImplementationInstance as MediatorAssemblyRegistry;

        if (existingRegistry is not null)
            return existingRegistry;

        var registry = new MediatorAssemblyRegistry();
        services.AddSingleton(registry);
        return registry;
    }
}


