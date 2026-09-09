using Microsoft.Extensions.Logging;
using PaymentService.DB;
using PaymentService.Models;

namespace PaymentService.Services;

public class PaymentMatchingService
{
    private readonly OracleRepository _repo;
    private readonly CurrencyCache _currencyCache;
    private readonly HorizonService _horizon;
    private readonly ILogger<PaymentMatchingService> _logger;

    public PaymentMatchingService(OracleRepository repo, CurrencyCache currencyCache,
        HorizonService horizon, ILogger<PaymentMatchingService> logger)
    {
        _repo = repo;
        _currencyCache = currencyCache;
        _horizon = horizon;
        _logger = logger;
    }

    public async Task<bool> ProcessTransactionsAsync(IEnumerable<NormalizedTransaction> transactions)
    {
        var txList = transactions.ToList();
        if (txList.Count == 0) return true;

        var openInvoices = (await _repo.GetOpenAdvancedPaymentInvoicesAsync()).ToList();
        var currencyMap = await _currencyCache.GetMapAsync();
        _logger.LogInformation("Loaded {Count} open invoices awaiting payment", openInvoices.Count);

        var allSucceeded = true;
        foreach (var tx in txList)
        {
            try
            {
                var result = await MatchAsync(tx, openInvoices);
                await RecordResultAsync(result, currencyMap);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[{Source}] Failed to process transaction {TxId} — will retry next cycle",
                    tx.Source, tx.TransactionId);
                allSucceeded = false;
            }
        }
        return allSucceeded;
    }

    private async Task<MatchResult> MatchAsync(NormalizedTransaction tx, List<OpenInvoice> openInvoices)
    {
        if (await _repo.PaymentExistsForTransactionAsync(tx.TransactionId))
        {
            return new MatchResult { Outcome = MatchOutcome.AlreadyProcessed, Transaction = tx };
        }

        var invoice = FindInvoice(tx, openInvoices);

        if (invoice == null)
        {
            return new MatchResult
            {
                Outcome = MatchOutcome.NoMatch,
                Transaction = tx,
                Details = $"No open invoice found matching reference '{tx.ExtractedReference}'"
            };
        }

        if (!string.Equals(tx.Currency, invoice.CurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            return new MatchResult
            {
                Outcome = MatchOutcome.CurrencyMismatch,
                Transaction = tx,
                Invoice = invoice,
                Details = $"Expected {invoice.CurrencyCode}, received {tx.Currency}"
            };
        }

        var tolerance = invoice.Amount * 0.01m; // 1% tolerance for rounding/fees
        if (Math.Abs(tx.Amount - invoice.Amount) > tolerance)
        {
            return new MatchResult
            {
                Outcome = MatchOutcome.AmountMismatch,
                Transaction = tx,
                Invoice = invoice,
                Details = $"Expected {invoice.Amount} {invoice.CurrencyCode}, received {tx.Amount} {tx.Currency}"
            };
        }

        return new MatchResult
        {
            Outcome = MatchOutcome.Matched,
            Transaction = tx,
            Invoice = invoice
        };
    }

    private OpenInvoice? FindInvoice(NormalizedTransaction tx, List<OpenInvoice> invoices)
    {
        if (string.IsNullOrWhiteSpace(tx.ExtractedReference))
            return null;

        var reference = tx.ExtractedReference;

        // Exact invoice number match (e.g. reference = "YF27O3045123-I1")
        var byInvoice = invoices.FirstOrDefault(i =>
            !string.IsNullOrEmpty(i.InvoiceNumber) &&
            i.InvoiceNumber.Equals(reference, StringComparison.OrdinalIgnoreCase));
        if (byInvoice != null) return byInvoice;

        // Invoice starts with the order-number portion of the reference + "-" to avoid
        // false matches with orders that share a common prefix (e.g. "YF27O3045123-" won't
        // match "YF27O30451230-I1")
        byInvoice = invoices.FirstOrDefault(i =>
            !string.IsNullOrEmpty(i.InvoiceNumber) &&
            i.InvoiceNumber.StartsWith(reference + "-", StringComparison.OrdinalIgnoreCase));
        if (byInvoice != null) return byInvoice;

        // Exact order number match
        return invoices.FirstOrDefault(i =>
            !string.IsNullOrEmpty(i.OrderNumber) &&
            i.OrderNumber.Equals(reference, StringComparison.OrdinalIgnoreCase));
    }

    private async Task RecordResultAsync(MatchResult result, Dictionary<string, long> currencyMap)
    {
        var tx = result.Transaction;

        switch (result.Outcome)
        {
            case MatchOutcome.Matched:
                var inv = result.Invoice!;
                await _repo.InsertPaymentAsync(
                    inv.InvoiceId, inv.OrderId, tx.Amount, inv.CurrencyId,
                    tx.TransactionDate, tx.TransactionId, tx.Source == PaymentSource.Stripe);
                await _repo.UpdateInvoiceBalanceAsync(inv.InvoiceId);
                // A payment now exists for this transaction, so any stale review row from an earlier
                // failed attempt (NoMatch/AmountMismatch/etc.) no longer applies — clear it before
                // possibly recording a new (different) reason below.
                await _repo.DeleteReviewItemsForTransactionAsync(tx.TransactionId);
                _logger.LogInformation(
                    "[{Source}] Matched transaction {TxId} → Invoice {InvoiceNr} ({Amount} {Currency})",
                    tx.Source, tx.TransactionId, inv.InvoiceNumber, tx.Amount, tx.Currency);
                if (tx.Source == PaymentSource.Stripe)
                {
                    try
                    {
                        var horizonOutcome = await _horizon.ImportAsync(tx, inv);
                        if (horizonOutcome == HorizonImportOutcome.MissingBillingOrg)
                        {
                            await _repo.UpsertReviewItemAsync(
                                StripeSourceLabel(tx), tx.TransactionId, tx.TransactionDate,
                                tx.Amount, inv.CurrencyId,
                                inv.InvoiceId, inv.OrderId,
                                inv.Amount, inv.CurrencyId,
                                "HorizonPendingBillingOrg", tx.Description);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "[{Source}] Horizon import failed for transaction {TxId} — payment recorded, Horizon skipped",
                            tx.Source, tx.TransactionId);
                    }
                }
                break;

            case MatchOutcome.AlreadyProcessed:
                _logger.LogDebug("[{Source}] Transaction {TxId} already processed, skipping",
                    tx.Source, tx.TransactionId);
                break;

            case MatchOutcome.NoMatch:
            case MatchOutcome.AmountMismatch:
            case MatchOutcome.CurrencyMismatch:
                if (!currencyMap.TryGetValue(tx.Currency.ToUpperInvariant(), out var txCurrencyId))
                {
                    _logger.LogWarning(
                        "[{Source}] Unknown currency code '{Currency}' for transaction {TxId}, skipping review insert",
                        tx.Source, tx.Currency, tx.TransactionId);
                    break;
                }
                await _repo.UpsertReviewItemAsync(
                    StripeSourceLabel(tx), tx.TransactionId, tx.TransactionDate,
                    tx.Amount, txCurrencyId,
                    result.Invoice?.InvoiceId, result.Invoice?.OrderId,
                    result.Invoice?.Amount, result.Invoice?.CurrencyId,
                    result.Outcome.ToString(), tx.Description);
                _logger.LogWarning(
                    "[{Source}] Transaction {TxId} flagged for review: {Outcome} — {Details}",
                    tx.Source, tx.TransactionId, result.Outcome, result.Details);
                break;

            default:
                _logger.LogError("[{Source}] Unhandled MatchOutcome {Outcome} for transaction {TxId}",
                    tx.Source, result.Outcome, tx.TransactionId);
                break;
        }
    }

    // Encodes the Stripe account (SIA/INC) into the review row's SOURCE column so PaymentReviewRetryWorker
    // knows which API key to use when re-fetching the charge later.
    private static string StripeSourceLabel(NormalizedTransaction tx) =>
        tx.Source == PaymentSource.Stripe ? $"STRIPE_{tx.AccountName}" : tx.Source.ToString();
}
