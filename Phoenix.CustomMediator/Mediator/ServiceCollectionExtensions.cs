using Microsoft.Extensions.DependencyInjection;
using Phoenix.CustomMediator.Abstractions;

namespace Phoenix.CustomMediator.Mediator;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCustomMediator(this IServiceCollection services)
    {
        services.AddSingleton<CustomMediator>();
        services.AddSingleton<ISender>(sp => sp.GetRequiredService<CustomMediator>());

        // Pipelines (order = registration order; last registered runs closest to handler)
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<>), typeof(ValidationBehavior<>));

        return services;
    }
}


