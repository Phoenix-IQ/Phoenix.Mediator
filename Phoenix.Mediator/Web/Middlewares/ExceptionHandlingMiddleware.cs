using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Phoenix.Mediator.Exceptions;
using System.Net;
using System.Text.Json;

namespace Phoenix.Mediator.Web.Middlewares;

public sealed class ExceptionHandlingMiddleware(RequestDelegate next,ILogger<ExceptionHandlingMiddleware> logger)
{
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

    private async Task HandleHttpResponseException(HttpContext context,HttpResponseException exception)
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

    private async Task HandleUnhandledException(HttpContext context,Exception exception)
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
            errors = new[] { "Unknown error occurred" }
        });

        await context.Response.WriteAsync(json);
    }
}