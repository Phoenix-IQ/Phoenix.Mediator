using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Serilog;
using Serilog.Events;
using Serilog.Exceptions;
using Serilog.Context;
using Serilog.Formatting.Compact;
using System.Reflection;
using System.Diagnostics;

namespace Phoenix.Mediator.Extensions;

public static class LoggingExtensions
{
    /// <summary>
    /// Configures Serilog + optional Sentry integration.
    /// Call early in Program.cs: <c>builder.AddLogging(hasSentry: true);</c>
    /// </summary>
    public static void AddLogging(this WebApplicationBuilder builder, bool hasSentry = true)
    {
        string? sentryDsn = null;
        var sendDefaultPii = false;
        var tracesSampleRate = 0.1;

        if (hasSentry)
        {
            sentryDsn = builder.Configuration["Sentry:Dsn"];
            sendDefaultPii = bool.TryParse(builder.Configuration["Sentry:SendDefaultPii"], out var configuredSendDefaultPii)
                && configuredSendDefaultPii;
            tracesSampleRate = double.TryParse(builder.Configuration["Sentry:TracesSampleRate"], out var configuredRate)
                ? Math.Clamp(configuredRate, 0.0, 1.0)
                : 0.1;
            builder.WebHost.UseSentry(o =>
            {
                o.Dsn = sentryDsn;
                o.TracesSampleRate = tracesSampleRate;
                o.Environment = builder.Environment.EnvironmentName;
                o.Debug = false;
                o.AttachStacktrace = true;
                o.SendDefaultPii = sendDefaultPii;
            });
        }

        Serilog.Debugging.SelfLog.Enable(Console.Error);
        builder.Host.UseSerilog((context, loggerConfig) =>
        {
            var env = context.HostingEnvironment;
            var logsDir = Path.Combine(env.ContentRootPath, "logs");
            Directory.CreateDirectory(logsDir);

            ConfigureBaseSerilog(loggerConfig, logsDir);

            if (hasSentry)
            {
                var warningsFilePath = Path.Combine(logsDir, "exceptions-.log");
                var exceptionsJsonPath = Path.Combine(logsDir, "exceptions-json-.log");

                loggerConfig
                    .WriteTo.File(
                        path: warningsFilePath,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 30,
                        fileSizeLimitBytes: 50_000_000,
                        shared: true,
                        restrictedToMinimumLevel: LogEventLevel.Warning)
                    .WriteTo.Logger(lc => lc
                        .Filter.ByIncludingOnly(e => e.Exception != null)
                        .WriteTo.File(
                            formatter: new CompactJsonFormatter(),
                            path: exceptionsJsonPath,
                            rollingInterval: RollingInterval.Day,
                            retainedFileCountLimit: 30,
                            fileSizeLimitBytes: 50_000_000,
                            shared: true))
                    .WriteTo.Sentry(o =>
                    {
                        o.Dsn = sentryDsn;
                        o.MinimumBreadcrumbLevel = LogEventLevel.Information;
                        o.MinimumEventLevel = LogEventLevel.Error;
                        o.SendDefaultPii = sendDefaultPii;
                        o.AttachStacktrace = true;
                        o.Environment = env.EnvironmentName;
                        o.Release = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
                        o.MaxBreadcrumbs = 100;
                        o.TracesSampleRate = tracesSampleRate;
                    });
            }
        });
    }

    private static void ConfigureBaseSerilog(LoggerConfiguration loggerConfig, string logsDir)
    {
        var logFilePath = Path.Combine(logsDir, "log-.log");

        loggerConfig
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithExceptionDetails()
            .Enrich.WithMachineName()
            .WriteTo.Console(
                outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj} " +
                "(ClientIP={ClientIP} TraceId={TraceId}){NewLine}{Exception}")
            .WriteTo.File(
                path: logFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 50_000_000,
                shared: true);
    }

    /// <summary>
    /// Adds per-request log enrichment so Serilog logs include client IP and trace id out of the box.
    /// Call after building the app: <c>app.UsePhoenixRequestLogEnrichment();</c>
    /// </summary>
    public static WebApplication UsePhoenixRequestLogEnrichment(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.Use(async (ctx, next) =>
        {
            var clientIp = GetClientIp(ctx);
            var traceId = Activity.Current?.TraceId.ToString() ?? ctx.TraceIdentifier;

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
