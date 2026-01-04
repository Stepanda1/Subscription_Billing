# Subscription & Billing Backend

> Снимок проекта на момент **недели 3** (API + транзакции + готовность к асинхронности).

---

## Properties/launchSettings.json
```json
{
  "$schema": "http://json.schemastore.org/launchsettings.json",
  "iisSettings": {
    "windowsAuthentication": false,
    "anonymousAuthentication": true,
    "iisExpress": {
      "applicationUrl": "http://localhost:4721",
      "sslPort": 44338
    }
  },
  "profiles": {
    "http": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "launchBrowser": true,
      "launchUrl": "swagger",
      "applicationUrl": "http://localhost:5265",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      }
    },
    "https": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "launchBrowser": true,
      "launchUrl": "swagger",
      "applicationUrl": "https://localhost:7289;http://localhost:5265",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      }
    },
    "IIS Express": {
      "commandName": "IISExpress",
      "launchBrowser": true,
      "launchUrl": "swagger",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      }
    }
  }
}
```

---

## Controllers/SubscriptionBillingController.cs
```csharp
using Microsoft.AspNetCore.Mvc;
using Subscription_Backend.Dtos;
using Subscription_Backend.Models;
using Subscription_Backend.Services;

namespace Subscription_Backend.Controllers;

[ApiController]
[Route("api/subscriptions")]
public class SubscriptionWorkflowController : ControllerBase
{
    private readonly SubscriptionService _service;

    public SubscriptionWorkflowController(SubscriptionService service)
    {
        _service = service;
    }

    [HttpPost("create")]
    public async Task<ActionResult<Subscription>> Create(CreateSubscriptionRequest request)
    {
        var subscription = await _service.CreateSubscriptionAsync(request);
        return Ok(subscription);
    }
}
```

---

## Controllers/WeatherForecastController.cs
```csharp
using Microsoft.AspNetCore.Mvc;

namespace Subscription___Billing.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class WeatherForecastController : ControllerBase
    {
        private static readonly string[] Summaries = new[]
        {
            "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
        };

        private readonly ILogger<WeatherForecastController> _logger;

        public WeatherForecastController(ILogger<WeatherForecastController> logger)
        {
            _logger = logger;
        }

        [HttpGet(Name = "GetWeatherForecast")]
        public IEnumerable<WeatherForecast> Get()
        {
            return Enumerable.Range(1, 5).Select(index => new WeatherForecast
            {
                Date = DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                TemperatureC = Random.Shared.Next(-20, 55),
                Summary = Summaries[Random.Shared.Next(Summaries.Length)]
            })
            .ToArray();
        }
    }
}
```

---

## Dtos/CreateSubscriptionRequest.cs
```csharp
namespace Subscription_Backend.Dtos;

public class CreateSubscriptionRequest
{
    public int UserId { get; set; }
    public string Plan { get; set; } = null!;
    public decimal Amount { get; set; }
}
```

---

## Models/Payment.cs
```csharp
namespace Subscription_Backend.Models;

public class Payment
{
    public int Id { get; set; }

    public int SubscriptionId { get; set; }
    public Subscription Subscription { get; set; } = null!;

    public decimal Amount { get; set; }
    public DateTime PaidAt { get; set; } = DateTime.UtcNow;
}
```

---

## Models/Subscription.cs
```csharp
namespace Subscription_Backend.Models
{
    public class Subscription
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string Plan { get; set; } = null!;
        public bool IsActive { get; set; }
    }
}
```

---

## Models/User.cs
```csharp
namespace Subscription_Backend.Models
{
    public class User
    {
        public int Id { get; set; }
        public string Name { get; set; } = null!;
        public string Email { get; set; } = null!;
    }
}
```

---

## Services/SubscriptionService.cs
```csharp
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Subscription_Backend.Dtos;
using Subscription_Backend.Messages;
using Subscription_Backend.Models;

namespace Subscription_Backend.Services;

public class SubscriptionService
{
    private readonly AppDbContext _db;
    private readonly IPublishEndpoint _publishEndpoint;

    public SubscriptionService(AppDbContext db, IPublishEndpoint publishEndpoint)
    {
        _db = db;
        _publishEndpoint = publishEndpoint;
    }

    public async Task<Subscription> CreateSubscriptionAsync(CreateSubscriptionRequest request)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();

        try
        {
            var user = await _db.Users.FindAsync(request.UserId);
            if (user == null)
                throw new Exception("User not found");

            var subscription = new Subscription
            {
                UserId = user.Id,
                Plan = request.Plan,
                IsActive = false
            };

            _db.Subscriptions.Add(subscription);
            await _db.SaveChangesAsync();

            var payment = new Payment
            {
                SubscriptionId = subscription.Id,
                Amount = request.Amount
            };

            _db.Payments.Add(payment);
            await _db.SaveChangesAsync();

            await transaction.CommitAsync();

            await _publishEndpoint.Publish(new SubscriptionPaidEvent
            {
                SubscriptionId = subscription.Id,
                Amount = request.Amount,
                PaidAt = DateTime.UtcNow
            });

            return subscription;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}
```
csharp
using Microsoft.EntityFrameworkCore;
using Subscription_Backend.Dtos;
using Subscription_Backend.Models;

