namespace PaymentService.Models;

public class PendingReviewItem
{
    public string Source { get; set; } = string.Empty;
    public string TransactionId { get; set; } = string.Empty;
    public string MatchType { get; set; } = string.Empty;
    public long? InvoiceId { get; set; }
    public long? OrderId { get; set; }
}
