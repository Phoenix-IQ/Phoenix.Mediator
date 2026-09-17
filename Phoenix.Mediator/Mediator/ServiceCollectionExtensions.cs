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
    /// </summary>
    public static IServiceCollection AddMediatorHandlers(this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (assemblies is null || assemblies.Length == 0)
            throw new ArgumentException("At least one assembly must be provided.", nameof(assemblies));

        foreach (var assembly in assemblies.Distinct())
        {
            foreach (var type in AssemblyTypeLoader.GetLoadableTypes(assembly))
            {
                if (!type.IsClass || type.IsAbstract)
                    continue;

                var interfaces = type.GetInterfaces();
                foreach (var it in interfaces)
                {
                    if (!it.IsGenericType)
                        continue;

                    var def = it.GetGenericTypeDefinition();
                    if (def == typeof(IRequestHandler<,>) || def == typeof(IRequestHandler<>))
                    {
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


