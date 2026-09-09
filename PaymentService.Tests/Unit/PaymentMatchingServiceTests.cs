using PaymentService.Models;
using PaymentService.Services;

namespace PaymentService.Tests.Unit;

public class PaymentMatchingServiceTests
{
    private static OpenInvoice Invoice(long id, string invoiceNumber = "", string orderNumber = "") => new()
    {
        InvoiceId = id,
        OrderId = id,
        InvoiceNumber = invoiceNumber,
        OrderNumber = orderNumber,
        Amount = 100m,
        CurrencyCode = "USD",
        CurrencyId = 2
    };

    private static NormalizedTransaction Tx(string? extractedReference) => new()
    {
        Source = PaymentSource.Stripe,
        TransactionId = "ch_test",
        TransactionDate = DateTime.UtcNow,
        Amount = 100m,
        Currency = "USD",
        ExtractedReference = extractedReference
    };

    private static OpenInvoice? Find(string? reference, params OpenInvoice[] invoices)
    {
        var index = PaymentMatchingService.BuildInvoiceIndex(invoices.ToList());
        return PaymentMatchingService.FindInvoice(Tx(reference), index);
    }

    [Fact]
    public void FindInvoice_ExactInvoiceNumberMatch()
    {
        var invoice = Invoice(1, invoiceNumber: "YF27O3045123-I1");

        var result = Find("YF27O3045123-I1", invoice);

        Assert.Same(invoice, result);
    }

    [Fact]
    public void FindInvoice_ExactMatchIsCaseInsensitive()
    {
        var invoice = Invoice(1, invoiceNumber: "YF27O3045123-I1");

        var result = Find("yf27o3045123-i1", invoice);

        Assert.Same(invoice, result);
    }

    [Fact]
    public void FindInvoice_OrderNumberPrefixMatch()
    {
        // reference is the bare order-number portion; invoice number has an "-I1" invoice suffix.
        var invoice = Invoice(1, invoiceNumber: "YF27O3045123-I1");

        var result = Find("YF27O3045123", invoice);

        Assert.Same(invoice, result);
    }

    [Fact]
    public void FindInvoice_PrefixMatchDoesNotFalselyMatchLongerSharedPrefix()
    {
        // "YF27O3045123" must not match "YF27O30451230-I1" — that's a different order number
        // that merely starts with the same digits.
        var invoice = Invoice(1, invoiceNumber: "YF27O30451230-I1");

        var result = Find("YF27O3045123", invoice);

        Assert.Null(result);
    }

    [Fact]
    public void FindInvoice_FallsBackToOrderNumberMatch()
    {
        var invoice = Invoice(1, invoiceNumber: "MI00000D3HAKVXI1", orderNumber: "ORD-555");

        var result = Find("ORD-555", invoice);

        Assert.Same(invoice, result);
    }

    [Fact]
    public void FindInvoice_ExactInvoiceNumberMatchTakesPriorityOverPrefixMatch()
    {
        var exact = Invoice(1, invoiceNumber: "YF27O3045123");
        var prefixed = Invoice(2, invoiceNumber: "YF27O3045123-I1");

        var result = Find("YF27O3045123", exact, prefixed);

        Assert.Same(exact, result);
    }

    [Fact]
    public void FindInvoice_PrefixMatchTakesPriorityOverOrderNumberMatch()
    {
        var byPrefix = Invoice(1, invoiceNumber: "YF27O3045123-I1");
        var byOrderNumber = Invoice(2, invoiceNumber: "OTHER-1", orderNumber: "YF27O3045123");

        var result = Find("YF27O3045123", byPrefix, byOrderNumber);

        Assert.Same(byPrefix, result);
    }

    [Fact]
    public void FindInvoice_NoMatchReturnsNull()
    {
        var invoice = Invoice(1, invoiceNumber: "YF27O3045123-I1");

        var result = Find("SOME-OTHER-REFERENCE", invoice);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FindInvoice_NullOrBlankReferenceReturnsNullWithoutSearching(string? reference)
    {
        var invoice = Invoice(1, invoiceNumber: "YF27O3045123-I1");

        var result = Find(reference, invoice);

        Assert.Null(result);
    }

    [Fact]
    public void FindInvoice_InvoiceNumberWithoutDashIsNotEligibleForPrefixMatch()
    {
        // No "-" in the invoice number at all, so it can only ever be found by exact match.
        var invoice = Invoice(1, invoiceNumber: "MI00000D3HAKVXI1");

        var result = Find("MI00000D3HAKVXI", invoice);

        Assert.Null(result);
    }

    [Fact]
    public void FindInvoice_DuplicateInvoiceNumbersResolveToTheFirstOneInList()
    {
        var first = Invoice(1, invoiceNumber: "DUP-1-I1");
        var second = Invoice(2, invoiceNumber: "DUP-1-I1");

        var result = Find("DUP-1-I1", first, second);

        Assert.Same(first, result);
    }

    [Fact]
    public void BuildInvoiceIndex_IgnoresInvoicesWithNoInvoiceOrOrderNumber()
    {
        var blank = Invoice(1);

        var index = PaymentMatchingService.BuildInvoiceIndex([blank]);

        Assert.Empty(index.ByNumber);
        Assert.Empty(index.ByOrderPrefix);
        Assert.Empty(index.ByOrderNumber);
    }

    [Fact]
    public void BuildInvoiceIndex_HandlesEmptyInvoiceList()
    {
        var index = PaymentMatchingService.BuildInvoiceIndex([]);

        Assert.Empty(index.ByNumber);
        Assert.Empty(index.ByOrderPrefix);
        Assert.Empty(index.ByOrderNumber);
    }
}
