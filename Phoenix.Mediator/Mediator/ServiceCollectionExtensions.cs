using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        // Pipeline behaviors are opt-in via companion packages:
        // - Phoenix.Mediator.Sentry      -> services.AddMediatorSentry()
        // - Phoenix.Mediator.Validation  -> services.AddMediatorValidation(assemblies)
        // Register them in the order you want them to run (first registered = OUTERMOST).
        return services;
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


