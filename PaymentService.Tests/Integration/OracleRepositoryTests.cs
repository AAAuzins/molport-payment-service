using PaymentService.DB;
using PaymentService.Services;

namespace PaymentService.Tests.Integration;

public class OracleRepositoryTests
{
    private readonly OracleRepository _repo;

    public OracleRepositoryTests()
    {
        var settings = TestConfig.Load();
        _repo = new OracleRepository(settings.OracleConnectionString);
    }

    [Fact]
    public async Task CanConnectAndLoadCurrencies()
    {
        var map = await _repo.GetCurrencyMapAsync();

        Assert.NotEmpty(map);
        Assert.True(map.ContainsKey("EUR"), "EUR currency not found");
        Assert.True(map.ContainsKey("USD"), "USD currency not found");
        Assert.Equal(3, map["EUR"]);
        Assert.Equal(2, map["USD"]);
    }

    [Fact]
    public async Task CurrencyMapMatchesHardcodedFallback()
    {
        var fromDb = await _repo.GetCurrencyMapAsync();

        foreach (var (code, id) in CurrencyCache.Hardcoded)
            Assert.True(fromDb.TryGetValue(code, out var dbId) && dbId == id,
                $"Currency {code} mismatch: hardcoded={id}, DB={fromDb.GetValueOrDefault(code)}");
    }

    [Fact]
    public async Task CanLoadOpenInvoices()
    {
        var invoices = (await _repo.GetOpenAdvancedPaymentInvoicesAsync()).ToList();

        Assert.NotNull(invoices);
        foreach (var inv in invoices)
        {
            Assert.True(inv.InvoiceId > 0);
            Assert.True(inv.OrderId > 0);
            Assert.True(inv.Amount > 0);
            Assert.True(inv.CurrencyId > 0);
        }
    }

    [Fact]
    public async Task PaymentExistsReturnsFalseForNonexistentTransaction()
    {
        var exists = await _repo.PaymentExistsForTransactionAsync("TEST_TX_DOES_NOT_EXIST_XYZ");

        Assert.False(exists);
    }

    [Fact]
    public async Task SyncStateUpsertRoundTrips()
    {
        const string testSource = "TEST";
        var testDate = new DateTime(2026, 1, 1);

        await _repo.UpsertSyncStateAsync(testSource, testDate);
        var retrieved = await _repo.GetLastSyncDateAsync(testSource);

        Assert.NotNull(retrieved);
        Assert.Equal(testDate.Date, retrieved!.Value.Date);

        // Clean up
        await _repo.UpsertSyncStateAsync(testSource, DateTime.MinValue);
    }

    [Fact]
    public async Task ReviewItemUpsertSucceeds()
    {
        // Verifies OT_PAYMENT_REVIEW exists and zoho has MERGE (insert+update) privilege.
        // Leaves one row with SOURCE='TEST' — safe to delete manually.
        var transactionId = "TEST_REVIEW_PERMS_" + DateTime.UtcNow.Ticks;

        await _repo.UpsertReviewItemAsync(
            source: "TEST",
            transactionId: transactionId,
            transactionDate: DateTime.UtcNow,
            transactionAmount: 1.00m,
            transactionCurrencyId: 2,
            invoiceId: null,
            orderId: null,
            expectedAmount: null,
            expectedCurrencyId: null,
            matchType: "NoMatch",
            rawDescription: "automated permission check — safe to delete");

        // Re-running with the same transactionId should update the existing row, not insert a duplicate.
        await _repo.UpsertReviewItemAsync(
            source: "TEST",
            transactionId: transactionId,
            transactionDate: DateTime.UtcNow,
            transactionAmount: 2.00m,
            transactionCurrencyId: 2,
            invoiceId: null,
            orderId: null,
            expectedAmount: null,
            expectedCurrencyId: null,
            matchType: "AmountMismatch",
            rawDescription: "automated permission check — safe to delete");
    }

    [Fact]
    public async Task DeleteReviewItemsForTransactionSucceeds()
    {
        var transactionId = "TEST_REVIEW_DELETE_" + DateTime.UtcNow.Ticks;
        await _repo.UpsertReviewItemAsync(
            source: "TEST", transactionId: transactionId, transactionDate: DateTime.UtcNow,
            transactionAmount: 1.00m, transactionCurrencyId: 2, invoiceId: null, orderId: null,
            expectedAmount: null, expectedCurrencyId: null, matchType: "NoMatch",
            rawDescription: "automated permission check — safe to delete");

        await _repo.DeleteReviewItemsForTransactionAsync(transactionId);
    }
}
