using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentService.Clients;
using PaymentService.DB;
using PaymentService.Models;
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
            try
            {
                await RunCycleAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "StripeWorker cycle failed");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    // Fetches all accounts' charges first, then runs the whole batch through PaymentMatchingService
    // once — matching per-account would load the (shared, thousands-of-rows) open-invoice list
    // once per account for no reason, since invoices aren't scoped by Stripe account.
    private async Task RunCycleAsync()
    {
        var allTransactions = new List<NormalizedTransaction>();
        var sources = new List<string>();

        foreach (var (accountName, client) in _clients)
        {
            var source = $"STRIPE_{accountName.ToUpperInvariant()}";
            try
            {
                var since = await _repo.GetLastSyncDateAsync(source) ?? DateTime.UtcNow.AddDays(-7);
                _logger.LogInformation("Stripe [{Account}]: fetching charges since {Since}", accountName, since);

                allTransactions.AddRange(await client.GetSucceededChargesSinceAsync(since));
                sources.Add(source);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Stripe [{Account}]: fetch failed — sync state not advanced, will retry next cycle",
                    accountName);
            }
        }

        var allSucceeded = await _matcher.ProcessTransactionsAsync(allTransactions);
        if (allSucceeded)
        {
            var now = DateTime.UtcNow;
            foreach (var source in sources)
                await _repo.UpsertSyncStateAsync(source, now);
        }
        else
        {
            _logger.LogWarning("Stripe: some transactions failed — sync state not advanced, will retry next cycle");
        }
    }
}
