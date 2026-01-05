using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Web.Dtos;
using Phoenix.Mediator.Wrappers;
using System.Reflection;
using System.Text.Json;

namespace Phoenix.Mediator.Web;

public static class EndpointsExtensions
{
    public static WebApplication MapEndpoints(this WebApplication app)
    {
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = async (context, report) =>
            {
                context.Response.ContentType = "application/json";
                var response = new
                {
                    status = report.Status.ToString(),
                    checks = report.Entries.Select(e => new
                    {
                        name = e.Key,
                        status = e.Value.Status.ToString(),
                        description = e.Value.Description
                    }),
                    duration = report.TotalDuration
                };
                await context.Response.WriteAsync(JsonSerializer.Serialize(response));
            }
        });
        var endpointGroupType = typeof(BaseEndpointGroup);
        var assembly = Assembly.GetCallingAssembly();
        var endpointGroupTypes = assembly
            .GetExportedTypes()
            .Where(t => !t.IsAbstract && t.IsSubclassOf(endpointGroupType));

        foreach (var type in endpointGroupTypes)
        {
            if (Activator.CreateInstance(type) is BaseEndpointGroup instance)
                instance.Map(app);
        }
        return app;
    }

    private static RouteHandlerBuilder AddResponses(this RouteHandlerBuilder handler, Delegate endpointHandler, ResponseDto[]? responses)
    {
        handler.Produces(statusCode: 401);
        handler.Produces(statusCode: 403);
        handler.Produces<ErrorsResponse>(statusCode: 400, contentType: "application/json");
        handler.Produces<ErrorsResponse>(statusCode: 500, contentType: "application/json");

        // Success responses:
        // - Prefer explicit responseDtos when provided.
        // - Otherwise infer from the IRequest/IRequest<TResponse> parameter on the delegate.
        var successResponses = (responses is { Length: > 0 })
            ? responses
            : InferSuccessResponses(endpointHandler);

        if (successResponses is { Length: > 0 })
        {
            foreach (var r in successResponses)
            {
                if (r.Type is null)
                    handler.Produces(r.StatusCode);
                else
                    handler.Produces(r.StatusCode, r.Type);
            }
        }
        return handler;
    }

    private static ResponseDto[]? InferSuccessResponses(Delegate endpointHandler)
    {
        // Typical minimal-API pattern:
        // (ISender sender, TRequest request, CancellationToken ct) => await sender.Send(request, ct)
        // We infer OpenAPI success responses based on the request type:
        // - IRequest<TResponse> => 200 with schema = TResponse
        // - IRequest (no response) => 204 only
        var requestType = endpointHandler.Method
            .GetParameters()
            .Select(p => p.ParameterType)
            .FirstOrDefault(IsMediatorRequestType);

        if (requestType is null)
            return null;

        var genericIRequest = requestType
            .GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>));

        if (genericIRequest is not null)
        {
            var responseType = genericIRequest.GetGenericArguments()[0];
            // IMPORTANT: do NOT advertise 204 for response requests; Swagger would show 200+204 even when you always return a body.
            return [new ResponseDto(200, responseType)];
        }

        if (typeof(IRequest).IsAssignableFrom(requestType))
        {
            return [new ResponseDto(204, null)];
        }

        return null;
    }

    private static bool IsMediatorRequestType(Type t)
    {
        if (t is null) return false;
        if (t == typeof(IRequest) || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IRequest<>)))
            return true;

        if (typeof(IRequest).IsAssignableFrom(t))
            return true;

        return t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>));
    }

    // --------------------
    // GET
    // --------------------
    public static IEndpointRouteBuilder Get(this IEndpointRouteBuilder builder, string pattern, Delegate handler, params ResponseDto[]? responseDtos)
    {
        builder.MapGet(pattern, handler)
            .AddResponses(handler, responseDtos);
        return builder;
    }

    // --------------------
    // POST
    // --------------------
    public static IEndpointRouteBuilder Post(this IEndpointRouteBuilder builder, string pattern, Delegate handler, params ResponseDto[]? responseDtos)
    {
        builder.MapPost(pattern, handler)
            .AddResponses(handler, responseDtos);
        return builder;
    }

    // --------------------
    // PUT
    // --------------------
    public static IEndpointRouteBuilder Put(this IEndpointRouteBuilder builder, string pattern, Delegate handler, params ResponseDto[]? responseDtos)
    {
        builder.MapPut(pattern, handler)
            .AddResponses(handler, responseDtos);
        return builder;
    }

    // --------------------
    // DELETE
    // --------------------
    public static IEndpointRouteBuilder Delete(this IEndpointRouteBuilder builder, string pattern, Delegate handler, params ResponseDto[]? responseDtos)
    {
        builder.MapDelete(pattern, handler)
            .AddResponses(handler, responseDtos);
        return builder;
    }

    // --------------------
    // PATCH
    // --------------------
    public static IEndpointRouteBuilder Patch(this IEndpointRouteBuilder builder, string pattern, Delegate handler, params ResponseDto[]? responseDtos)
    {
        builder.MapPatch(pattern, handler)
            .AddResponses(handler, responseDtos);
        return builder;
    }
    // --------------------
    // POST MULTIPART
    // --------------------
    public static IEndpointRouteBuilder PostMultiPart(this IEndpointRouteBuilder builder, string pattern, Delegate handler, long maxRequestBodySize = 5_000_000, int timeoutSeconds = 1, ResponseDto[]? responseDtos = null)
    {
        builder.MapPost(pattern, handler)
            .DisableAntiforgery()
            .AddResponses(handler, responseDtos)
            .Accepts<IFormFile>("multipart/form-data")
            .Accepts<IFormFileCollection>("multipart/form-data")
            .WithMetadata(new RequestSizeLimitAttribute(maxRequestBodySize))
            .WithRequestTimeout(TimeSpan.FromSeconds(timeoutSeconds));
        return builder;
    }

    // --------------------
    // PUT MULTIPART
    // --------------------
    public static IEndpointRouteBuilder PutMultiPart(this IEndpointRouteBuilder builder, string pattern, Delegate handler, long maxRequestBodySize = 5_000_000, int timeoutSeconds = 120, ResponseDto[]? responseDtos = null)
    {
        builder.MapPut(pattern, handler)
            .DisableAntiforgery()
            .AddResponses(handler, responseDtos)
            .Accepts<IFormFile>("multipart/form-data")
            .Accepts<IFormFileCollection>("multipart/form-data")
            .WithMetadata(new RequestSizeLimitAttribute(maxRequestBodySize))
            .WithRequestTimeout(TimeSpan.FromSeconds(timeoutSeconds));
        return builder;
    }

    public static IEndpointRouteBuilder PatchMultiPart(this IEndpointRouteBuilder builder, string pattern, Delegate handler, long maxRequestBodySize = 5_000_000, int timeoutSeconds = 120, ResponseDto[]? responseDtos = null)
    {
        builder.MapPatch(pattern, handler)
            .DisableAntiforgery()
            .AddResponses(handler, responseDtos)
            .Accepts<IFormFile>("multipart/form-data")
            .Accepts<IFormFileCollection>("multipart/form-data")
            .WithMetadata(new RequestSizeLimitAttribute(maxRequestBodySize))
            .WithRequestTimeout(TimeSpan.FromSeconds(timeoutSeconds));
        return builder;
    }

    // --------------------
    // MEDIATOR MULTIPART (AUTO RESULT MAPPING)
    // --------------------
    public static RouteHandlerBuilder PostMultiPartMediator<TRequest, TResponse>(
        this IEndpointRouteBuilder builder,
        string pattern,
        long maxRequestBodySize = 5_000_000,
        int timeoutSeconds = 1)
        where TRequest : IRequest<TResponse>
    {
        var rhb = builder.MapPost(pattern, async (ISender sender, [FromForm] TRequest request, CancellationToken ct) =>
                (await sender.Send(request, ct).ConfigureAwait(false)).ToApiResult())
            .DisableAntiforgery()
            .AddResponses((Delegate)((ISender s, TRequest r, CancellationToken c) => s.Send(r, c)), null)
            .Accepts<IFormFile>("multipart/form-data")
            .Accepts<IFormFileCollection>("multipart/form-data")
            .WithMetadata(new RequestSizeLimitAttribute(maxRequestBodySize));

        rhb.WithRequestTimeout(TimeSpan.FromSeconds(timeoutSeconds));
        // Mediator can return null => 204 via ToApiResult()
        return rhb
            .Produces<TResponse>(statusCode: 200, contentType: "application/json")
            .Produces(statusCode: 204);
    }

    public static RouteHandlerBuilder PutMultiPartMediator<TRequest, TResponse>(
        this IEndpointRouteBuilder builder,
        string pattern,
        long maxRequestBodySize = 5_000_000,
        int timeoutSeconds = 120)
        where TRequest : IRequest<TResponse>
    {
        var rhb = builder.MapPut(pattern, async (ISender sender, [FromForm] TRequest request, CancellationToken ct) =>
                (await sender.Send(request, ct).ConfigureAwait(false)).ToApiResult())
            .DisableAntiforgery()
            .AddResponses((Delegate)((ISender s, TRequest r, CancellationToken c) => s.Send(r, c)), null)
            .Accepts<IFormFile>("multipart/form-data")
            .Accepts<IFormFileCollection>("multipart/form-data")
            .WithMetadata(new RequestSizeLimitAttribute(maxRequestBodySize));

        rhb.WithRequestTimeout(TimeSpan.FromSeconds(timeoutSeconds));
        // Mediator can return null => 204 via ToApiResult()
        return rhb
            .Produces<TResponse>(statusCode: 200, contentType: "application/json")
            .Produces(statusCode: 204);
    }

    public static RouteHandlerBuilder PatchMultiPartMediator<TRequest, TResponse>(
        this IEndpointRouteBuilder builder,
        string pattern,
        long maxRequestBodySize = 5_000_000,
        int timeoutSeconds = 120)
        where TRequest : IRequest<TResponse>
    {
        var rhb = builder.MapPatch(pattern, async (ISender sender, [FromForm] TRequest request, CancellationToken ct) =>
                (await sender.Send(request, ct).ConfigureAwait(false)).ToApiResult())
            .DisableAntiforgery()
            .AddResponses((Delegate)((ISender s, TRequest r, CancellationToken c) => s.Send(r, c)), null)
            .Accepts<IFormFile>("multipart/form-data")
            .Accepts<IFormFileCollection>("multipart/form-data")
            .WithMetadata(new RequestSizeLimitAttribute(maxRequestBodySize));

        rhb.WithRequestTimeout(TimeSpan.FromSeconds(timeoutSeconds));
        // Mediator can return null => 204 via ToApiResult()
        return rhb
            .Produces<TResponse>(statusCode: 200, contentType: "application/json")
            .Produces(statusCode: 204);
    }

    // No-response multipart variants (IRequest => 204 on success)
    public static RouteHandlerBuilder PostMultiPartMediator<TRequest>(
        this IEndpointRouteBuilder builder,
        string pattern,
        long maxRequestBodySize = 5_000_000,
        int timeoutSeconds = 1)
        where TRequest : IRequest
    {
        var rhb = builder.MapPost(pattern, async (ISender sender, [FromForm] TRequest request, CancellationToken ct) =>
                (await sender.Send(request, ct).ConfigureAwait(false)).ToApiResult())
            .DisableAntiforgery()
            .AddResponses((Delegate)((ISender s, TRequest r, CancellationToken c) => s.Send(r, c)), null)
            .Accepts<IFormFile>("multipart/form-data")
            .Accepts<IFormFileCollection>("multipart/form-data")
            .WithMetadata(new RequestSizeLimitAttribute(maxRequestBodySize));

        rhb.WithRequestTimeout(TimeSpan.FromSeconds(timeoutSeconds));
        return rhb.Produces(statusCode: 204);
    }

    public static RouteHandlerBuilder PutMultiPartMediator<TRequest>(
        this IEndpointRouteBuilder builder,
        string pattern,
        long maxRequestBodySize = 5_000_000,
        int timeoutSeconds = 120)
        where TRequest : IRequest
    {
        var rhb = builder.MapPut(pattern, async (ISender sender, [FromForm] TRequest request, CancellationToken ct) =>
                (await sender.Send(request, ct).ConfigureAwait(false)).ToApiResult())
            .DisableAntiforgery()
            .AddResponses((Delegate)((ISender s, TRequest r, CancellationToken c) => s.Send(r, c)), null)
            .Accepts<IFormFile>("multipart/form-data")
            .Accepts<IFormFileCollection>("multipart/form-data")
            .WithMetadata(new RequestSizeLimitAttribute(maxRequestBodySize));

        rhb.WithRequestTimeout(TimeSpan.FromSeconds(timeoutSeconds));
        return rhb.Produces(statusCode: 204);
    }

    public static RouteHandlerBuilder PatchMultiPartMediator<TRequest>(
        this IEndpointRouteBuilder builder,
        string pattern,
        long maxRequestBodySize = 5_000_000,
        int timeoutSeconds = 120)
        where TRequest : IRequest
    {
        var rhb = builder.MapPatch(pattern, async (ISender sender, [FromForm] TRequest request, CancellationToken ct) =>
                (await sender.Send(request, ct).ConfigureAwait(false)).ToApiResult())
            .DisableAntiforgery()
            .AddResponses((Delegate)((ISender s, TRequest r, CancellationToken c) => s.Send(r, c)), null)
            .Accepts<IFormFile>("multipart/form-data")
            .Accepts<IFormFileCollection>("multipart/form-data")
            .WithMetadata(new RequestSizeLimitAttribute(maxRequestBodySize));

        rhb.WithRequestTimeout(TimeSpan.FromSeconds(timeoutSeconds));
        return rhb.Produces(statusCode: 204);
    }

    // --------------------
    // MEDIATOR (AUTO RESULT MAPPING)
    // --------------------
    public static RouteHandlerBuilder GetMediator<TRequest, TResponse>(this IEndpointRouteBuilder builder, string pattern)
        where TRequest : IRequest<TResponse>
    {
        return builder.MapGet(pattern, async (ISender sender, TRequest request, CancellationToken ct) =>
                (await sender.Send(request, ct).ConfigureAwait(false)).ToApiResult())
            .AddResponses((Delegate)((ISender s, TRequest r, CancellationToken c) => s.Send(r, c)), null)
            // Mediator can return null => 204 via ToApiResult()
            .Produces<TResponse>(statusCode: 200, contentType: "application/json")
            .Produces(statusCode: 204);
    }

    public static RouteHandlerBuilder PostMediator<TRequest, TResponse>(this IEndpointRouteBuilder builder, string pattern)
        where TRequest : IRequest<TResponse>
    {
        return builder.MapPost(pattern, async (ISender sender, TRequest request, CancellationToken ct) =>
                (await sender.Send(request, ct).ConfigureAwait(false)).ToApiResult())
            .AddResponses((Delegate)((ISender s, TRequest r, CancellationToken c) => s.Send(r, c)), null)
            // Mediator can return null => 204 via ToApiResult()
            .Produces<TResponse>(statusCode: 200, contentType: "application/json")
            .Produces(statusCode: 204);
    }

    public static RouteHandlerBuilder PutMediator<TRequest, TResponse>(this IEndpointRouteBuilder builder, string pattern)
        where TRequest : IRequest<TResponse>
    {
        return builder.MapPut(pattern, async (ISender sender, TRequest request, CancellationToken ct) =>
                (await sender.Send(request, ct).ConfigureAwait(false)).ToApiResult())
            .AddResponses((Delegate)((ISender s, TRequest r, CancellationToken c) => s.Send(r, c)), null)
            // Mediator can return null => 204 via ToApiResult()
            .Produces<TResponse>(statusCode: 200, contentType: "application/json")
            .Produces(statusCode: 204);
    }

    public static RouteHandlerBuilder PatchMediator<TRequest, TResponse>(this IEndpointRouteBuilder builder, string pattern)
        where TRequest : IRequest<TResponse>
    {
        return builder.MapPatch(pattern, async (ISender sender, TRequest request, CancellationToken ct) =>
                (await sender.Send(request, ct).ConfigureAwait(false)).ToApiResult())
            .AddResponses((Delegate)((ISender s, TRequest r, CancellationToken c) => s.Send(r, c)), null)
            // Mediator can return null => 204 via ToApiResult()
            .Produces<TResponse>(statusCode: 200, contentType: "application/json")
            .Produces(statusCode: 204);
    }

    public static RouteHandlerBuilder DeleteMediator<TRequest, TResponse>(this IEndpointRouteBuilder builder, string pattern)
        where TRequest : IRequest<TResponse>
    {
        return builder.MapDelete(pattern, async (ISender sender, TRequest request, CancellationToken ct) =>
                (await sender.Send(request, ct).ConfigureAwait(false)).ToApiResult())
            .AddResponses((Delegate)((ISender s, TRequest r, CancellationToken c) => s.Send(r, c)), null)
            // Mediator can return null => 204 via ToApiResult()
            .Produces<TResponse>(statusCode: 200, contentType: "application/json")
            .Produces(statusCode: 204);
    }

    // No-response request variants (IRequest => 204 on success)
    public static RouteHandlerBuilder PostMediator<TRequest>(this IEndpointRouteBuilder builder, string pattern)
        where TRequest : IRequest
    {
        return builder.MapPost(pattern, async (ISender sender, TRequest request, CancellationToken ct) =>
                (await sender.Send(request, ct).ConfigureAwait(false)).ToApiResult())
            .AddResponses((Delegate)((ISender s, TRequest r, CancellationToken c) => s.Send(r, c)), null)
            .Produces(statusCode: 204);
    }

    public static RouteHandlerBuilder PutMediator<TRequest>(this IEndpointRouteBuilder builder, string pattern)
        where TRequest : IRequest
    {
        return builder.MapPut(pattern, async (ISender sender, TRequest request, CancellationToken ct) =>
                (await sender.Send(request, ct).ConfigureAwait(false)).ToApiResult())
            .AddResponses((Delegate)((ISender s, TRequest r, CancellationToken c) => s.Send(r, c)), null)
            .Produces(statusCode: 204);
    }

    public static RouteHandlerBuilder PatchMediator<TRequest>(this IEndpointRouteBuilder builder, string pattern)
        where TRequest : IRequest
    {
        return builder.MapPatch(pattern, async (ISender sender, TRequest request, CancellationToken ct) =>
                (await sender.Send(request, ct).ConfigureAwait(false)).ToApiResult())
            .AddResponses((Delegate)((ISender s, TRequest r, CancellationToken c) => s.Send(r, c)), null)
            .Produces(statusCode: 204);
    }

    public static RouteHandlerBuilder DeleteMediator<TRequest>(this IEndpointRouteBuilder builder, string pattern)
        where TRequest : IRequest
    {
        return builder.MapDelete(pattern, async (ISender sender, TRequest request, CancellationToken ct) =>
                (await sender.Send(request, ct).ConfigureAwait(false)).ToApiResult())
            .AddResponses((Delegate)((ISender s, TRequest r, CancellationToken c) => s.Send(r, c)), null)
            .Produces(statusCode: 204);
    }
}
