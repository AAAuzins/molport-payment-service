namespace PaymentService.Models;

public enum HorizonImportOutcome
{
    Imported,
    AlreadyExists,
    MissingBillingOrg,
    NoClientConfigured,
    CustomerCreationFailed
}
