using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Phoenix.Mediator.Abstractions;
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
    private static RouteHandlerBuilder AddResponses(this RouteHandlerBuilder handler)
    {
        handler.Produces(statusCode: 401);
        handler.Produces(statusCode: 403);
        handler.Produces<ErrorsResponse>(statusCode: 400);
        handler.Produces<ErrorsResponse>(statusCode: 500);
        return handler;
    }

    // --------------------
    // GET
    // --------------------
    public static IEndpointRouteBuilder Get(this IEndpointRouteBuilder builder, string pattern, Delegate handler)
    {
        builder.MapGet(pattern, handler)
            .AddResponses();
        return builder;
    }

    // --------------------
    // POST
    // --------------------
    public static IEndpointRouteBuilder Post(this IEndpointRouteBuilder builder, string pattern, Delegate handler)
    {
        builder.MapPost(pattern, handler)
            .AddResponses();
        return builder;
    }

    // --------------------
    // PUT
    // --------------------
    public static IEndpointRouteBuilder Put(this IEndpointRouteBuilder builder, string pattern, Delegate handler)
    {
        builder.MapPut(pattern, handler)
            .AddResponses();
        return builder;
    }

    // --------------------
    // DELETE
    // --------------------
    public static IEndpointRouteBuilder Delete(this IEndpointRouteBuilder builder, string pattern, Delegate handler)
    {
        builder.MapDelete(pattern, handler)
            .AddResponses();
        return builder;
    }

    // --------------------
    // PATCH
    // --------------------
    public static IEndpointRouteBuilder Patch(this IEndpointRouteBuilder builder, string pattern, Delegate handler)
    {
        builder.MapPatch(pattern, handler)
            .AddResponses();
        return builder;
    }
    // --------------------
    // POST MULTIPART
    // --------------------
    public static IEndpointRouteBuilder PostMultiPart(this IEndpointRouteBuilder builder, string pattern, Delegate handler, long maxRequestBodySize = 5_000_000, int timeoutSeconds = 1)
    {
        builder.MapPost(pattern, handler)
            .DisableAntiforgery()
            .AddResponses()
            .Accepts<IFormFile>("multipart/form-data")
            .Accepts<IFormFileCollection>("multipart/form-data")
            .WithMetadata(new RequestSizeLimitAttribute(maxRequestBodySize))
            .AddResponses()
            .WithRequestTimeout(TimeSpan.FromSeconds(timeoutSeconds));
        return builder;
    }

    // --------------------
    // PUT MULTIPART
    // --------------------
    public static IEndpointRouteBuilder PutMultiPart(this IEndpointRouteBuilder builder, string pattern, Delegate handler, long maxRequestBodySize = 5_000_000, int timeoutSeconds = 120)
    {
        builder.MapPut(pattern, handler)
            .DisableAntiforgery()
            .AddResponses()
            .Accepts<IFormFile>("multipart/form-data")
            .Accepts<IFormFileCollection>("multipart/form-data")
            .WithMetadata(new RequestSizeLimitAttribute(maxRequestBodySize))
            .AddResponses()
            .WithRequestTimeout(TimeSpan.FromSeconds(timeoutSeconds));
        return builder;
    }

    public static IEndpointRouteBuilder PatchMultiPart(this IEndpointRouteBuilder builder, string pattern, Delegate handler, long maxRequestBodySize = 5_000_000, int timeoutSeconds = 120)
    {
        builder.MapPatch(pattern, handler)
            .DisableAntiforgery()
            .AddResponses()
            .Accepts<IFormFile>("multipart/form-data")
            .Accepts<IFormFileCollection>("multipart/form-data")
            .WithMetadata(new RequestSizeLimitAttribute(maxRequestBodySize))
            .AddResponses()
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
            .AddResponses()
            .Accepts<IFormFile>("multipart/form-data")
            .Accepts<IFormFileCollection>("multipart/form-data")
            .WithMetadata(new RequestSizeLimitAttribute(maxRequestBodySize));

        rhb.WithRequestTimeout(TimeSpan.FromSeconds(timeoutSeconds));
        return rhb.Produces<TResponse>(statusCode: 200);
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
            .AddResponses()
            .Accepts<IFormFile>("multipart/form-data")
            .Accepts<IFormFileCollection>("multipart/form-data")
            .WithMetadata(new RequestSizeLimitAttribute(maxRequestBodySize));

        rhb.WithRequestTimeout(TimeSpan.FromSeconds(timeoutSeconds));
        return rhb.Produces<TResponse>(statusCode: 200);
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
            .AddResponses()
            .Accepts<IFormFile>("multipart/form-data")
            .Accepts<IFormFileCollection>("multipart/form-data")
            .WithMetadata(new RequestSizeLimitAttribute(maxRequestBodySize));

        rhb.WithRequestTimeout(TimeSpan.FromSeconds(timeoutSeconds));
        return rhb.Produces<TResponse>(statusCode: 200);
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
            .AddResponses()
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
            .AddResponses()
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
            .AddResponses()
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
            .AddResponses()
            .Produces<TResponse>(statusCode: 200);
    }

    public static RouteHandlerBuilder PostMediator<TRequest, TResponse>(this IEndpointRouteBuilder builder, string pattern)
        where TRequest : IRequest<TResponse>
    {
        return builder.MapPost(pattern, async (ISender sender, TRequest request, CancellationToken ct) =>
                (await sender.Send(request, ct).ConfigureAwait(false)).ToApiResult())
            .AddResponses()
            .Produces<TResponse>(statusCode: 200);
    }

    public static RouteHandlerBuilder PutMediator<TRequest, TResponse>(this IEndpointRouteBuilder builder, string pattern)
        where TRequest : IRequest<TResponse>
    {
        return builder.MapPut(pattern, async (ISender sender, TRequest request, CancellationToken ct) =>
                (await sender.Send(request, ct).ConfigureAwait(false)).ToApiResult())
            .AddResponses()
            .Produces<TResponse>(statusCode: 200);
    }

    public static RouteHandlerBuilder PatchMediator<TRequest, TResponse>(this IEndpointRouteBuilder builder, string pattern)
        where TRequest : IRequest<TResponse>
    {
        return builder.MapPatch(pattern, async (ISender sender, TRequest request, CancellationToken ct) =>
                (await sender.Send(request, ct).ConfigureAwait(false)).ToApiResult())
            .AddResponses()
            .Produces<TResponse>(statusCode: 200);
    }

    public static RouteHandlerBuilder DeleteMediator<TRequest, TResponse>(this IEndpointRouteBuilder builder, string pattern)
        where TRequest : IRequest<TResponse>
    {
        return builder.MapDelete(pattern, async (ISender sender, TRequest request, CancellationToken ct) =>
                (await sender.Send(request, ct).ConfigureAwait(false)).ToApiResult())
            .AddResponses()
            .Produces<TResponse>(statusCode: 200);
    }

    // No-response request variants (IRequest => 204 on success)
    public static RouteHandlerBuilder PostMediator<TRequest>(this IEndpointRouteBuilder builder, string pattern)
        where TRequest : IRequest
    {
        return builder.MapPost(pattern, async (ISender sender, TRequest request, CancellationToken ct) =>
                (await sender.Send(request, ct).ConfigureAwait(false)).ToApiResult())
            .AddResponses()
            .Produces(statusCode: 204);
    }

    public static RouteHandlerBuilder PutMediator<TRequest>(this IEndpointRouteBuilder builder, string pattern)
        where TRequest : IRequest
    {
        return builder.MapPut(pattern, async (ISender sender, TRequest request, CancellationToken ct) =>
                (await sender.Send(request, ct).ConfigureAwait(false)).ToApiResult())
            .AddResponses()
            .Produces(statusCode: 204);
    }

    public static RouteHandlerBuilder PatchMediator<TRequest>(this IEndpointRouteBuilder builder, string pattern)
        where TRequest : IRequest
    {
        return builder.MapPatch(pattern, async (ISender sender, TRequest request, CancellationToken ct) =>
                (await sender.Send(request, ct).ConfigureAwait(false)).ToApiResult())
            .AddResponses()
            .Produces(statusCode: 204);
    }

    public static RouteHandlerBuilder DeleteMediator<TRequest>(this IEndpointRouteBuilder builder, string pattern)
        where TRequest : IRequest
    {
        return builder.MapDelete(pattern, async (ISender sender, TRequest request, CancellationToken ct) =>
                (await sender.Send(request, ct).ConfigureAwait(false)).ToApiResult())
            .AddResponses()
            .Produces(statusCode: 204);
    }
}
