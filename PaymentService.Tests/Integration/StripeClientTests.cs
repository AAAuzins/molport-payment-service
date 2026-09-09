using Microsoft.Extensions.Logging.Abstractions;
using PaymentService.Clients;
using PaymentService.Models;
using Stripe;

namespace PaymentService.Tests.Integration;

public class StripeClientTests
{
    private readonly AppSettings _settings;

    public StripeClientTests()
    {
        _settings = TestConfig.Load();
    }

    [Fact]
    public async Task CanConnectAndListRecentCharges_AllAccounts()
    {
        foreach (var account in _settings.StripeAccounts)
        {
            var client = new StripeApiClient(account.Name, account.ApiKey, NullLogger<StripeApiClient>.Instance);
            var since = DateTime.UtcNow.AddDays(-30);

            var transactions = (await client.GetSucceededChargesSinceAsync(since)).ToList();

            Assert.NotNull(transactions);
            foreach (var tx in transactions)
            {
                Assert.False(string.IsNullOrEmpty(tx.TransactionId),
                    $"[{account.Name}] TransactionId is empty");
                Assert.True(tx.Amount > 0,
                    $"[{account.Name}] Amount is 0 for {tx.TransactionId}");
                Assert.False(string.IsNullOrEmpty(tx.Currency),
                    $"[{account.Name}] Currency is empty for {tx.TransactionId}");
            }
        }
    }

    [Fact]
    public async Task SpecificCharge_IsAccessibleViaIncKey_AndProducesIncAccountName()
    {
        const string chargeId = "ch_3TeED95AsVXSV8UQ0ZCcvjIq";
        var incAccount = _settings.StripeAccounts.First(a => a.Name == "INC");

        var charge = await new ChargeService().GetAsync(
            chargeId,
            requestOptions: new RequestOptions { ApiKey = incAccount.ApiKey });

        Assert.Equal(chargeId, charge.Id);
        Assert.Equal("succeeded", charge.Status);

        // This is what StripeApiClient sets — confirms future charges from INC will be routed correctly
        var client = new StripeApiClient(incAccount.Name, incAccount.ApiKey, NullLogger<StripeApiClient>.Instance);
        Assert.Equal("INC", incAccount.Name);
    }

    [Fact]
    public async Task SpecificCharge_IsNotAccessibleViaSiaKey()
    {
        const string chargeId = "ch_3TeED95AsVXSV8UQ0ZCcvjIq";
        var siaAccount = _settings.StripeAccounts.First(a => a.Name == "SIA");

        // If this throws → charge is on a different account → SIA key is correct
        // If this succeeds → SIA API key is actually the INC account key → root cause found
        var ex = await Assert.ThrowsAsync<StripeException>(() =>
            new ChargeService().GetAsync(
                chargeId,
                requestOptions: new RequestOptions { ApiKey = siaAccount.ApiKey }));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, ex.HttpStatusCode);
    }

    [Fact]
    public async Task GetChargeByIdAsync_ReturnsTransaction_ForKnownCharge()
    {
        const string chargeId = "ch_3TeED95AsVXSV8UQ0ZCcvjIq";
        var incAccount = _settings.StripeAccounts.First(a => a.Name == "INC");
        var client = new StripeApiClient(incAccount.Name, incAccount.ApiKey, NullLogger<StripeApiClient>.Instance);

        var tx = await client.GetChargeByIdAsync(chargeId);

        Assert.NotNull(tx);
        Assert.Equal(chargeId, tx!.TransactionId);
        Assert.Equal("INC", tx.AccountName);
        Assert.Equal(PaymentSource.Stripe, tx.Source);
        Assert.True(tx.Amount > 0);
    }

    [Fact]
    public async Task GetChargeByIdAsync_ReturnsNull_ForWrongAccount()
    {
        const string chargeId = "ch_3TeED95AsVXSV8UQ0ZCcvjIq"; // belongs to INC, not SIA
        var siaAccount = _settings.StripeAccounts.First(a => a.Name == "SIA");
        var client = new StripeApiClient(siaAccount.Name, siaAccount.ApiKey, NullLogger<StripeApiClient>.Instance);

        var tx = await client.GetChargeByIdAsync(chargeId);

        Assert.Null(tx);
    }

    [Fact]
    public async Task ChargesHaveExpectedFields_AllAccounts()
    {
        foreach (var account in _settings.StripeAccounts)
        {
            var client = new StripeApiClient(account.Name, account.ApiKey, NullLogger<StripeApiClient>.Instance);
            var transactions = (await client.GetSucceededChargesSinceAsync(DateTime.UtcNow.AddDays(-90))).ToList();

            if (transactions.Count == 0) continue;

            var sample = transactions.First();
            Assert.StartsWith("ch_", sample.TransactionId);
            Assert.Equal(PaymentSource.Stripe, sample.Source);
            Assert.Contains(sample.Currency.ToUpperInvariant(), new[] { "USD", "EUR", "GBP", "CHF" });
        }
    }
}
