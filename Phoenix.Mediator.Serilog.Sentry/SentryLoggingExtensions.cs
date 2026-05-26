using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using System.Reflection;

namespace Phoenix.Mediator.Serilog.Sentry;

/// <summary>
/// Sentry add-on for the Serilog bootstrapping helpers. Pairs with
/// <c>Phoenix.Mediator.Serilog</c>'s <c>AddLogging(...)</c>.
/// </summary>
public static class SentryLoggingExtensions
{
    /// <summary>
    /// Wires the Sentry ASP.NET Core integration (request error capture + tracing + <c>IHub</c>
    /// registration). Reads <c>Sentry:Dsn</c>, <c>Sentry:SendDefaultPii</c>, and
    /// <c>Sentry:TracesSampleRate</c> from configuration. Call early in Program.cs.
    /// </summary>
    public static WebApplicationBuilder AddSentry(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var (dsn, sendDefaultPii, tracesSampleRate) = ReadSentryConfig(builder.Configuration);

        builder.WebHost.UseSentry(o =>
        {
            o.Dsn = dsn;
            o.TracesSampleRate = tracesSampleRate;
            o.Environment = builder.Environment.EnvironmentName;
            o.Debug = false;
            o.AttachStacktrace = true;
            o.SendDefaultPii = sendDefaultPii;
        });

        return builder;
    }

    /// <summary>
    /// Adds the Serilog → Sentry sink (errors as events, lower levels as breadcrumbs). Pass this to
    /// <c>AddLogging(configureSinks: lc =&gt; lc.WriteToSentry(builder.Configuration))</c>.
    /// </summary>
    public static LoggerConfiguration WriteToSentry(this LoggerConfiguration loggerConfiguration, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(loggerConfiguration);
        ArgumentNullException.ThrowIfNull(configuration);

        var (dsn, sendDefaultPii, tracesSampleRate) = ReadSentryConfig(configuration);

        loggerConfiguration.WriteTo.Sentry(o =>
        {
            o.Dsn = dsn;
            o.MinimumBreadcrumbLevel = LogEventLevel.Information;
            o.MinimumEventLevel = LogEventLevel.Error;
            o.SendDefaultPii = sendDefaultPii;
            o.AttachStacktrace = true;
            o.Release = Assembly.GetEntryAssembly()?.GetName().Version?.ToString();
            o.MaxBreadcrumbs = 100;
            o.TracesSampleRate = tracesSampleRate;
        });

        return loggerConfiguration;
    }

    private static (string? Dsn, bool SendDefaultPii, double TracesSampleRate) ReadSentryConfig(IConfiguration configuration)
    {
        var dsn = configuration["Sentry:Dsn"];
        var sendDefaultPii = bool.TryParse(configuration["Sentry:SendDefaultPii"], out var configuredPii) && configuredPii;
        var tracesSampleRate = double.TryParse(configuration["Sentry:TracesSampleRate"], out var configuredRate)
            ? Math.Clamp(configuredRate, 0.0, 1.0)
            : 0.1;

        return (dsn, sendDefaultPii, tracesSampleRate);
    }
}
