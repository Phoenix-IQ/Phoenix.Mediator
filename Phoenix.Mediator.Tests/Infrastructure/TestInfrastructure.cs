using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Text.Json;

namespace Phoenix.Mediator.Tests.Infrastructure;

/// <summary>
/// Shared helpers for the test suite. Every test file uses these instead of growing its own copy of
/// "build a WebApplication", "capture the logs" or "read the status code off an IResult".
/// </summary>
internal static class TestApps
{
    /// <summary>
    /// A host builder rooted at the test output directory, so tests behave the same on every machine.
    /// Defaults to Production: Development additionally turns on <c>ValidateOnBuild</c>, which
    /// constructs every registered service and so fails on registrations a test deliberately left broken.
    /// </summary>
    public static WebApplicationBuilder CreateBuilder(
        string? environment = null,
        ILoggerProvider? loggerProvider = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(TestApps).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = environment ?? Environments.Production
        });

        if (loggerProvider is not null)
            builder.Logging.AddProvider(loggerProvider);

        return builder;
    }

    /// <summary>
    /// Every <see cref="RouteEndpoint"/> the app has mapped so far, including the ones endpoint groups added.
    /// </summary>
    public static IReadOnlyList<RouteEndpoint> RouteEndpoints(this WebApplication app)
    {
        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
    }

    /// <summary>The single endpoint whose route pattern ends with <paramref name="routeSuffix"/>.</summary>
    public static RouteEndpoint Endpoint(this WebApplication app, string routeSuffix)
    {
        return app.RouteEndpoints().Single(endpoint => endpoint.RoutePattern.RawText is not null
            && endpoint.RoutePattern.RawText.EndsWith(routeSuffix, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>One log record, captured so a test can assert on level, message and exception.</summary>
internal sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

/// <summary>
/// Collects every log record written through the app's logger factory. Safe to read from the test
/// thread while the host writes from another.
/// </summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider, ILoggerFactory
{
    private readonly List<LogEntry> entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (entries)
                return entries.ToArray();
        }
    }

    public IReadOnlyList<string> Messages => Entries.Select(static entry => entry.Message).ToArray();

    public IReadOnlyList<LogEntry> Warnings => Entries.Where(static entry => entry.Level >= LogLevel.Warning).ToArray();

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);

    /// <summary>A typed logger over this recorder, for a component that takes <c>ILogger&lt;T&gt;</c> directly.</summary>
    public ILogger<T> CreateLogger<T>() => new Logger<T>(this);

    void ILoggerFactory.AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private void Add(LogEntry entry)
    {
        lock (entries)
            entries.Add(entry);
    }

    private sealed class RecordingLogger(RecordingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => owner.Add(new LogEntry(category, logLevel, formatter(state, exception), exception));
    }
}

/// <summary>
/// An assembly whose type list is exactly what the test passes in, so a scan sees those types and
/// nothing else. Lets a test exercise scanning without adding types to the real test assembly, where
/// every other test's <c>AddMediator(assembly)</c> would pick them up too.
/// </summary>
internal sealed class FakeAssembly(string name, params Type[] types) : Assembly
{
    public FakeAssembly(params Type[] types) : this("Phoenix.Mediator.Tests.Fake", types)
    {
    }

    public override Type[] GetTypes() => types;

    public override Type[] GetExportedTypes() => types.Where(static type => type.IsVisible).ToArray();

    public override AssemblyName GetName() => new(name);

    public override string FullName => name;
}

/// <summary>
/// An assembly that fails to load some of its types, the way one referencing a missing dependency does.
/// </summary>
internal sealed class PartiallyLoadableFakeAssembly(params Type[] loadableTypes) : Assembly
{
    public override Type[] GetTypes()
        => throw new ReflectionTypeLoadException([.. loadableTypes, null], [new TypeLoadException("Simulated missing dependency.")]);

    public override AssemblyName GetName() => new("Phoenix.Mediator.Tests.PartiallyLoadable");
}

/// <summary>Runs an <see cref="IResult"/> the way the framework would, and captures what it wrote.</summary>
internal static class ResultExecution
{
    public static int StatusCode(IResult result)
    {
        var statusCodeResult = result as IStatusCodeHttpResult
            ?? throw new InvalidOperationException($"{result.GetType().Name} does not carry a status code.");

        return statusCodeResult.StatusCode
            ?? throw new InvalidOperationException($"{result.GetType().Name} left the status code unset.");
    }

    /// <summary>Executes the result against a fresh context and returns the status, content type and body.</summary>
    public static async Task<ExecutedResult> ExecuteAsync(IResult result, IServiceProvider? services = null)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = services ?? EmptyServices.Instance
        };
        var body = new MemoryStream();
        context.Response.Body = body;

        await result.ExecuteAsync(context);

        body.Position = 0;
        using var reader = new StreamReader(body);

        return new ExecutedResult(
            context.Response.StatusCode,
            context.Response.ContentType,
            await reader.ReadToEndAsync());
    }

    private static class EmptyServices
    {
        public static readonly IServiceProvider Instance = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .BuildServiceProvider();
    }
}

/// <summary>What an executed <see cref="IResult"/> wrote to the response.</summary>
internal sealed record ExecutedResult(int StatusCode, string? ContentType, string Body)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement.Clone();
}

/// <summary>
/// The feature <c>UseRequestTimeouts</c> sets on a request it times. Its token fires on the timeout alone, which is how
/// the exception middleware tells a timeout from a client disconnect.
/// </summary>
internal sealed class TestRequestTimeoutFeature(CancellationToken requestTimeoutToken) : IHttpRequestTimeoutFeature
{
    public CancellationToken RequestTimeoutToken { get; } = requestTimeoutToken;

    public void DisableTimeout()
    {
    }
}
