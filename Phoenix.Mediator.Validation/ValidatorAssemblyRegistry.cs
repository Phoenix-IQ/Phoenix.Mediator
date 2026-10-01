using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Phoenix.Mediator.Validation;

/// <summary>
/// Remembers which assemblies FluentValidation's scan has already seen, so an assembly that reaches it more than once —
/// given to both <c>AddMediator(...)</c> and <c>AddMediatorValidation(...)</c>, or to either twice — is scanned once.
/// </summary>
internal sealed class ValidatorAssemblyRegistry(IServiceCollection services)
{
    private readonly object gate = new();
    private readonly HashSet<Assembly> assemblies = [];

    public Assembly[] Add(IEnumerable<Assembly> candidates)
    {
        lock (gate)
        {
            var added = new List<Assembly>();
            foreach (var assembly in candidates.Where(static a => a is not null))
            {
                if (assemblies.Add(assembly))
                    added.Add(assembly);
            }

            return added.ToArray();
        }
    }

    public Assembly[] Snapshot()
    {
        lock (gate)
            return [.. assemblies];
    }

    /// <summary>
    /// Whether anything registered an <see cref="IValidator{T}"/> outside the assembly scan.
    /// <para>
    /// Only meaningful once the scan has found nothing: any descriptor left at that point came from the
    /// app registering its validators by hand, which <c>AddMediatorValidation()</c> supports and which
    /// must not be reported as "nothing is validated".
    /// </para>
    /// <para>
    /// This reads the live <see cref="IServiceCollection"/> rather than a count taken during registration,
    /// because manual registrations can be added after the last <c>AddMediatorValidation(...)</c> call.
    /// It runs at startup, when the collection is complete.
    /// </para>
    /// </summary>
    public bool HasValidatorsRegisteredOutsideScan()
    {
        return services.Any(static descriptor =>
            descriptor.ServiceType.IsGenericType
            && descriptor.ServiceType.GetGenericTypeDefinition() == typeof(IValidator<>));
    }
}
