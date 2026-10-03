using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Subscription_Backend.Services;

namespace Subscription_Backend.Infrastructure;

public sealed class BillingExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not BillingException billing) return false;
        context.Response.StatusCode = billing.StatusCode;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails { Status = billing.StatusCode, Title = "Billing request rejected", Detail = billing.Message }
        });
    }
}
