using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Phoenix.Mediator.Exceptions;
using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace Phoenix.Mediator.Web.Middlewares;

public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger, IConfiguration configuration)
{
    private const string ErrorMessagesSectionName = "ErrorMessages";
    private const string UnknownErrorMessage = "Unknown error occurred";
    private static readonly string[] DefaultLanguageKeys = ["Default", "DefaultLanguage", "DefaultAcceptLanguage"];
    private static readonly string[] EnglishLanguageAliases = ["en", "En", "English"];
    private static readonly string[] ArabicLanguageAliases = ["ar", "Ar", "Arabic"];

    // Snapshot configuration once at construction. The middleware instance is created a single
    // time for the app lifetime, so there is no need to re-read/re-allocate this on every error.
    private readonly Dictionary<string, string> errorMessages = BuildErrorMessages(configuration);
    private readonly IReadOnlyList<string> defaultLanguageCandidates = BuildDefaultLanguageCandidates(configuration);

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (HttpResponseException ex)
        {
            await HandleHttpResponseException(context, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            await HandleUnauthorizedException(context, ex);
        }
        catch (Exception ex)
        {
            await HandleUnhandledException(context, ex);
        }
    }

    private async Task HandleHttpResponseException(HttpContext context, HttpResponseException exception)
    {
        logger.LogWarning(exception,
            "HttpResponseException occurred for {Method} {Path}",
            context.Request.Method,
            context.Request.Path);

        if (!TryResetResponse(context, exception))
            return;

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)exception.HttpStatusCode;

        var json = JsonSerializer.Serialize(new
        {
            errors = exception.Errors,
            traceId = GetTraceId(context)
        });

        await context.Response.WriteAsync(json);
    }

    private Task HandleUnauthorizedException(HttpContext context, Exception exception)
    {
        if (!TryResetResponse(context, exception))
            return Task.CompletedTask;

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;

        return Task.CompletedTask;
    }

    private async Task HandleUnhandledException(HttpContext context, Exception exception)
    {
        var statusCode = exception switch
        {
            KeyNotFoundException => HttpStatusCode.NotFound,
            ArgumentException => HttpStatusCode.BadRequest,
            _ => HttpStatusCode.InternalServerError
        };

        logger.LogError(exception,
            "Unhandled exception for {Method} {Path}",
            context.Request.Method,
            context.Request.Path);

        if (!TryResetResponse(context, exception))
            return;

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)statusCode;

        var json = JsonSerializer.Serialize(new
        {
            errors = new[] { GetUnknownErrorMessage(context) },
            traceId = GetTraceId(context)
        });

        await context.Response.WriteAsync(json);
    }

    private static string GetTraceId(HttpContext context)
        => Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

    /// <summary>
    /// Resets the response so an error body can be written. If the response has already
    /// started sending, the headers/body cannot be cleared — we log and bail out instead
    /// of letting <c>HttpResponse.Clear()</c> throw a secondary exception that would
    /// tear the connection and mask the original error.
    /// </summary>
    private bool TryResetResponse(HttpContext context, Exception exception)
    {
        if (context.Response.HasStarted)
        {
            logger.LogError(exception,
                "Response already started; cannot write error body for {Method} {Path}",
                context.Request.Method,
                context.Request.Path);

            return false;
        }

        context.Response.Clear();
        return true;
    }

    private string GetUnknownErrorMessage(HttpContext context)
    {
        if (errorMessages.Count == 0)
            return UnknownErrorMessage;

        var acceptLanguage = context.Request.Headers.AcceptLanguage.ToString();
        foreach (var candidate in GetLanguageCandidates(acceptLanguage))
        {
            if (errorMessages.TryGetValue(candidate, out var message))
                return message;
        }

        foreach (var candidate in defaultLanguageCandidates)
        {
            if (errorMessages.TryGetValue(candidate, out var message))
                return message;
        }

        return errorMessages.Values.FirstOrDefault(static message => !string.IsNullOrWhiteSpace(message))
            ?? UnknownErrorMessage;
    }

    private static Dictionary<string, string> BuildErrorMessages(IConfiguration configuration)
    {
        var section = configuration.GetSection(ErrorMessagesSectionName);
        if (!section.Exists())
            return [];

        return section
            .GetChildren()
            .Where(static child => !IsDefaultLanguageKey(child.Key) && !string.IsNullOrWhiteSpace(child.Value))
            .ToDictionary(static child => child.Key, static child => child.Value!, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> BuildDefaultLanguageCandidates(IConfiguration configuration)
    {
        foreach (var key in DefaultLanguageKeys)
        {
            var configuredDefault = configuration[$"{ErrorMessagesSectionName}:{key}"];
            if (!string.IsNullOrWhiteSpace(configuredDefault))
                return GetLanguageCandidates(configuredDefault);
        }

        return EnglishLanguageAliases;
    }

    private static bool IsDefaultLanguageKey(string key)
    {
        return DefaultLanguageKeys.Contains(key, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> GetLanguageCandidates(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var candidates = new List<string>();
        var knownCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var valuePart in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var language = valuePart.Split(';', 2, StringSplitOptions.TrimEntries)[0];
            if (string.IsNullOrWhiteSpace(language))
                continue;

            AddCandidate(language);

            var normalizedLanguage = language.Replace('_', '-');
            AddCandidate(normalizedLanguage);

            var primaryLanguage = normalizedLanguage.Split('-', 2, StringSplitOptions.TrimEntries)[0];
            AddCandidate(primaryLanguage);

            AddKnownAliases(primaryLanguage);
            AddKnownAliases(normalizedLanguage);
        }

        return candidates;

        void AddKnownAliases(string language)
        {
            if (language.StartsWith("ar", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("Arabic", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var alias in ArabicLanguageAliases)
                    AddCandidate(alias);
            }

            if (language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("English", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var alias in EnglishLanguageAliases)
                    AddCandidate(alias);
            }
        }

        void AddCandidate(string candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && knownCandidates.Add(candidate))
                candidates.Add(candidate);
        }
    }
}
