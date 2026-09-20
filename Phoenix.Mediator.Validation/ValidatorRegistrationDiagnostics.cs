using FluentValidation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace Phoenix.Mediator.Validation;

/// <summary>
/// Startup check for validators that the assembly scan cannot register, and for the case where the
/// validation behavior runs with no validators at all.
/// <para>
/// Every case reported here is otherwise silent: the behavior resolves an empty
/// <c>IEnumerable&lt;IValidator&lt;TRequest&gt;&gt;</c>, finds no failures and lets the request through, so
/// invalid input is accepted exactly as if it had been validated. The warnings run once at host startup.
/// </para>
/// </summary>
internal sealed class ValidatorRegistrationDiagnostics(ValidatorAssemblyRegistry registry, ILoggerFactory? loggerFactory = null) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var assemblies = registry.Snapshot();
        var warnings = Analyze(assemblies, registry.HasValidatorsRegisteredOutsideScan);

        if (warnings.Count > 0)
        {
            var logger = loggerFactory?.CreateLogger(typeof(ValidatorRegistrationDiagnostics).FullName!);
            foreach (var warning in warnings)
                logger?.LogWarning("Phoenix.Mediator validation: {Problem}", warning);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <param name="assemblies">The assemblies handed to <c>AddMediatorValidation(...)</c>.</param>
    /// <param name="hasValidatorsRegisteredOutsideScan">
    /// Called only when the scan found nothing, to tell "the app registered its validators by hand"
    /// apart from "nothing is validated at all".
    /// </param>
    internal static IReadOnlyList<string> Analyze(IReadOnlyCollection<Assembly> assemblies, Func<bool> hasValidatorsRegisteredOutsideScan)
    {
        var warnings = new List<string>();
        var registered = new List<Type>();
        var openGenerics = new List<Type>();

        foreach (var assembly in assemblies)
        {
            foreach (var type in GetLoadableTypes(assembly))
            {
                // Abstract validators are base classes by design, and open generics are judged below
                // once the closed types are known.
                if (!type.IsClass || type.IsAbstract || GetValidatedType(type) is null)
                    continue;

                if (type.ContainsGenericParameters)
                {
                    openGenerics.Add(type);
                    continue;
                }

                // GetConstructors() returns public instance constructors, which is exactly what the
                // container will look for.
                if (type.GetConstructors().Length == 0)
                {
                    warnings.Add(
                        $"Validator '{Describe(type)}' has no public constructor, so the container cannot construct it and resolving it throws " +
                        "\"A suitable constructor ... could not be located\" on the first request it applies to. Make the constructor public.");
                    continue;
                }

                registered.Add(type);
            }
        }

        foreach (var type in openGenerics)
        {
            if (IsClosedBy(type, registered))
                continue;

            warnings.Add(
                $"Validator '{Describe(type)}' is never registered because it still has unbound type parameters, so requests of type " +
                $"'{Describe(GetValidatedType(type)!)}' are not validated. A validator nested inside a generic type inherits that type's " +
                "parameters and hits this even when it looks closed. Move it out of the generic type, or register a closed version explicitly.");
        }

        if (registered.Count == 0 && !hasValidatorsRegisteredOutsideScan())
        {
            warnings.Add(assemblies.Count == 0
                ? "AddMediatorValidation() was called without assemblies, so it registered the pipeline behavior and no validators. " +
                  "Every request passes validation. Pass the assemblies holding your IValidator<T> implementations, " +
                  "e.g. AddMediatorValidation(typeof(SomeValidator).Assembly)."
                : $"AddMediatorValidation(...) found no FluentValidation validators in the scanned assemblies ({string.Join(", ", assemblies.Select(static a => a.GetName().Name))}). " +
                  "Every request passes validation. Pass the assemblies holding your IValidator<T> implementations — they are often not the " +
                  "same assemblies as your handlers.");
        }

        return warnings;
    }

    private static Type? GetValidatedType(Type type)
    {
        foreach (var contract in type.GetInterfaces())
        {
            if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IValidator<>))
                return contract.GetGenericArguments()[0];
        }

        return null;
    }

    /// <summary>
    /// Whether a registered validator closes <paramref name="openType"/>, which makes the open type an
    /// intentional base rather than a validator that silently never runs.
    /// </summary>
    private static bool IsClosedBy(Type openType, List<Type> registered)
    {
        foreach (var candidate in registered)
        {
            for (var baseType = candidate.BaseType; baseType is not null; baseType = baseType.BaseType)
            {
                if (baseType.IsGenericType && baseType.GetGenericTypeDefinition() == openType)
                    return true;
            }
        }

        return false;
    }

    private static string Describe(Type type) => type.FullName ?? type.Name;

    /// <summary>
    /// Mirrors the handler scan's tolerance for assemblies whose types don't all load: a partial load must
    /// not turn a startup warning into a startup crash.
    /// </summary>
    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(static type => type is not null)!;
        }
    }
}
