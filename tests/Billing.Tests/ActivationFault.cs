using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Billing.Tests;

public sealed class ActivationFault : DbCommandInterceptor
{
    public int? SubscriptionId { get; set; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Failures { get; private set; }

    public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        int result, CancellationToken ct = default)
    {
        if (SubscriptionId is { } id && command.CommandText.StartsWith("UPDATE \"Subscriptions\"", StringComparison.Ordinal) &&
            command.Parameters.Cast<DbParameter>().Any(p => p.Value is int value && value == id))
        {
            SubscriptionId = null;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            Failures++;
            throw new InvalidOperationException("Injected failure after activation UPDATE, before inbox commit.");
        }
        return result;
    }
}
