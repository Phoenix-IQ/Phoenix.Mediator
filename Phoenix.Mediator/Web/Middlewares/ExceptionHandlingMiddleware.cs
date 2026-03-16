using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Phoenix.Mediator.Exceptions;
using Phoenix.Mediator.Wrappers;
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
        catch (UnauthorizedAccessException ex)
        {
            await HandleUnauthorizedException(context, ex);
        }
        catch (Exception ex)
        {
            await HandleUnhandledException(context, ex);
        }
    }

    private async Task HandleHttpResponseException(HttpContext context,HttpResponseException exception)
    {
        var response = exception.ErrorResponse;

        logger.LogWarning(exception,
            "HttpResponseException occurred for {Method} {Path}",
            context.Request.Method,
            context.Request.Path);

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)response.HttpStatusCode;

        var json = JsonSerializer.Serialize(response);

        await context.Response.WriteAsync(json);
    }

    private async Task HandleUnauthorizedException(HttpContext context,UnauthorizedAccessException exception)
    {
        var response = new ErrorResponse(HttpStatusCode.Unauthorized,["Unauthorized"]);

        logger.LogWarning(exception,
            "Unauthorized request {Method} {Path}",
            context.Request.Method,
            context.Request.Path);

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;

        var json = JsonSerializer.Serialize(response);

        await context.Response.WriteAsync(json);
    }

    private async Task HandleUnhandledException(HttpContext context,Exception exception)
    {
        var statusCode = exception switch
        {
            KeyNotFoundException => HttpStatusCode.NotFound,
            ArgumentException => HttpStatusCode.BadRequest,
            _ => HttpStatusCode.InternalServerError
        };

        var response = new ErrorResponse(
            statusCode,
            ["Unknown error occurred"]
        );

        logger.LogError(exception,"Unhandled exception for {Method} {Path}",context.Request.Method,context.Request.Path);

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)statusCode;

        var json = JsonSerializer.Serialize(response);

        await context.Response.WriteAsync(json);
    }
}