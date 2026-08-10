using Colossus.Application.Catalog;
using Microsoft.AspNetCore.Mvc;
using System.Runtime.ExceptionServices;

namespace Colossus.Server.Controllers;

internal static class CatalogControllerErrors
{
    public static IActionResult ToResponse(this ControllerBase controller, Exception error)
    {
        if (error is CatalogNotFoundException) return controller.NotFound(new { error = error.Message });
        if (error is CatalogConcurrencyException or CatalogConflictException)
            return controller.Conflict(new { error = error.Message });
        if (error is CatalogRelationshipException or ArgumentException)
            return controller.BadRequest(new { error = error.Message });

        ExceptionDispatchInfo.Capture(error).Throw();
        throw new System.Diagnostics.UnreachableException();
    }
}
