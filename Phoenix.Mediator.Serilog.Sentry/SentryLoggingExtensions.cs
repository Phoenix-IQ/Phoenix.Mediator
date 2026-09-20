using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using System.Globalization;
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
    /// <param name="loggerConfiguration">The Serilog configuration to add the sink to.</param>
    /// <param name="configuration">Application configuration holding the <c>Sentry:*</c> values.</param>
    /// <param name="initializeSdk">
    /// Whether this sink initializes the Sentry SDK. The SDK must be initialized exactly once.
    /// Pass <see langword="false"/> when the app also calls <see cref="AddSentry"/>, which initializes it
    /// through the ASP.NET Core integration; otherwise both initialize it and one set of options
    /// (<c>Environment</c> from <see cref="AddSentry"/>, <c>Release</c> here) is discarded.
    /// Leave <see langword="true"/> when the Serilog sink is the only Sentry integration.
    /// </param>
    public static LoggerConfiguration WriteToSentry(this LoggerConfiguration loggerConfiguration, IConfiguration configuration, bool initializeSdk = true)
    {
        ArgumentNullException.ThrowIfNull(loggerConfiguration);
        ArgumentNullException.ThrowIfNull(configuration);

        var (dsn, sendDefaultPii, tracesSampleRate) = ReadSentryConfig(configuration);

        loggerConfiguration.WriteTo.Sentry(o =>
        {
            o.InitializeSdk = initializeSdk;
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
        // Invariant culture: configuration always stores "0.2", but a current-culture parse reads that as
        // 2 where "," is the decimal separator (de-DE/tr-TR -> clamped to 1.0, tracing every request) and
        // fails outright on cultures using another separator (ar-IQ/fr-FR -> the configured value is lost).
        var tracesSampleRate = double.TryParse(configuration["Sentry:TracesSampleRate"], NumberStyles.Float, CultureInfo.InvariantCulture, out var configuredRate)
            ? Math.Clamp(configuredRate, 0.0, 1.0)
            : 0.1;

        return (dsn, sendDefaultPii, tracesSampleRate);
    }
}
