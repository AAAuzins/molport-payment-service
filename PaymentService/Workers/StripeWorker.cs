using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentService.Clients;
using PaymentService.DB;
using PaymentService.Services;

namespace PaymentService.Workers;

public class StripeWorker : BackgroundService
{
    private readonly PaymentMatchingService _matcher;
    private readonly OracleRepository _repo;
    private readonly ILogger<StripeWorker> _logger;
    private readonly Dictionary<string, StripeApiClient> _clients;
    private readonly TimeSpan _pollInterval;

    public StripeWorker(PaymentMatchingService matcher, OracleRepository repo,
        IOptions<AppSettings> settings, ILogger<StripeWorker> logger,
        ILogger<StripeApiClient> clientLogger)
    {
        _matcher = matcher;
        _repo = repo;
        _logger = logger;
        _pollInterval = TimeSpan.FromMinutes(settings.Value.StripePollIntervalMinutes);
        _clients = StripeApiClient.CreateClients(settings.Value, clientLogger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StripeWorker started ({Count} accounts), polling every {Interval}",
            _clients.Count, _pollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var (accountName, client) in _clients)
            {
                try
                {
                    await RunCycleAsync(accountName, client);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "StripeWorker cycle failed for account {Account}", accountName);
                }
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    private async Task RunCycleAsync(string accountName, StripeApiClient client)
    {
        var source = $"STRIPE_{accountName.ToUpperInvariant()}";
        var since = await _repo.GetLastSyncDateAsync(source) ?? DateTime.UtcNow.AddDays(-7);

        _logger.LogInformation("Stripe [{Account}]: fetching charges since {Since}", accountName, since);

        var transactions = await client.GetSucceededChargesSinceAsync(since);
        var allSucceeded = await _matcher.ProcessTransactionsAsync(transactions);
        if (allSucceeded)
            await _repo.UpsertSyncStateAsync(source, DateTime.UtcNow);
        else
            _logger.LogWarning("Stripe [{Account}]: some transactions failed — sync state not advanced, will retry next cycle", accountName);
    }
}
