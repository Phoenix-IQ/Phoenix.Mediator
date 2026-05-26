using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Serilog;
using Serilog.Events;
using Serilog.Exceptions;
using Serilog.Context;
using Serilog.Formatting.Compact;
using System.Diagnostics;

namespace Phoenix.Mediator.Serilog;

public static class LoggingExtensions
{
    /// <summary>
    /// Configures Serilog with a console sink and (optionally) rolling file sinks.
    /// Call early in Program.cs: <c>builder.AddLogging();</c>
    /// <para>
    /// This package has no error-tracking dependency. To send events to Sentry, install
    /// <c>Phoenix.Mediator.Serilog.Sentry</c> and pass its sink via <paramref name="configureSinks"/>
    /// (e.g. <c>builder.AddLogging(configureSinks: lc =&gt; lc.WriteToSentry(builder.Configuration))</c>),
    /// plus call <c>builder.AddSentry()</c> for the ASP.NET integration.
    /// </para>
    /// </summary>
    /// <param name="builder">The web application builder to configure.</param>
    /// <param name="enableFileLogging">
    /// Write rolling log files under <c>{ContentRoot}/logs</c>. Defaults to <see langword="true"/>.
    /// Set to <see langword="false"/> for containerized/horizontally-scaled deployments where the
    /// filesystem is ephemeral and logs should be collected from stdout instead.
    /// </param>
    /// <param name="configureSinks">
    /// Optional hook to add extra Serilog sinks (e.g. the Sentry sink from the companion package).
    /// Invoked after the built-in console/file sinks are configured.
    /// </param>
    public static void AddLogging(this WebApplicationBuilder builder, bool enableFileLogging = true, Action<LoggerConfiguration>? configureSinks = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        global::Serilog.Debugging.SelfLog.Enable(Console.Error);
        builder.Host.UseSerilog((context, loggerConfig) =>
        {
            var env = context.HostingEnvironment;

            string? logsDir = null;
            if (enableFileLogging)
            {
                logsDir = Path.Combine(env.ContentRootPath, "logs");
                Directory.CreateDirectory(logsDir);
            }

            ConfigureBaseSerilog(loggerConfig, logsDir);
            configureSinks?.Invoke(loggerConfig);
        });
    }

    private static void ConfigureBaseSerilog(LoggerConfiguration loggerConfig, string? logsDir)
    {
        loggerConfig
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithExceptionDetails()
            .Enrich.WithMachineName()
            .WriteTo.Console(
                outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj} " +
                "(ClientIP={ClientIP} TraceId={TraceId}){NewLine}{Exception}");

        if (logsDir is null)
            return;

        loggerConfig.WriteTo.File(
            path: Path.Combine(logsDir, "log-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            fileSizeLimitBytes: 50_000_000,
            shared: true);

        loggerConfig.WriteTo.File(
            path: Path.Combine(logsDir, "exceptions-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30,
            fileSizeLimitBytes: 50_000_000,
            shared: true,
            restrictedToMinimumLevel: LogEventLevel.Warning);

        loggerConfig.WriteTo.Logger(lc => lc
            .Filter.ByIncludingOnly(e => e.Exception != null)
            .WriteTo.File(
                formatter: new CompactJsonFormatter(),
                path: Path.Combine(logsDir, "exceptions-json-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                fileSizeLimitBytes: 50_000_000,
                shared: true));
    }

    /// <summary>
    /// Adds per-request log enrichment so Serilog logs include trace id (and, when enabled, client IP).
    /// Call after building the app: <c>app.UsePhoenixRequestLogEnrichment();</c>
    /// <para>
    /// Client IP is personal data (PII). It is only logged when explicitly enabled — pass
    /// <paramref name="logClientIp"/>, or set <c>Sentry:SendDefaultPii=true</c> in configuration.
    /// By default only the (non-PII) trace id is enriched.
    /// </para>
    /// </summary>
    /// <param name="app">The web application to add the enrichment middleware to.</param>
    /// <param name="logClientIp">
    /// When <see langword="null"/> (default), client-IP logging follows <c>Sentry:SendDefaultPii</c>.
    /// Set <see langword="true"/>/<see langword="false"/> to force it on/off regardless of that flag.
    /// </param>
    public static WebApplication UsePhoenixRequestLogEnrichment(this WebApplication app, bool? logClientIp = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        var shouldLogClientIp = logClientIp
            ?? (bool.TryParse(app.Configuration["Sentry:SendDefaultPii"], out var sendDefaultPii) && sendDefaultPii);

        app.Use(async (ctx, next) =>
        {
            var traceId = Activity.Current?.TraceId.ToString() ?? ctx.TraceIdentifier;
            var clientIp = shouldLogClientIp ? GetClientIp(ctx) : null;

            using (LogContext.PushProperty("ClientIP", clientIp ?? string.Empty))
            using (LogContext.PushProperty("TraceId", traceId ?? string.Empty))
            {
                await next().ConfigureAwait(false);
            }
        });

        return app;
    }

    private static string? GetClientIp(HttpContext ctx)
    {
        // Use the connection address. If the host runs behind a trusted proxy,
        // ASP.NET Core's forwarded headers middleware should update this value.
        return ctx.Connection.RemoteIpAddress?.ToString();
    }
}
