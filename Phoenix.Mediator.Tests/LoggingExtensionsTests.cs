using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Phoenix.Mediator.Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;
using LoggerConfiguration = Serilog.LoggerConfiguration;

namespace Phoenix.Mediator.Tests;

// AddLogging's UseSerilog call doesn't preserve the static Serilog Log.Logger, so every test that calls
// AddLogging belongs in this class: xUnit runs a class's tests sequentially.
public sealed class LoggingExtensionsTests
{
    private const string AppCategory = "Phoenix.Mediator.Tests.App";
    private const string AspNetCoreCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";
    private const string EfCommandCategory = "Microsoft.EntityFrameworkCore.Database.Command";

    [Fact]
    public async Task AddLogging_CategoryLevelFromConfiguration_SuppressesInformationForThatCategory()
    {
        var sink = new CollectingSink();
        await using var app = CreateApp(sink, new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Information",
            ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Warning"
        });
        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
        var efLogger = loggerFactory.CreateLogger(EfCommandCategory);

        efLogger.LogInformation("Executed DbCommand");
        efLogger.LogWarning("Slow DbCommand");
        loggerFactory.CreateLogger(AppCategory).LogInformation("Handled request");

        Assert.DoesNotContain(sink.Events, e => IsEvent(e, EfCommandCategory, LogEventLevel.Information));
        Assert.Contains(sink.Events, e => IsEvent(e, EfCommandCategory, LogEventLevel.Warning));
        Assert.Contains(sink.Events, e => IsEvent(e, AppCategory, LogEventLevel.Information));
    }

    [Theory]
    [InlineData("Trace", LogLevel.Trace)]
    [InlineData("Debug", LogLevel.Debug)]
    [InlineData("Information", LogLevel.Information)]
    [InlineData("warning", LogLevel.Warning)]
    [InlineData("Error", LogLevel.Error)]
    [InlineData("Critical", LogLevel.Critical)]
    [InlineData("None", LogLevel.Critical)]
    public async Task AddLogging_DefaultLevelFromConfiguration_SetsMinimumLevel(string configuredLevel, LogLevel lowestEnabledLevel)
    {
        await using var app = CreateApp(new CollectingSink(), new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = configuredLevel
        });

        var appLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(AppCategory);

        AssertLowestEnabledLevel(lowestEnabledLevel, appLogger);
    }

    [Theory]
    [InlineData(null, LogLevel.Information)]
    [InlineData("Debug", LogLevel.Debug)]
    public async Task AddLogging_BuiltInLevels_ApplyWhereConfigurationDoesNotSetThem(string? configuredDefault, LogLevel appLowestEnabledLevel)
    {
        // null: no Logging:LogLevel section at all. Otherwise the section exists but doesn't name Microsoft.AspNetCore.
        var configuration = configuredDefault is null
            ? null
            : new Dictionary<string, string?> { ["Logging:LogLevel:Default"] = configuredDefault };
        await using var app = CreateApp(new CollectingSink(), configuration);
        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

        AssertLowestEnabledLevel(appLowestEnabledLevel, loggerFactory.CreateLogger(AppCategory));
        AssertLowestEnabledLevel(LogLevel.Warning, loggerFactory.CreateLogger(AspNetCoreCategory));
    }

    [Theory]
    [InlineData("Warn")]
    [InlineData("-1")]
    public async Task AddLogging_UnrecognizedLevelValue_IsIgnored(string configuredLevel)
    {
        await using var app = CreateApp(new CollectingSink(), new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = configuredLevel
        });

        var efLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(EfCommandCategory);

        AssertLowestEnabledLevel(LogLevel.Information, efLogger);
    }

    [Fact]
    public async Task AddLogging_LevelsSetInConfigureSinks_TakePrecedenceOverConfiguration()
    {
        await using var app = CreateApp(
            new CollectingSink(),
            new Dictionary<string, string?> { ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Warning" },
            lc => lc.MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Debug));

        var efLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(EfCommandCategory);

        AssertLowestEnabledLevel(LogLevel.Debug, efLogger);
    }

    private static void AssertLowestEnabledLevel(LogLevel expected, ILogger logger)
    {
        Assert.True(logger.IsEnabled(expected));
        if (expected > LogLevel.Trace)
            Assert.False(logger.IsEnabled(expected - 1));
    }

    private static bool IsEvent(LogEvent logEvent, string sourceContext, LogEventLevel level)
    {
        return logEvent.Level == level
            && logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var value)
            && value is ScalarValue { Value: string context }
            && context == sourceContext;
    }

    private static WebApplication CreateApp(
        ILogEventSink sink,
        Dictionary<string, string?>? configuration = null,
        Action<LoggerConfiguration>? configureSinks = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(LoggingExtensionsTests).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Development
        });

        // Only the values under test: no Logging__LogLevel__* environment variables from the machine running the tests.
        builder.Configuration.Sources.Clear();
        if (configuration is not null)
            builder.Configuration.AddInMemoryCollection(configuration);

        builder.AddLogging(enableFileLogging: false, configureSinks: lc =>
        {
            lc.WriteTo.Sink(sink);
            configureSinks?.Invoke(lc);
        });

        return builder.Build();
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }
}
