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
}
