using Microsoft.Extensions.Logging;
using PaymentService.Models;
using PaymentService.Services;
using Stripe;

namespace PaymentService.Clients;

public class StripeApiClient
{
    private readonly string _apiKey;
    private readonly string _accountName;
    private readonly ChargeService _chargeService = new();
    private readonly ILogger<StripeApiClient> _logger;

    public StripeApiClient(string accountName, string apiKey, ILogger<StripeApiClient> logger)
    {
        _accountName = accountName;
        _apiKey = apiKey;
        _logger = logger;
    }

    public static Dictionary<string, StripeApiClient> CreateClients(AppSettings settings, ILogger<StripeApiClient> logger) =>
        settings.StripeAccounts.ToDictionary(a => a.Name, a => new StripeApiClient(a.Name, a.ApiKey, logger));

    public async Task<IEnumerable<NormalizedTransaction>> GetSucceededChargesSinceAsync(DateTime since)
    {
        var requestOptions = new RequestOptions { ApiKey = _apiKey };
        var options = new ChargeListOptions
        {
            Created = new DateRangeOptions { GreaterThanOrEqual = since },
            Limit = 100,
            Expand = ["data.balance_transaction"]
        };

        var results = new List<NormalizedTransaction>();
        var page = await _chargeService.ListAsync(options, requestOptions);

        while (true)
        {
            results.AddRange(page
                .Where(c => c.Status == "succeeded" && c.Paid && !c.Refunded)
                .Select(MapCharge));

            if (!page.HasMore) break;

            options.StartingAfter = page.LastOrDefault()?.Id;
            page = await _chargeService.ListAsync(options, requestOptions);
        }

        _logger.LogInformation("Fetched {Count} succeeded Stripe charges since {Since}", results.Count, since);
        return results;
    }

    // Re-fetches a single charge by id, e.g. to retry a previously failed/unmatched transaction
    // without having to persist its amount/currency/fee/reference locally.
    public async Task<NormalizedTransaction?> GetChargeByIdAsync(string chargeId)
    {
        var requestOptions = new RequestOptions { ApiKey = _apiKey };
        var options = new ChargeGetOptions { Expand = ["balance_transaction"] };

        Charge charge;
        try
        {
            charge = await _chargeService.GetAsync(chargeId, options, requestOptions);
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Stripe [{Account}]: failed to re-fetch charge {ChargeId}", _accountName, chargeId);
            return null;
        }

        if (charge.Status != "succeeded" || !charge.Paid || charge.Refunded)
        {
            _logger.LogWarning(
                "Stripe [{Account}]: charge {ChargeId} is no longer succeeded/unrefunded (status={Status}, refunded={Refunded}) — skipping",
                _accountName, chargeId, charge.Status, charge.Refunded);
            return null;
        }

        return MapCharge(charge);
    }

    private NormalizedTransaction MapCharge(Charge charge)
    {
        charge.Metadata.TryGetValue("order", out var orderRef);
        charge.Metadata.TryGetValue("invoice", out var invoiceRef);
        var rawDescription = charge.Description ?? orderRef ?? invoiceRef ?? string.Empty;
        var reference = invoiceRef ?? orderRef
            ?? ReferenceExtractor.Extract(rawDescription);

        return new NormalizedTransaction
        {
            Source = PaymentSource.Stripe,
            TransactionId = charge.Id,
            TransactionDate = charge.Created,
            Amount = charge.Amount / 100m,
            Currency = charge.Currency.ToUpperInvariant(),
            Description = rawDescription,
            ExtractedReference = reference,
            StripeFee = charge.BalanceTransaction != null ? charge.BalanceTransaction.Fee / 100m : null,
            AccountName = _accountName
        };
    }
}
