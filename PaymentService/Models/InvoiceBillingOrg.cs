namespace PaymentService.Models;

public class InvoiceBillingOrg
{
    public string InvoiceNumber { get; set; } = string.Empty;
    public int? BillingCode { get; set; }
    public string? BillingName { get; set; }
    public string? BillingVat { get; set; }
    public string? EinNumber { get; set; }
    public string? CountryCode { get; set; }
}
