using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phoenix.Mediator.Exceptions;
using Phoenix.Mediator.Wrappers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Phoenix.Mediator.Web.Middlewares;

/// <summary>
/// Turns exceptions into the documented <c>{"errors": [...], "traceId": "..."}</c> error body. Configure it through
/// <see cref="ExceptionHandlingOptions"/>.
/// <para>
/// This is Phoenix's own middleware rather than ASP.NET Core's <c>UseExceptionHandler()</c> with an
/// <c>IExceptionHandler</c>, deliberately. On .NET 8 the framework middleware logs every exception at Error, stack
/// trace included, before any handler runs, so every 404 and failed validation would land in the error log. And with
/// the usual <c>UseRequestTimeouts()</c> before it, it answers a request timeout with 499 instead of the 504 the
/// timeout middleware writes.
/// </para>
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private const string ErrorMessagesSectionName = "ErrorMessages";
    private const string UnknownErrorMessage = "Unknown error occurred";
    private static readonly string[] DefaultLanguageKeys = ["Default", "DefaultLanguage", "DefaultAcceptLanguage"];
    private static readonly string[] EnglishLanguageAliases = ["en", "En", "English"];
    private static readonly string[] ArabicLanguageAliases = ["ar", "Ar", "Arabic"];

    private readonly RequestDelegate next;
    private readonly ILogger<ExceptionHandlingMiddleware> logger;
    private readonly ExceptionHandlingOptions options;

    // Snapshot configuration once at construction. The middleware instance is created a single
    // time for the app lifetime, so there is no need to re-read/re-allocate this on every error.
    private readonly Dictionary<string, string> errorMessages;
    private readonly IReadOnlyList<string> defaultLanguageCandidates;

    /// <summary>Creates the middleware with the app's <see cref="ExceptionHandlingOptions"/>, read once.</summary>
    /// <remarks>
    /// Declared before the three-argument constructor on purpose. ASP.NET Core 8's <c>UseMiddleware</c> takes the first
    /// declared constructor it can satisfy and ignores <see cref="ActivatorUtilitiesConstructorAttribute"/> (.NET 10
    /// honors it), so with the order swapped every configured mapping was silently dropped on .NET 8.
    /// </remarks>
    [ActivatorUtilitiesConstructor]
    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger,
        IConfiguration configuration,
        IOptions<ExceptionHandlingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        this.next = next;
        this.logger = logger;
        this.options = options.Value;
        errorMessages = BuildErrorMessages(configuration);
        defaultLanguageCandidates = BuildDefaultLanguageCandidates(configuration);
    }

    /// <summary>Creates the middleware with the default <see cref="ExceptionHandlingOptions"/>.</summary>
    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger, IConfiguration configuration)
        : this(next, logger, configuration, Options.Create(new ExceptionHandlingOptions()))
    {
    }

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
            // The client disconnected, or a request timeout fired. Neither is a server fault, and a 500 would fill the
            // error log on every aborted request.
            logger.LogDebug("Request cancelled for {Method} {Path}", context.Request.Method, context.Request.Path);

            // A timeout is rethrown, so UseRequestTimeouts writes its 504. The timeout feature's token fires on the
            // timeout only; RequestAborted, which UseRequestTimeouts links to it, fires on a disconnect too.
            if (context.Features.Get<IHttpRequestTimeoutFeature>()?.RequestTimeoutToken.IsCancellationRequested == true)
                throw;

            // A disconnect ends here: nobody is left to answer. Rethrown, it would reach the middleware further out,
            // where error trackers such as Sentry's report every exception they see as unhandled.
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
        }
        catch (BadHttpRequestException ex)
        {
            await HandleBadHttpRequestException(context, ex);
        }
        catch (Exception ex)
        {
            await HandleOtherException(context, ex);
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
        // A 4xx message describes what was wrong with the request itself, so it is useful to the caller.
        // Anything else stays generic.
        IReadOnlyList<string> errors = exception.StatusCode is >= 400 and < 500
            ? [exception.Message]
            : [GetUnknownErrorMessage(context)];

        LogFailure(context, exception, exception.StatusCode, errors);

        if (!TryResetResponse(context, exception))
            return;

        await WriteErrorsAsync(context, exception.StatusCode, errors);
    }

    private async Task HandleHttpResponseException(HttpContext context, HttpResponseException exception)
    {
        var statusCode = (int)exception.HttpStatusCode;

        LogFailure(context, exception, statusCode, exception.Errors);

        if (!TryResetResponse(context, exception))
            return;

        await WriteErrorsAsync(context, statusCode, exception.Errors);
    }

    /// <summary>
    /// Everything that is not already an error response: the app's <see cref="ExceptionHandlingOptions"/> mappings first,
    /// then a 500.
    /// </summary>
    private async Task HandleOtherException(HttpContext context, Exception exception)
    {
        if (TryMapException(exception, out var mapped, out var useGenericMessage))
        {
            var statusCode = (int)mapped.HttpStatusCode;
            IReadOnlyList<string> errors = useGenericMessage ? [GetUnknownErrorMessage(context)] : mapped.Errors;

            LogFailure(context, exception, statusCode, errors);

            if (!TryResetResponse(context, exception))
                return;

            await WriteErrorsAsync(context, statusCode, errors);
            return;
        }

        await HandleUnhandledException(context, exception);
    }

    private bool TryMapException(Exception exception, [NotNullWhen(true)] out ErrorResponse? response, out bool useGenericMessage)
    {
        try
        {
            return options.TryMap(exception, out response, out useGenericMessage);
        }
        catch (Exception mappingFailure)
        {
            // A mapping that throws must not replace the error the request actually hit; that one still gets reported,
            // as an unhandled exception.
            logger.LogError(mappingFailure,
                "The ExceptionHandlingOptions mapping for {ExceptionType} threw; handling the original exception without it",
                exception.GetType().FullName);

            response = null;
            useGenericMessage = false;
            return false;
        }
    }

    /// <summary>
    /// Logs an exception that became an error response. A client error (4xx) is the caller's doing, so it is logged at
    /// <see cref="ExceptionHandlingOptions.ClientErrorLogLevel"/> without the exception: a stack trace per 404 or failed
    /// validation filled the exception logs and buried the real failures. A server error keeps its stack trace at Error.
    /// </summary>
    private void LogFailure(HttpContext context, Exception exception, int statusCode, IReadOnlyList<string> errors)
    {
        if (statusCode is >= 400 and < 500)
        {
            logger.Log(options.ClientErrorLogLevel,
                "{ExceptionType} for {Method} {Path}; responding {StatusCode}: {Errors}",
                exception.GetType().Name,
                context.Request.Method,
                context.Request.Path,
                statusCode,
                errors);

            return;
        }

        logger.LogError(exception,
            "{ExceptionType} for {Method} {Path}; responding {StatusCode}",
            exception.GetType().Name,
            context.Request.Method,
            context.Request.Path,
            statusCode);
    }

    private async Task HandleUnhandledException(HttpContext context, Exception exception)
    {
        logger.LogError(exception,
            "Unhandled exception for {Method} {Path}",
            context.Request.Method,
            context.Request.Path);

        if (!TryResetResponse(context, exception))
            return;

        await WriteErrorsAsync(context, StatusCodes.Status500InternalServerError, [GetUnknownErrorMessage(context)]);
    }

    /// <summary>
    /// Writes the one error body this package documents and advertises. Every arm goes through here so the
    /// wire shape lives in <see cref="ErrorsResponse"/> alone — it used to be hand-rolled as an anonymous
    /// type once per arm, next to a fourth copy in the endpoint helpers' <c>Produces&lt;ErrorsResponse&gt;</c>
    /// metadata, which is the sort of drift a client only finds in production.
    /// </summary>
    private async Task WriteErrorsAsync(HttpContext context, int statusCode, IReadOnlyList<string> errors)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.StatusCode = statusCode;

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(new ErrorsResponse(errors, GetTraceId(context)), ErrorBodyJsonOptions));
    }

    // The anonymous types this replaced spelled their members in camelCase literally. ErrorsResponse names
    // them in PascalCase, so the camelCase policy is what keeps the body byte-identical for existing clients.
    // The encoder and the charset above are what ASP.NET Core writes its own JSON responses with, a returned
    // ErrorResponse (Results.Json) included, so a thrown error reads the same: Arabic as UTF-8, a quote as \"
    // rather than \u0022. Microsoft documents this encoder, which leaves <, > and & unescaped, for responses
    // that declare charset=utf-8.
    private static readonly JsonSerializerOptions ErrorBodyJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

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
