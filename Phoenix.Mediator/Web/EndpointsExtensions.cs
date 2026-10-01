using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Web.Middlewares;
using Phoenix.Mediator.Wrappers;
using System.Reflection;
using System.Text.Json;

namespace Phoenix.Mediator.Web;

public static class EndpointsExtensions
{
    private const long DefaultMaxMultipartBodySize = 5_000_000;
    private const int DefaultMultipartTimeoutSeconds = 120;

    /// <summary>
    /// Discovers and maps endpoint groups, and (by default) also adds the Phoenix exception-handling
    /// middleware and the <c>/health</c> endpoint. Use the <see cref="MapEndpointsOptions"/> overload
    /// to opt out of either, or compose the pieces yourself via
    /// <see cref="UsePhoenixExceptionHandling"/> / <see cref="MapPhoenixHealthChecks"/> for full
    /// control over middleware ordering.
    /// </summary>
    public static WebApplication MapEndpoints(this WebApplication app, params Assembly[] assemblies)
        => MapEndpoints(app, new MapEndpointsOptions(), assemblies);

    /// <inheritdoc cref="MapEndpoints(WebApplication, Assembly[])"/>
    public static WebApplication MapEndpoints(this WebApplication app, MapEndpointsOptions options, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(assemblies);

        if (options.UseExceptionHandling)
            app.UsePhoenixExceptionHandling();

        if (options.MapHealthChecks)
            app.MapPhoenixHealthChecks(options.HealthCheckPattern);

        MapEndpointGroups(app, options, assemblies);
        app.ValidateNoDuplicateEndpoints(options.DuplicateEndpointHandling);
        return app;
    }

    /// <summary>
    /// Reports routes that are mapped more than once with the same HTTP method. Routing accepts such
    /// a mapping and throws <c>AmbiguousMatchException</c> (a 500) only when a request first matches
    /// both endpoints, so this turns a duplicate introduced by mistake into a startup failure.
    /// <see cref="MapEndpoints(WebApplication, MapEndpointsOptions, Assembly[])"/> calls this for you;
    /// call it again yourself after mapping any endpoints that come later.
    /// </summary>
    /// <param name="app">The application whose mapped endpoints are inspected.</param>
    /// <param name="handling">Throw (default), log a warning, or skip the check.</param>
    /// <exception cref="DuplicateEndpointException">
    /// A route is mapped more than once and <paramref name="handling"/> is
    /// <see cref="DuplicateEndpointHandling.Throw"/>.
    /// </exception>
    public static WebApplication ValidateNoDuplicateEndpoints(this WebApplication app, DuplicateEndpointHandling handling = DuplicateEndpointHandling.Throw)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (handling == DuplicateEndpointHandling.None)
            return app;

        var duplicates = DuplicateEndpointDetector.Find(
            ((IEndpointRouteBuilder)app).DataSources,
            app.Services.GetServices<MatcherPolicy>());
        if (duplicates.Count == 0)
            return app;

        var report = DuplicateEndpointDetector.BuildMessage(duplicates);

        if (handling == DuplicateEndpointHandling.Throw)
            throw new DuplicateEndpointException(report, duplicates.Select(duplicate => duplicate.Describe()));

