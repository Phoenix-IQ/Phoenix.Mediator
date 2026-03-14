using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using FluentValidation;
using Phoenix.Mediator.Abstractions;
using System.Reflection;

namespace Phoenix.Mediator.Mediator;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMediator(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHealthChecks();
        services.GetOrCreateAssemblyRegistry();
        // IMPORTANT: Mediator must be scoped so request handlers can depend on scoped services
        // (e.g. current user, DbContext, HttpContext-related services).
        services.TryAddScoped<Mediator>();
        services.TryAddScoped<ISender, Mediator>();

        // Pipelines:
        // - GetServices<T>() returns in registration order
        // - Mediator wraps from the end, so:
        //   - first registered runs OUTERMOST (first to execute)
        //   - last registered runs INNERMOST (closest to the handler)
        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IPipelineBehavior<,>), typeof(SentryBehavior<,>)));
        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IPipelineBehavior<>), typeof(SentryBehavior<>)));
        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>)));
        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IPipelineBehavior<>), typeof(ValidationBehavior<>)));

        return services;
    }

    /// <summary>
    /// Registers Mediator plus request handlers found in the provided assemblies.
    /// </summary>
    public static IServiceCollection AddMediator(this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        AddMediator(services);

        if (assemblies.Length > 0)
        {
            var newAssemblies = services.GetOrCreateAssemblyRegistry().AddAssemblies(assemblies.Distinct());

            if (newAssemblies.Length > 0)
            {
                services.AddMediatorHandlers(newAssemblies);
                services.AddMediatorValidators(newAssemblies);
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
                        services.AddTransient(it, type.AsType());
                    }
                }
            }
        }

        return services;
    }

    /// <summary>
    /// Registers FluentValidation validators (IValidator&lt;T&gt;/AbstractValidator&lt;T&gt;) found in the provided assemblies.
    /// </summary>
    public static IServiceCollection AddMediatorValidators(this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (assemblies is null || assemblies.Length == 0)
            throw new ArgumentException("At least one assembly must be provided.", nameof(assemblies));

        foreach (var assembly in assemblies.Distinct())
        {
            // Uses FluentValidation.DependencyInjectionExtensions
            services.AddValidatorsFromAssembly(assembly);
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


