using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Tests.Infrastructure;
using Xunit;

namespace Phoenix.Mediator.Tests;

/// <summary>
/// <see cref="MediatorOptions.MissingHandlerHandling"/>: the startup check for request types in the scanned assemblies
/// that no handler is registered for. Each test scans a <see cref="FakeAssembly"/> holding exactly the types it needs,
/// and starts a real generic host, because the check is a hosted service.
/// </summary>
public sealed class MissingHandlerDiagnosticsTests
{
    // Off by default: turning it on can surface request types an existing app deliberately leaves without a handler.
    [Fact]
    public async Task Startup_ByDefault_DoesNotCheckForMissingHandlers()
    {
        var logs = new RecordingLoggerProvider();
        using var host = BuildHost(logs, configure: null, typeof(MhOrphanQuery));

        await host.StartAsync();

        Assert.Empty(logs.Warnings);
        await host.StopAsync();
    }

    [Fact]
    public async Task Startup_WithThrow_FailsNamingTheRequestAndTheHandlerItNeeds()
    {
        using var host = BuildHost(
            new RecordingLoggerProvider(),
            options => options.MissingHandlerHandling = MissingHandlerHandling.Throw,
            typeof(MhHandledQuery), typeof(MhHandledQueryHandler), typeof(MhOrphanQuery), typeof(MhOrphanCommand));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.StartsWith("2 request types have no handler registered.", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"{typeof(MhOrphanQuery).FullName}: nothing implements IRequestHandler<MhOrphanQuery, String>", exception.Message);
        Assert.Contains($"{typeof(MhOrphanCommand).FullName}: nothing implements IRequestHandler<MhOrphanCommand>", exception.Message);
        Assert.DoesNotContain(nameof(MhHandledQuery) + ":", exception.Message);
        Assert.Contains("MissingHandlerHandling", exception.Message);
    }

    [Fact]
    public async Task Startup_WithWarn_LogsTheReportAndStarts()
    {
        var logs = new RecordingLoggerProvider();
        using var host = BuildHost(logs, options => options.MissingHandlerHandling = MissingHandlerHandling.Warn, typeof(MhOrphanQuery));

        await host.StartAsync();

        var warning = Assert.Single(logs.Warnings);
        Assert.StartsWith("1 request type has no handler registered.", warning.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(MhOrphanQuery).FullName!, warning.Message);
        await host.StopAsync();
    }

    [Fact]
    public async Task Startup_WithEveryRequestHandled_ReportsNothing()
    {
        var logs = new RecordingLoggerProvider();
        using var host = BuildHost(
            logs,
            options => options.MissingHandlerHandling = MissingHandlerHandling.Throw,
            typeof(MhHandledQuery), typeof(MhHandledQueryHandler));

        await host.StartAsync();

        Assert.Empty(logs.Warnings);
        await host.StopAsync();
    }

    // Abstract requests are base types by design, and an open generic request can only be checked once closed, which
    // a scan cannot see. Neither is a missing handler.
    [Fact]
    public async Task Startup_DoesNotReportAbstractOrOpenGenericRequestTypes()
    {
        using var host = BuildHost(
            new RecordingLoggerProvider(),
            options => options.MissingHandlerHandling = MissingHandlerHandling.Throw,
            typeof(MhAbstractQuery), typeof(MhGenericQuery<>));

        await host.StartAsync();
        await host.StopAsync();
    }

    // The check asks the container, not the scan: a handler registered by hand counts.
    [Fact]
    public async Task Startup_CountsAHandlerRegisteredOutsideTheScan()
    {
        using var host = BuildHost(
            new RecordingLoggerProvider(),
            options => options.MissingHandlerHandling = MissingHandlerHandling.Throw,
            [typeof(MhOrphanQuery)],
            services => services.AddTransient<IRequestHandler<MhOrphanQuery, string>, MhHost<object>.ManualOrphanHandler>());

        await host.StartAsync();
        await host.StopAsync();
    }

    // A request implementing both request interfaces is sendable through either handler, so one is enough.
    [Fact]
    public async Task Startup_AcceptsEitherHandlerForARequestImplementingBothInterfaces()
    {
        using var host = BuildHost(
            new RecordingLoggerProvider(),
            options => options.MissingHandlerHandling = MissingHandlerHandling.Throw,
            typeof(MhDualRequest), typeof(MhDualVoidHandler));

        await host.StartAsync();
        await host.StopAsync();
    }

    // MediatorOptions are validated when read, and an invalid value has always surfaced where the mediator reads it.
    // The check reads them at startup; it must not move that failure there.
    [Fact]
    public async Task Startup_WithInvalidMediatorOptions_LeavesTheFailureWhereItWas()
    {
        using var host = BuildHost(
            new RecordingLoggerProvider(),
            options => options.EmptyResponseStatusCode = (EmptyResponseStatusCode)201,
            typeof(MhOrphanQuery));

        await host.StartAsync();

        Assert.Throws<OptionsValidationException>(() => host.Services.GetRequiredService<IOptions<MediatorOptions>>().Value);
        await host.StopAsync();
    }

    [Fact]
    public void MediatorOptions_RejectAnUndefinedMissingHandlerHandling()
    {
        var services = new ServiceCollection();
        services.AddMediator(static options => options.MissingHandlerHandling = (MissingHandlerHandling)7);
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<MediatorOptions>>().Value);

        Assert.Contains("MissingHandlerHandling", exception.Message);
    }

    private static IHost BuildHost(RecordingLoggerProvider logs, Action<MediatorOptions>? configure, params Type[] scannedTypes)
        => BuildHost(logs, configure, scannedTypes, services: null);

    private static IHost BuildHost(RecordingLoggerProvider logs, Action<MediatorOptions>? configure, Type[] scannedTypes, Action<IServiceCollection>? services)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);

        var assembly = new FakeAssembly("Phoenix.Mediator.Tests.MissingHandlers", scannedTypes);
        if (configure is null)
            builder.Services.AddMediator(assembly);
        else
            builder.Services.AddMediator(configure, assembly);

        services?.Invoke(builder.Services);
        return builder.Build();
    }
}

public sealed class MhHandledQuery : IRequest<string>;

public sealed class MhHandledQueryHandler : IRequestHandler<MhHandledQuery, string>
{
    public Task<string> Handle(MhHandledQuery request, CancellationToken cancellationToken) => Task.FromResult("handled");
}

/// <summary>Deliberately left without a handler.</summary>
public sealed class MhOrphanQuery : IRequest<string>;

/// <summary>Deliberately left without a handler.</summary>
public sealed class MhOrphanCommand : IRequest;

public abstract class MhAbstractQuery : IRequest<string>;

public sealed class MhGenericQuery<T> : IRequest<T>;

public sealed class MhDualRequest : IRequest, IRequest<string>;

public sealed class MhDualVoidHandler : IRequestHandler<MhDualRequest>
{
    public Task Handle(MhDualRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Handlers the assembly scan must not register: the tests register them by hand.</summary>
public static class MhHost<TMarker>
{
    public sealed class ManualOrphanHandler : IRequestHandler<MhOrphanQuery, string>
    {
        public Task<string> Handle(MhOrphanQuery request, CancellationToken cancellationToken) => Task.FromResult("manual");
    }
}
