using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentService.Clients;
using PaymentService.DB;
using PaymentService.Models;
using PaymentService.Services;

namespace PaymentService.Workers;

// Daily sweep over ORDER_TRACKING.OT_PAYMENT_REVIEW: re-attempts anything that previously failed
// to match (NoMatch/AmountMismatch/CurrencyMismatch) or matched but couldn't reach Horizon
// (HorizonPendingBillingOrg), in case the underlying data (missing invoice, wrong amount/reference,
// missing billing org) has since been fixed. Transaction details are re-fetched from Stripe by charge
// id rather than persisted locally, so amount/currency/fee/reference are always accurate and no schema
// change to the review table is needed. Only Stripe-sourced rows are retried today.
// Rows older than PaymentReviewRetryMaxAgeDays are no longer picked up, so a genuinely-never-fixed
// item stops being retried instead of continuing forever — it stays visible in BO's review view for
// manual handling.
public class PaymentReviewRetryWorker : BackgroundService
{
    private readonly OracleRepository _repo;
    private readonly HorizonService _horizon;
    private readonly PaymentMatchingService _matcher;
    private readonly ILogger<PaymentReviewRetryWorker> _logger;
    private readonly Dictionary<string, StripeApiClient> _stripeClients;
    private readonly TimeSpan _interval;
    private readonly int _maxAgeDays;

    private const string HorizonPendingBillingOrg = "HorizonPendingBillingOrg";
    private const string StripeSourcePrefix = "STRIPE_";

    public PaymentReviewRetryWorker(OracleRepository repo, HorizonService horizon, PaymentMatchingService matcher,
        IOptions<AppSettings> settings, ILogger<PaymentReviewRetryWorker> logger, ILogger<StripeApiClient> stripeClientLogger)
    {
        _repo = repo;
        _horizon = horizon;
        _matcher = matcher;
        _logger = logger;
        _interval = TimeSpan.FromHours(settings.Value.PaymentReviewRetryIntervalHours);
        _maxAgeDays = settings.Value.PaymentReviewRetryMaxAgeDays;
        _stripeClients = StripeApiClient.CreateClients(settings.Value, stripeClientLogger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PaymentReviewRetryWorker started, retrying every {Interval}", _interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunRetryPassAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "PaymentReviewRetryWorker pass failed");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task RunRetryPassAsync()
    {
        var pending = (await _repo.GetPendingReviewItemsAsync(_maxAgeDays)).ToList();
        if (pending.Count == 0) return;

        _logger.LogInformation("PaymentReviewRetryWorker: re-checking {Count} pending review item(s)", pending.Count);

        foreach (var item in pending)
        {
            try
            {
                await RetryItemAsync(item);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PaymentReviewRetryWorker: retry failed for transaction {TxId}", item.TransactionId);
            }
        }
    }

    private async Task RetryItemAsync(PendingReviewItem item)
    {
        if (!item.Source.StartsWith(StripeSourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "PaymentReviewRetryWorker: skipping {TxId} — source '{Source}' is not a retryable Stripe account (legacy row or non-Stripe source)",
                item.TransactionId, item.Source);
            return;
        }

        var accountName = item.Source[StripeSourcePrefix.Length..];
        if (!_stripeClients.TryGetValue(accountName, out var client))
        {
            _logger.LogWarning(
                "PaymentReviewRetryWorker: no Stripe client configured for account '{Account}' (transaction {TxId})",
                accountName, item.TransactionId);
            return;
        }

        var tx = await client.GetChargeByIdAsync(item.TransactionId);
        if (tx == null) return; // charge no longer retrievable/valid — leave for manual review

        if (item.MatchType == HorizonPendingBillingOrg)
            await RetryHorizonPendingAsync(item, tx);
        else
        {
            // Re-run through the normal matching pipeline in case the invoice was created/fixed since.
            // PaymentMatchingService itself deletes this row on a full match, or upserts it in place
            // (preserving the original CREATED date) if it still fails.
            await _matcher.ProcessTransactionsAsync([tx]);
        }
    }

    private async Task RetryHorizonPendingAsync(PendingReviewItem item, NormalizedTransaction tx)
    {
        if (item.InvoiceId == null) return;

        var billingOrg = await _repo.GetInvoiceBillingOrgAsync(item.InvoiceId.Value);
        if (billingOrg?.BillingCode == null) return; // still not fixed — try again next sweep

        var invoiceNumber = await _repo.GetInvoiceNumberAsync(item.InvoiceId.Value);
        if (invoiceNumber == null)
        {
            _logger.LogError("PaymentReviewRetryWorker: invoice {InvoiceId} no longer exists for transaction {TxId}",
                item.InvoiceId, item.TransactionId);
            return;
        }

        var currencyMap = await _repo.GetCurrencyMapAsync();
        if (!currencyMap.TryGetValue(tx.Currency.ToUpperInvariant(), out var currencyId))
        {
            _logger.LogError("PaymentReviewRetryWorker: unknown currency '{Currency}' for transaction {TxId}",
                tx.Currency, item.TransactionId);
            return;
        }

        var invoice = new OpenInvoice
        {
            InvoiceId = item.InvoiceId.Value,
            OrderId = item.OrderId ?? 0,
            InvoiceNumber = invoiceNumber,
            Amount = tx.Amount,
            CurrencyCode = tx.Currency,
            CurrencyId = currencyId
        };

        var outcome = await _horizon.ImportAsync(tx, invoice);
        if (outcome is HorizonImportOutcome.Imported or HorizonImportOutcome.AlreadyExists)
        {
            await _repo.DeleteReviewItemsForTransactionAsync(item.TransactionId);
            _logger.LogInformation(
                "PaymentReviewRetryWorker: resolved pending Horizon import for transaction {TxId}", item.TransactionId);
        }
    }
}
