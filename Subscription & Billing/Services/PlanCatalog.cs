namespace Subscription_Backend.Services;

// Prices belong to the server, never to the caller.
public static class PlanCatalog
{
    public static decimal Price(string plan) => plan switch
    {
        "basic" => 990m,
        "pro" => 1990m,
        _ => throw new BillingException(400, "Unknown plan. Use basic or pro.")
    };
}

public sealed class BillingException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