        // Route patterns contain braces, so the report goes in as an argument rather than as the
        // message template, which would otherwise be parsed as (malformed) placeholders.
        app.Logger.LogWarning("{DuplicateEndpointReport}", report);
        return app;
    }

    /// <summary>
    /// Registers the Phoenix exception-handling middleware. Call this where you want it in the
    /// pipeline (typically early) when composing manually instead of relying on <see cref="MapEndpoints(WebApplication, Assembly[])"/>.
    /// </summary>
    public static WebApplication UsePhoenixExceptionHandling(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseMiddleware<ExceptionHandlingMiddleware>();
        return app;
    }

    /// <summary>
    /// Maps a public, unauthenticated health endpoint that returns only the overall status.
    /// Per-check names/descriptions are intentionally NOT exposed (they can leak infrastructure
    /// detail); map a separate authenticated endpoint if you need detailed diagnostics.
    /// </summary>
    public static WebApplication MapPhoenixHealthChecks(this WebApplication app, string pattern = "/health")
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapHealthChecks(pattern, new HealthCheckOptions
        {
            ResponseWriter = async (context, report) =>
            {
                context.Response.ContentType = "application/json";
                var response = new
                {
                    status = report.Status.ToString()
                };
                await context.Response.WriteAsync(JsonSerializer.Serialize(response));
            }
        });

        return app;
    }

    private static void MapEndpointGroups(WebApplication app, MapEndpointsOptions options, Assembly[] assemblies)
    {
        var endpointGroupType = typeof(BaseEndpointGroup);
        var endpointGroupTypes = GetEndpointAssemblies(app, assemblies)
            .SelectMany(assembly => GetLoadableTypes(assembly, app.Logger))
            // Open generic groups (CrudEndpoints<TEntity>) can't be instantiated; skip them instead of
            // failing the whole application at startup.
            .Where(t => t.IsClass && !t.IsAbstract && !t.ContainsGenericParameters && endpointGroupType.IsAssignableFrom(t))
            .Distinct();

        // Every group is mapped under this one route group, so the prefix and the shared conventions are set once and
        // reach everything below it.
        var root = CreateRootGroup(app, options);

        foreach (var type in endpointGroupTypes)
        {
            // Dispose asynchronously: a synchronous scope Dispose throws when a resolved dependency only
            // implements IAsyncDisposable. Blocking is fine here because this runs once at startup.
            var scope = app.Services.CreateAsyncScope();
            try
            {
                var instance = (BaseEndpointGroup)ActivatorUtilities.CreateInstance(scope.ServiceProvider, type);

                var group = root.MapGroup(string.Empty);
                if (options.TagEndpointsWithGroupName)
                    TagUnlessTagged(group, instance.GroupName);

                instance.Map(group);
            }
            finally
            {
                scope.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    private static RouteGroupBuilder CreateRootGroup(WebApplication app, MapEndpointsOptions options)
    {
        var root = app.MapGroup(options.NormalizedRoutePrefix);
        options.ConfigureEndpoints?.Invoke(root);
        return root;
    }

    /// <summary>
    /// Tags every endpoint in <paramref name="group"/> with <paramref name="tag"/>, unless it already has tags of its own.
    /// Tags add up rather than replace each other, so tagging unconditionally would list an endpoint that sets its own
    /// under both. A finally convention runs after every other one, the endpoint's own included, so it sees them all.
    /// </summary>
    private static void TagUnlessTagged(RouteGroupBuilder group, string tag)
    {
        ((IEndpointConventionBuilder)group).Finally(endpoint =>
        {
            if (!endpoint.Metadata.OfType<ITagsMetadata>().Any())
                endpoint.Metadata.Add(new TagsAttribute(tag));
        });
    }

    private static IEnumerable<Assembly> GetEndpointAssemblies(WebApplication app, IReadOnlyCollection<Assembly> additionalAssemblies)
    {
        var knownAssemblies = new HashSet<Assembly>();
        var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();

        if (!string.IsNullOrWhiteSpace(app.Environment.ApplicationName))
        {
            var appAssembly = loadedAssemblies.FirstOrDefault(assembly =>
                string.Equals(assembly.GetName().Name, app.Environment.ApplicationName, StringComparison.Ordinal));

            if (IsEndpointAssembly(appAssembly))
                knownAssemblies.Add(appAssembly!);
        }

        var entryAssembly = Assembly.GetEntryAssembly();
        if (IsEndpointAssembly(entryAssembly))
            knownAssemblies.Add(entryAssembly!);

        var registry = app.Services.GetService<MediatorAssemblyRegistry>();
        if (registry is not null)
        {
            foreach (var assembly in registry.GetAssemblies())
            {
                if (IsEndpointAssembly(assembly))
                    knownAssemblies.Add(assembly);
            }
        }

        foreach (var assembly in additionalAssemblies)
        {
            if (IsEndpointAssembly(assembly))
                knownAssemblies.Add(assembly);
        }

        return knownAssemblies;
    }

    private static bool IsEndpointAssembly(Assembly? assembly)
    {
        return assembly is not null && !assembly.IsDynamic;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly, ILogger logger)
    {
        return AssemblyTypeLoader.GetLoadableTypes(assembly, ex => logger.LogWarning(ex,
            "Could not load all types from assembly {Assembly} during endpoint discovery; {LoadedCount} of {TotalCount} types were usable. Some endpoint groups may be missing.",
            assembly.FullName,
            ex.Types.Count(t => t is not null),
            ex.Types.Length));
    }

    private static RouteHandlerBuilder AddResponses(this RouteHandlerBuilder handler, IServiceProvider services, Delegate endpointHandler)
    {
        var emptyResponseStatusCode = GetConfiguredEmptyResponseStatusCode(services);
        handler.MapResultToApiResult(endpointHandler, emptyResponseStatusCode);

        handler.Produces(statusCode: 401);
        handler.Produces(statusCode: 403);
        handler.Produces<ErrorsResponse>(statusCode: 400, contentType: "application/json");
        handler.Produces<ErrorsResponse>(statusCode: 500, contentType: "application/json");

        // The success response, inferred from the IRequest/IRequest<TResponse> parameter on the delegate.
        if (InferSuccessResponse(endpointHandler, emptyResponseStatusCode) is not { } success)
            return handler;

        if (success.Type is null)
            handler.Produces(success.StatusCode);
        else
            handler.Produces(success.StatusCode, success.Type);

        // Minimal APIs infer a 200 response of their own from the delegate's return type (Task<SingleResponse<T>>,
        // Task<object?>). It precedes the one declared above, which a convention adds, so when ours is a 200 it is the
        // last one. The inferred one goes when ours is not a 200, when it has no schema, or when it repeats our type.
        // It stays only for a different type: the delegate's value is what the mapping writes.
        var declaresOk = success.StatusCode == StatusCodes.Status200OK;
        handler.Add(endpointBuilder =>
        {
            var responses200 = endpointBuilder.Metadata
                .OfType<IProducesResponseTypeMetadata>()
                .Where(m => m.StatusCode == StatusCodes.Status200OK)
                .ToArray();
            var declared = declaresOk ? responses200[^1] : null;

            foreach (var inferred in declaresOk ? responses200[..^1] : responses200)
            {
                if (declared is null || HasNoSchema(inferred.Type) || declared.Type == inferred.Type)
                    endpointBuilder.Metadata.Remove(inferred);
            }
        });
        return handler;
    }

    /// <summary>
    /// Lets an endpoint return what the mediator returns — <c>(ISender sender, ...) =&gt; sender.Send(request, ct)</c> —
    /// and answer with the responses its OpenAPI metadata describes: no result (a void request, or a handler returning
    /// <see langword="null"/>) is the configured empty status, an <see cref="ErrorResponse"/> is its status with the error
    /// body, and anything else is 200 with a JSON body, as <see cref="AutoResponseMappingExtensions.ToApiResult(object?, EmptyResponseStatusCode)"/>
    /// maps them. A delegate that returns an <see cref="IResult"/> itself is left alone.
    /// </summary>
    private static void MapResultToApiResult(this RouteHandlerBuilder handler, Delegate endpointHandler, EmptyResponseStatusCode emptyResponseStatusCode)
    {
        if (ReturnsHttpResult(endpointHandler.Method.ReturnType))
            return;

        handler.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context).ConfigureAwait(false);

            // A delegate returning void or Task hands its filters EmptyHttpResult: no result, like a handler returning null.
            return (result is EmptyHttpResult ? null : result).ToApiResult(emptyResponseStatusCode);
        });
    }

    private static bool HasNoSchema(Type? type)
    {
        return type is null || type == typeof(void) || typeof(IResult).IsAssignableFrom(type);
    }

    private static bool ReturnsHttpResult(Type returnType)
    {
        if (returnType.IsGenericType
            && (returnType.GetGenericTypeDefinition() == typeof(Task<>) || returnType.GetGenericTypeDefinition() == typeof(ValueTask<>)))
            returnType = returnType.GetGenericArguments()[0];

        return typeof(IResult).IsAssignableFrom(returnType);
    }

    private static (int StatusCode, Type? Type)? InferSuccessResponse(Delegate endpointHandler, EmptyResponseStatusCode emptyResponseStatusCode)
    {
        // Typical minimal-API pattern:
        // (ISender sender, TRequest request, CancellationToken ct) => await sender.Send(request, ct)
        // We infer the OpenAPI success response from the request type:
        // - IRequest<TResponse> => 200 with schema = TResponse
        // - IRequest (no response) => configured empty response status code
        var requestType = endpointHandler.Method
            .GetParameters()
            .Select(p => p.ParameterType)
            .FirstOrDefault(MediatorRequestTypes.IsRequestOrRequestInterface);

        if (requestType is null)
            return null;

        // Only two outcomes are possible here, because IsRequestOrRequestInterface above accepted this type
        // for exactly one of two reasons: it has a response type, or it is an IRequest with none. The two
        // questions have to be answered by the same helper — when they were asked separately, a parameter
        // declared as the IRequest<TResponse> interface passed the first check and failed the second, and the
        // endpoint silently advertised no success response at all.
        var responseType = MediatorRequestTypes.GetDeclaredResponseType(requestType);

        // IMPORTANT: do NOT advertise 204 for response requests; Swagger would show 200+204 even when you always return a body.
        return responseType is not null
            ? (StatusCodes.Status200OK, responseType)
            : ((int)emptyResponseStatusCode, null);
    }

    private static EmptyResponseStatusCode GetConfiguredEmptyResponseStatusCode(IServiceProvider services)
    {
        return services.GetService<IOptions<MediatorOptions>>()?.Value.EmptyResponseStatusCode
            ?? EmptyResponseStatusCode.NoContent;
    }

    // --------------------
    // GET
    // --------------------
    public static RouteHandlerBuilder Get(this IEndpointRouteBuilder builder, string pattern, Delegate handler)
    {
        return builder.MapGet(pattern, handler)
            .AddResponses(builder.ServiceProvider, handler);
    }

    // --------------------
    // POST
    // --------------------
    public static RouteHandlerBuilder Post(this IEndpointRouteBuilder builder, string pattern, Delegate handler)
    {
        return builder.MapPost(pattern, handler)
            .AddResponses(builder.ServiceProvider, handler);
    }

    // --------------------
    // PUT
    // --------------------
    public static RouteHandlerBuilder Put(this IEndpointRouteBuilder builder, string pattern, Delegate handler)
    {
        return builder.MapPut(pattern, handler)
            .AddResponses(builder.ServiceProvider, handler);
    }

    // --------------------
    // DELETE
    // --------------------
    public static RouteHandlerBuilder Delete(this IEndpointRouteBuilder builder, string pattern, Delegate handler)
    {
        return builder.MapDelete(pattern, handler)
            .AddResponses(builder.ServiceProvider, handler);
    }

    // --------------------
    // PATCH
    // --------------------
    public static RouteHandlerBuilder Patch(this IEndpointRouteBuilder builder, string pattern, Delegate handler)
    {
        return builder.MapPatch(pattern, handler)
            .AddResponses(builder.ServiceProvider, handler);
    }
    // --------------------
    // POST MULTIPART
    // --------------------
    /// <summary>
    /// Maps an endpoint that accepts <c>multipart/form-data</c>, with a body size limit, a request timeout,
    /// and the default OpenAPI responses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Antiforgery.</b> ASP.NET Core requires an antiforgery token for any endpoint whose delegate binds
    /// form data (<c>IFormFile</c>, <c>IFormFileCollection</c>, <c>IFormCollection</c>, <c>[FromForm]</c>).
    /// That needs <c>services.AddAntiforgery()</c> <b>and</b> <c>app.UseAntiforgery()</c>, plus clients sending
    /// the token (<c>RequestVerificationToken</c> header or <c>__RequestVerificationToken</c> form field) with
    /// the antiforgery cookie. Registering the services without the middleware is not enough. APIs authenticated
    /// with bearer tokens rather than cookies should pass <paramref name="disableAntiforgery"/> as
    /// <see langword="true"/>. A delegate that instead reads <c>HttpRequest.Form</c> itself is never validated,
    /// whatever this flag says, because validation is enforced by form parameter binding.
    /// </para>
    /// <para>
    /// <b>Timeout.</b> <paramref name="timeoutSeconds"/> only adds metadata. It is enforced only when the app
    /// calls <c>services.AddRequestTimeouts()</c> and <c>app.UseRequestTimeouts()</c>, and it covers the whole
    /// request, so a large upload over a slow connection can hit it.
    /// </para>
    /// </remarks>
    /// <param name="builder">The route builder to map onto.</param>
    /// <param name="pattern">The route pattern.</param>
    /// <param name="handler">The endpoint delegate.</param>
    /// <param name="maxRequestBodySize">Maximum request body size in bytes. Applied to both the server limit and the multipart form limit.</param>
    /// <param name="timeoutSeconds">Request timeout in seconds; see the remarks about what enforces it.</param>
    /// <param name="disableAntiforgery">Pass <see langword="true"/> to opt out of antiforgery validation; see the remarks.</param>
    public static RouteHandlerBuilder PostMultiPart(this IEndpointRouteBuilder builder, string pattern, Delegate handler, long maxRequestBodySize = DefaultMaxMultipartBodySize, int timeoutSeconds = DefaultMultipartTimeoutSeconds, bool disableAntiforgery = false)
    {
        return builder.MapPost(pattern, handler)
            .ConfigureMultiPart(builder.ServiceProvider, pattern, handler, maxRequestBodySize, timeoutSeconds, disableAntiforgery);
    }

    // --------------------
    // PUT MULTIPART
    // --------------------
    /// <inheritdoc cref="PostMultiPart"/>
    public static RouteHandlerBuilder PutMultiPart(this IEndpointRouteBuilder builder, string pattern, Delegate handler, long maxRequestBodySize = DefaultMaxMultipartBodySize, int timeoutSeconds = DefaultMultipartTimeoutSeconds, bool disableAntiforgery = false)
    {
        return builder.MapPut(pattern, handler)
            .ConfigureMultiPart(builder.ServiceProvider, pattern, handler, maxRequestBodySize, timeoutSeconds, disableAntiforgery);
    }

    /// <inheritdoc cref="PostMultiPart"/>
    public static RouteHandlerBuilder PatchMultiPart(this IEndpointRouteBuilder builder, string pattern, Delegate handler, long maxRequestBodySize = DefaultMaxMultipartBodySize, int timeoutSeconds = DefaultMultipartTimeoutSeconds, bool disableAntiforgery = false)
    {
        return builder.MapPatch(pattern, handler)
            .ConfigureMultiPart(builder.ServiceProvider, pattern, handler, maxRequestBodySize, timeoutSeconds, disableAntiforgery);
    }

    private static RouteHandlerBuilder ConfigureMultiPart(this RouteHandlerBuilder route, IServiceProvider services, string pattern, Delegate handler, long maxRequestBodySize, int timeoutSeconds, bool disableAntiforgery)
    {
        route
            .AddResponses(services, handler)
            .Accepts<IFormFileCollection>("multipart/form-data")
            .WithMetadata(new RequestSizeLimitAttribute(maxRequestBodySize))
            // The request size limit alone is not enough: form parsing has its own ceiling
            // (FormOptions.MultipartBodyLengthLimit, 128 MB by default), so without this a
            // maxRequestBodySize above that is rejected while parsing the form.
            .WithFormOptions(multipartBodyLengthLimit: maxRequestBodySize)
            // Only metadata: nothing enforces it unless the app calls AddRequestTimeouts() and
            // app.UseRequestTimeouts().
            .WithRequestTimeout(TimeSpan.FromSeconds(timeoutSeconds));

        // Antiforgery validation stays ON by default. Only disable it when the caller
        // explicitly opts in (e.g. a token-authenticated API not relying on cookies),
        // so cookie-authenticated uploads are not silently exposed to CSRF.
        if (disableAntiforgery)
            route.DisableAntiforgery();
        else
            WarnIfAntiforgeryIsUnavailable(services, pattern, handler);

        return route;
    }

    /// <summary>
    /// ASP.NET Core requires an antiforgery token for endpoints that bind form data. Without the antiforgery
    /// services, such a request fails at runtime with a 500 whose cause is only visible in the log, so say so
    /// while the routes are being built.
    /// </summary>
    private static void WarnIfAntiforgeryIsUnavailable(IServiceProvider services, string pattern, Delegate handler)
    {
        if (!BindsFormData(handler) || IsAntiforgeryRegistered(services))
            return;

        services.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(EndpointsExtensions).FullName!)
            .LogWarning(
                "Endpoint '{Pattern}' binds form data, so ASP.NET Core requires antiforgery validation, but no antiforgery services are registered. " +
                "Requests to it will fail with 500. Either call services.AddAntiforgery() and app.UseAntiforgery() and have clients send the token, " +
                "or pass disableAntiforgery: true (typical for APIs authenticated with bearer tokens rather than cookies).",
                pattern);
    }

    private static bool BindsFormData(Delegate handler)
    {
        return handler.Method.GetParameters().Any(static parameter =>
            typeof(IFormFile).IsAssignableFrom(parameter.ParameterType)
            || typeof(IFormFileCollection).IsAssignableFrom(parameter.ParameterType)
            || typeof(IFormCollection).IsAssignableFrom(parameter.ParameterType)
            || parameter.GetCustomAttributes(inherit: true).Any(static attribute => attribute is IFromFormMetadata));
    }

    private static bool IsAntiforgeryRegistered(IServiceProvider services)
    {
        // IServiceProviderIsService avoids constructing the service just to test for it.
        return services.GetService<IServiceProviderIsService>() is { } isService
            ? isService.IsService(typeof(IAntiforgery))
            : services.GetService<IAntiforgery>() is not null;
    }
}
