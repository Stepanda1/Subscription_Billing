using MassTransit;
using Microsoft.EntityFrameworkCore;
using Subscription_Backend.Messages;
using Subscription_Backend.Models;

namespace Subscription_Backend.Consumers;

public class SubscriptionActivationConsumer : IConsumer<SubscriptionPaidEvent>
{
    private readonly AppDbContext _db;
    private readonly ILogger<SubscriptionActivationConsumer> _logger;

    public SubscriptionActivationConsumer(AppDbContext db, ILogger<SubscriptionActivationConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<SubscriptionPaidEvent> context)
    {
        var message = context.Message;
        _logger.LogInformation($"[Worker] Получено событие оплаты для подписки {message.SubscriptionId}");

        // Убрали Random Exception. 
        // Если база упадет сама по себе — Retry сработает автоматически.

        var subscription = await _db.Subscriptions.FindAsync(message.SubscriptionId);

        if (subscription == null)
        {
            _logger.LogError($"Подписка {message.SubscriptionId} не найдена!");
            return;
        }

        subscription.IsActive = true;
        await _db.SaveChangesAsync();

        _logger.LogInformation($"[Worker] Подписка {message.SubscriptionId} успешно активирована!");
    }
}