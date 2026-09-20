using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Phoenix.Mediator.Exceptions;
using Phoenix.Mediator.Wrappers;
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
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client disconnected, or a request timeout fired. Neither is a server fault: reporting it
            // as a 500 error hides the 504 that UseRequestTimeouts writes when it sees the exception, and
            // fills the error log on every aborted request. Let it flow to whoever is waiting for it.
            logger.LogDebug("Request cancelled for {Method} {Path}", context.Request.Method, context.Request.Path);
            throw;
        }
        catch (BadHttpRequestException ex)
        {
            await HandleBadHttpRequestException(context, ex);
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

    /// <summary>
    /// Bad requests reported by the framework: malformed JSON, a missing required parameter, an invalid
    /// antiforgery token, or a form/body over the limit. ASP.NET Core throws these when
    /// <c>RouteHandlerOptions.ThrowOnBadRequest</c> is enabled, which is the default in Development.
    /// The status code it chose (400, 413, 415, ...) is kept instead of reporting the caller's mistake as a 500.
    /// </summary>
    private async Task HandleBadHttpRequestException(HttpContext context, BadHttpRequestException exception)
    {
        logger.LogWarning(exception,
            "Bad request for {Method} {Path}",
            context.Request.Method,
            context.Request.Path);

        if (!TryResetResponse(context, exception))
            return;

        // A 4xx message describes what was wrong with the request itself, so it is useful to the caller.
        // Anything else stays generic.
        var message = exception.StatusCode is >= 400 and < 500
            ? exception.Message
            : GetUnknownErrorMessage(context);

        await WriteErrorsAsync(context, exception.StatusCode, [message]);
    }

    private async Task HandleHttpResponseException(HttpContext context, HttpResponseException exception)
    {
        logger.LogWarning(exception,
            "HttpResponseException occurred for {Method} {Path}",
            context.Request.Method,
            context.Request.Path);

        if (!TryResetResponse(context, exception))
            return;

        await WriteErrorsAsync(context, (int)exception.HttpStatusCode, exception.Errors);
    }

    private Task HandleUnauthorizedException(HttpContext context, Exception exception)
    {
        // Logged because .NET throws UnauthorizedAccessException for file-system permission errors too
        // (saving an upload, for example). Without this, a broken directory permission looks like an
        // authentication failure to the client and leaves nothing behind on the server.
        logger.LogWarning(exception,
            "UnauthorizedAccessException for {Method} {Path}; responding 401",
            context.Request.Method,
            context.Request.Path);

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

        await WriteErrorsAsync(context, (int)statusCode, [GetUnknownErrorMessage(context)]);
    }

    /// <summary>
    /// Writes the one error body this package documents and advertises. Every arm goes through here so the
    /// wire shape lives in <see cref="ErrorsResponse"/> alone — it used to be hand-rolled as an anonymous
    /// type once per arm, next to a fourth copy in the endpoint helpers' <c>Produces&lt;ErrorsResponse&gt;</c>
    /// metadata, which is the sort of drift a client only finds in production.
    /// </summary>
    private async Task WriteErrorsAsync(HttpContext context, int statusCode, IReadOnlyList<string> errors)
    {
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = statusCode;

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(new ErrorsResponse(errors, GetTraceId(context)), ErrorBodyJsonOptions));
    }

    // The anonymous types this replaced spelled their members in camelCase literally. ErrorsResponse names
    // them in PascalCase, so the camelCase policy is what keeps the body byte-identical for existing clients.
    private static readonly JsonSerializerOptions ErrorBodyJsonOptions = new(JsonSerializerDefaults.Web);

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

        // BuildErrorMessages drops blank values and the empty case returned above, so there is always a
        // message left to fall back on here.
        return errorMessages.Values.First();
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
