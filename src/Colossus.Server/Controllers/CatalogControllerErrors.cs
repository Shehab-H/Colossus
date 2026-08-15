using Colossus.Application.Catalog;
using Colossus.Application.Abstractions.Sources;
using Microsoft.AspNetCore.Mvc;
using System.Runtime.ExceptionServices;

namespace Colossus.Server.Controllers;

internal static class CatalogControllerErrors
{
    public static IActionResult ToResponse(this ControllerBase controller, Exception error)
    {
        if (error is SourceOperationException source)
        {
            int status = source.Kind switch
            {
                SourceErrorKind.Connectivity => StatusCodes.Status502BadGateway,
                SourceErrorKind.TransientFailure => StatusCodes.Status503ServiceUnavailable,
                SourceErrorKind.Cancellation => StatusCodes.Status408RequestTimeout,
                _ => StatusCodes.Status400BadRequest,
            };
            var problem = new ValidationProblemDetails(source.FieldErrors.ToDictionary())
            {
                Status = status,
                Title = source.Message,
            };
            problem.Extensions["sourceErrorKind"] = source.Kind.ToString();
            problem.Extensions["transient"] = source.Transient;
            return controller.StatusCode(status, problem);
        }
        if (error is CatalogNotFoundException) return controller.NotFound(new { error = error.Message });
        if (error is CatalogConcurrencyException or CatalogConflictException)
            return controller.Conflict(new { error = error.Message });
        if (error is CatalogValidationException validation)
            return controller.BadRequest(new ValidationProblemDetails(validation.Errors.ToDictionary())
            {
                Status = StatusCodes.Status400BadRequest,
                Title = validation.Message,
            });
        if (error is CatalogRelationshipException relationship)
            return controller.BadRequest(new ValidationProblemDetails(
                new Dictionary<string, string[]> { ["definition"] = [relationship.Message] })
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "The catalog relationship is invalid.",
            });
        if (error is ArgumentException argument)
            return controller.BadRequest(new ValidationProblemDetails(
                new Dictionary<string, string[]> { [argument.ParamName ?? "definition"] = [argument.Message] })
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "The catalog definition is invalid.",
            });

        ExceptionDispatchInfo.Capture(error).Throw();
        throw new System.Diagnostics.UnreachableException();
    }
}
