using Microsoft.Extensions.DependencyInjection;
using Phoenix.CustomMediator.Abstractions;

namespace Phoenix.CustomMediator.Mediator;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCustomMediator(this IServiceCollection services)
    {
        services.AddSingleton<CustomMediator>();
        services.AddSingleton<ISender>(sp => sp.GetRequiredService<CustomMediator>());

        // Pipelines:
        // - GetServices<T>() returns in registration order
        // - CustomMediator wraps from the end, so:
        //   - first registered runs OUTERMOST (first to execute)
        //   - last registered runs INNERMOST (closest to the handler)
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(SentryBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<>), typeof(SentryBehavior<>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<>), typeof(ValidationBehavior<>));

        return services;
    }
}


