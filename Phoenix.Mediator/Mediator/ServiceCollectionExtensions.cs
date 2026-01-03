using Microsoft.Extensions.DependencyInjection;
using Phoenix.Mediator.Abstractions;
using System.Reflection;

namespace Phoenix.Mediator.Mediator;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMediator(this IServiceCollection services)
    {
        services.AddHealthChecks();
        services.AddSingleton<Mediator>();
        services.AddSingleton<ISender>(sp => sp.GetRequiredService<Mediator>());

        // Pipelines:
        // - GetServices<T>() returns in registration order
        // - Mediator wraps from the end, so:
        //   - first registered runs OUTERMOST (first to execute)
        //   - last registered runs INNERMOST (closest to the handler)
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(SentryBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<>), typeof(SentryBehavior<>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<>), typeof(ValidationBehavior<>));

        return services;
    }

    /// <summary>
    /// Registers Mediator plus request handlers found in the provided assemblies.
    /// </summary>
    public static IServiceCollection AddMediator(this IServiceCollection services, params Assembly[] assemblies)
    {
        AddMediator(services);

        if (assemblies is { Length: > 0 })
            services.AddMediatorHandlers(assemblies);

        return services;
    }

    /// <summary>
    /// Scans assemblies for IRequestHandler&lt;TRequest&gt; and IRequestHandler&lt;TRequest,TResponse&gt; implementations and registers them.
    /// </summary>
    public static IServiceCollection AddMediatorHandlers(this IServiceCollection services, params Assembly[] assemblies)
    {
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

    [Obsolete("Use AddMediator(...) instead.")]
    public static IServiceCollection AddCustomMediator(this IServiceCollection services) => AddMediator(services);

    [Obsolete("Use AddMediator(...) instead.")]
    public static IServiceCollection AddCustomMediator(this IServiceCollection services, params Assembly[] assemblies) =>
        AddMediator(services, assemblies);

    [Obsolete("Use AddMediatorHandlers(...) instead.")]
    public static IServiceCollection AddCustomMediatorHandlers(this IServiceCollection services, params Assembly[] assemblies) =>
        AddMediatorHandlers(services, assemblies);
}


