using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Phoenix.Mediator.Exceptions;
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
        catch (UnauthorizedAccessException)
        {
            await HandleUnauthorizedException(context);
        }
        catch (Exception ex)
        {
            await HandleUnhandledException(context, ex);
        }
    }

    private async Task HandleHttpResponseException(HttpContext context, HttpResponseException exception)
    {
        context.Response.Clear();

        logger.LogWarning(exception,
            "HttpResponseException occurred for {Method} {Path}",
            context.Request.Method,
            context.Request.Path);

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)exception.HttpStatusCode;

        var json = JsonSerializer.Serialize(new
        {
            errors = exception.Errors
        });

        await context.Response.WriteAsync(json);
    }

    private static Task HandleUnauthorizedException(HttpContext context)
    {
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;

        return Task.CompletedTask;
    }

    private async Task HandleUnhandledException(HttpContext context, Exception exception)
    {
        context.Response.Clear();

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

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)statusCode;

        var json = JsonSerializer.Serialize(new
        {
            errors = new[] { GetUnknownErrorMessage(context) }
        });

        await context.Response.WriteAsync(json);
    }

    private string GetUnknownErrorMessage(HttpContext context)
    {
        var messages = GetConfiguredErrorMessages();
        if (messages.Count == 0)
            return UnknownErrorMessage;

        var acceptLanguage = context.Request.Headers.AcceptLanguage.ToString();
        foreach (var candidate in GetLanguageCandidates(acceptLanguage))
        {
            if (messages.TryGetValue(candidate, out var message))
                return message;
        }

        foreach (var candidate in GetDefaultLanguageCandidates())
        {
            if (messages.TryGetValue(candidate, out var message))
                return message;
        }

        return messages.Values.FirstOrDefault(static message => !string.IsNullOrWhiteSpace(message))
            ?? UnknownErrorMessage;
    }

    private Dictionary<string, string> GetConfiguredErrorMessages()
    {
        var section = configuration.GetSection(ErrorMessagesSectionName);
        if (!section.Exists())
            return [];

        return section
            .GetChildren()
            .Where(static child => !IsDefaultLanguageKey(child.Key) && !string.IsNullOrWhiteSpace(child.Value))
            .ToDictionary(static child => child.Key, static child => child.Value!, StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<string> GetDefaultLanguageCandidates()
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