namespace Subscription_Backend.Services;

public class SubscriptionService
{
    private readonly AppDbContext _db;

    public SubscriptionService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<Subscription> CreateSubscriptionAsync(CreateSubscriptionRequest request)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();

        try
        {
            var user = await _db.Users.FindAsync(request.UserId);
            if (user == null)
                throw new Exception("User not found");

            var subscription = new Subscription
            {
                UserId = user.Id,
                Plan = request.Plan,
                IsActive = false
            };

            _db.Subscriptions.Add(subscription);
            await _db.SaveChangesAsync();

            var payment = new Payment
            {
                SubscriptionId = subscription.Id,
                Amount = request.Amount
            };

            _db.Payments.Add(payment);
            await _db.SaveChangesAsync();

            subscription.IsActive = true;
            await _db.SaveChangesAsync();

            await transaction.CommitAsync();

            return subscription;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}
```

---

## AppDbContext.cs
```csharp
using Microsoft.EntityFrameworkCore;
using Subscription_Backend.Models;
using System.Collections.Generic;

namespace Subscription_Backend
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<User> Users => Set<User>();
        public DbSet<Subscription> Subscriptions => Set<Subscription>();
        public DbSet<Payment> Payments => Set<Payment>();
    }
}
```

---

## appsettings.json
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=subscription_db;Username=postgres;Password=postgres"
  }
}
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=subscription_db;Username=postgres;Password=postgres"
  }
}
```

---

## Consumers/SubscriptionActivationConsumer.cs
```csharp
using MassTransit;
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

        // Имитация долгой обработки
        await Task.Delay(5000);

        var subscription = await _db.Subscriptions.FindAsync(message.SubscriptionId);

        if (subscription == null)
        {
            _logger.LogError($"Подписка {message.SubscriptionId} не найдена!");
            return;
        }

        // Активируем подписку
        subscription.IsActive = true;
        await _db.SaveChangesAsync();

        _logger.LogInformation($"[Worker] Подписка {message.SubscriptionId} успешно активирована!");
    }
}
```

---

## Messages/SubscriptionPaidEvent.cs
```csharp
namespace Subscription_Backend.Messages;

public record SubscriptionPaidEvent
{
    public int SubscriptionId { get; init; }
    public decimal Amount { get; init; }
    public DateTime PaidAt { get; init; }
}
```

---

## Program.cs
```csharp
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Subscription_Backend;
using Subscription_Backend.Consumers;
using Subscription_Backend.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<SubscriptionService>();

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<SubscriptionActivationConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host("localhost", "/", h =>
        {
            h.Username("guest");
            h.Password("guest");
        });

        cfg.ConfigureEndpoints(context);
    });
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.MapControllers();

app.Run();
```
csharp
using Microsoft.EntityFrameworkCore;
using Subscription_Backend;
using Subscription_Backend.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// DbContext
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<SubscriptionService>();

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Swagger UI
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.MapControllers();

app.Run();
```

---

## Subscription & Billing.http
```http
@Subscription___Billing_HostAddress = http://localhost:5265

GET {{Subscription___Billing_HostAddress}}/weatherforecast/
Accept: application/json
```

---

## WeatherForecast.cs
```csharp
namespace Subscription___Billing
{
    public class WeatherForecast
    {
        public DateOnly Date { get; set; }

        public int TemperatureC { get; set; }

        public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);

        public string? Summary { get; set; }
    }
}
```

---

## NuGet packages

- MassTransit
- MassTransit.RabbitMQ
- Microsoft.EntityFrameworkCore
- Npgsql.EntityFrameworkCore.PostgreSQL
- Swashbuckle.AspNetCore

---

## Infrastructure requirements

- PostgreSQL (localhost)
- RabbitMQ (localhost, guest / guest)

---

### Статус
- POST `/api/subscriptions/create` работает через Swagger
- Подписка создаётся **неактивной**
- После оплаты публикуется `SubscriptionPaidEvent`
- `SubscriptionActivationConsumer` асинхронно активирует подписку
- Архитектура соответствует **неделе 3 (async + message broker)**

