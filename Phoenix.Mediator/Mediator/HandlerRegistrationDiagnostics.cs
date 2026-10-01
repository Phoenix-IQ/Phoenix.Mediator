using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phoenix.Mediator.Abstractions;
using System.Reflection;
using System.Text;

namespace Phoenix.Mediator.Mediator;

/// <summary>
/// The startup check behind <see cref="MediatorOptions.MissingHandlerHandling"/>: every concrete request type in the
/// assemblies passed to <c>AddMediator(...)</c> should have a handler registered. Without it, a forgotten handler, or a
/// handler assembly that was never passed in, only shows up when the request is first sent — typically as a 500.
/// </summary>
internal sealed class HandlerRegistrationDiagnostics(
    MediatorAssemblyRegistry registry,
    IServiceProvider services,
    IOptions<MediatorOptions> options,
    ILoggerFactory? loggerFactory = null) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        MissingHandlerHandling handling;
        try
        {
            handling = options.Value.MissingHandlerHandling;
        }
        catch (OptionsValidationException)
        {
            // MediatorOptions are validated when read, and an invalid value has always surfaced where the mediator
            // reads it. Reading them here must not move that failure to host startup.
            return Task.CompletedTask;
        }

        if (handling == MissingHandlerHandling.None)
            return Task.CompletedTask;

        // Asks whether a handler is registered without constructing one. A container that cannot answer that
        // (a third-party one) gets no check rather than a check that builds every handler at startup.
        if (services.GetService<IServiceProviderIsService>() is not { } isService)
            return Task.CompletedTask;

        var missing = FindRequestsWithoutHandlers(registry.GetAssemblies(), isService);
        if (missing.Count == 0)
            return Task.CompletedTask;

        var report = BuildReport(missing);

        if (handling == MissingHandlerHandling.Throw)
            throw new InvalidOperationException(report);

        // The report goes in as an argument rather than as the message template: it is free text, and a template
        // is parsed for {placeholders}.
        loggerFactory?.CreateLogger(typeof(HandlerRegistrationDiagnostics).FullName!).LogWarning("{MissingHandlerReport}", report);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Each concrete, closed request type paired with the handler interface nothing implements. Abstract requests
    /// are base types by design, and open generic ones can only be checked once closed, which a scan cannot see.
    /// </summary>
    internal static IReadOnlyList<(Type Request, Type Handler)> FindRequestsWithoutHandlers(IEnumerable<Assembly> assemblies, IServiceProviderIsService isService)
    {
        var missing = new List<(Type Request, Type Handler)>();

        foreach (var assembly in assemblies)
        {
            foreach (var type in AssemblyTypeLoader.GetLoadableTypes(assembly))
            {
                if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
                    continue;

                var responseType = MediatorRequestTypes.GetResponseType(type);
                var responseHandler = responseType is null ? null : typeof(IRequestHandler<,>).MakeGenericType(type, responseType);
                var voidHandler = typeof(IRequest).IsAssignableFrom(type) ? typeof(IRequestHandler<>).MakeGenericType(type) : null;

                if (responseHandler is null && voidHandler is null)
                    continue;

                // A type implementing both request interfaces is covered by either handler.
                if ((responseHandler is not null && isService.IsService(responseHandler))
                    || (voidHandler is not null && isService.IsService(voidHandler)))
                {
                    continue;
                }

                missing.Add((type, responseHandler ?? voidHandler!));
            }
        }

        return missing;
    }

    private static string BuildReport(IReadOnlyList<(Type Request, Type Handler)> missing)
    {
        var report = new StringBuilder()
            .Append(missing.Count == 1 ? "1 request type has" : $"{missing.Count} request types have")
            .AppendLine(" no handler registered. Sending one throws InvalidOperationException:")
            .AppendLine();

        foreach (var (request, handler) in missing.OrderBy(static entry => entry.Request.FullName, StringComparer.Ordinal))
            report.Append("  - ").Append(request.FullName).Append(": nothing implements ").AppendLine(TypeNames.Display(handler));

        return report
            .AppendLine()
            .Append("Add the missing handlers, pass the assemblies that hold them to AddMediator(...), or set ")
            .Append("MediatorOptions.MissingHandlerHandling to None to skip this check.")
            .ToString();
    }
}
