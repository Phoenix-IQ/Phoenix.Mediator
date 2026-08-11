using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Serilog;
using Serilog.Events;
using Serilog.Exceptions;
using Serilog.Exceptions.Core;
using Serilog.Exceptions.Filters;
using Serilog.Context;
using Serilog.Formatting.Compact;
using System.Diagnostics;

namespace Phoenix.Mediator.Serilog;

public static class LoggingExtensions
{
    /// <summary>
    /// Depth limit for Serilog's own destructuring of <c>{@Property}</c> values. This is Serilog's
    /// default, stated explicitly so its relationship to <see cref="ExceptionDestructuringDepth"/>
    /// is visible: past this depth Serilog nulls the remainder and writes "Maximum destructuring
    /// depth reached." to SelfLog — once per branch that bottoms out, so a wide graph produces a
    /// burst of them.
    /// </summary>
    private const int MaximumDestructuringDepth = 10;

    /// <summary>
    /// Depth limit for the exception property tree built by <c>Serilog.Exceptions</c>, kept below
    /// <see cref="MaximumDestructuringDepth"/> because the enricher attaches its tree as a
    /// root-level property that Serilog then re-walks, adding a level per nested collection.
    /// <para>
    /// This narrows the gap but does not close it on its own — a wide enough graph still trips the
    /// limiter. <see cref="UnwalkableExceptionProperties"/> is what actually bounds the EF Core case.
    /// </para>
    /// </summary>
    private const int ExceptionDestructuringDepth = 8;

    /// <summary>
    /// Property names the reflection-based exception destructurer must not follow.
    /// <para>
    /// These are object-graph entry points rather than diagnostic data. EF Core's
    /// <c>DbUpdateException.Entries</c> is the case that motivated this: every <c>EntityEntry</c>
    /// exposes <c>Metadata</c> (the entire <c>IEntityType</c> model graph) and <c>Context</c>
    /// (which leads back through the change tracker to every tracked entry). The destructurer
    /// copes with the cycles; it is the depth of the model graph that overruns Serilog's limiter,
    /// flooding SelfLog while the recorded detail past the limit is nulled out anyway.
    /// </para>
    /// <para>
    /// Filtering by name keeps this package free of an EF Core dependency. The exception's type,
    /// message and stack trace are untouched — only the reflected property tree is pruned.
    /// </para>
    /// </summary>
    private static readonly string[] UnwalkableExceptionProperties =
    [
        "Entries",      // EF Core DbUpdateException -> EntityEntry[]
        "Context",      // EF Core EntityEntry -> DbContext -> ChangeTracker -> entries
        "ChangeTracker",
        "Metadata",     // EF Core IEntityType: the whole model graph
    ];

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
            .Destructure.ToMaximumDepth(MaximumDestructuringDepth)
            .Enrich.FromLogContext()
            .Enrich.WithExceptionDetails(new DestructuringOptionsBuilder()
                .WithDefaultDestructurers()
                .WithDestructuringDepth(ExceptionDestructuringDepth)
                .WithFilter(new IgnorePropertyByNameExceptionFilter(UnwalkableExceptionProperties)))
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
