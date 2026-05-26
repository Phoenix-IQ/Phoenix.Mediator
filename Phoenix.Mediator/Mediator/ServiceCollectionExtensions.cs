using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Phoenix.Mediator.Abstractions;
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

        services.AddOptions<MediatorOptions>()
            .Validate(
                options => options.EmptyResponseStatusCode is EmptyResponseStatusCode.Ok or EmptyResponseStatusCode.NoContent,
                MediatorMessages.InvalidEmptyResponseStatusCode);

        if (configureOptions is not null)
            services.Configure(configureOptions);

        services.AddHealthChecks();
        services.GetOrCreateAssemblyRegistry();
        // IMPORTANT: Mediator must be scoped so request handlers can depend on scoped services
        // (e.g. current user, DbContext, HttpContext-related services).
        services.TryAddScoped<Mediator>();
        services.TryAddScoped<ISender, Mediator>();

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
    /// </summary>
    public static IServiceCollection AddMediatorHandlers(this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (assemblies is null || assemblies.Length == 0)
            throw new ArgumentException("At least one assembly must be provided.", nameof(assemblies));

        foreach (var assembly in assemblies.Distinct())
        {
            foreach (var type in assembly.DefinedTypes)
            {
                if (!type.IsClass || type.IsAbstract)
                    continue;

                var interfaces = type.ImplementedInterfaces;
                foreach (var it in interfaces)
                {
                    if (!it.IsGenericType)
                        continue;

                    var def = it.GetGenericTypeDefinition();
                    if (def == typeof(IRequestHandler<,>) || def == typeof(IRequestHandler<>))
                    {
                        services.TryAddTransient(it, type.AsType());
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


