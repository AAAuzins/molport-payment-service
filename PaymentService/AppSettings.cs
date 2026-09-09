namespace PaymentService;

public class AppSettings
{
    public string OracleConnectionString { get; set; } = string.Empty;
    public List<StripeAccountSettings> StripeAccounts { get; set; } = [];
    public int StripePollIntervalMinutes { get; set; } = 15;
    public SwedBankSettings SwedBank { get; set; } = new();
    public int SwedBankPollIntervalHours { get; set; } = 6;
    public HorizonSettings Horizon { get; set; } = new();
    public int PaymentReviewRetryIntervalHours { get; set; } = 24;
    public int PaymentReviewRetryMaxAgeDays { get; set; } = 7;
}

public class SwedBankSettings
{
    public string ClientId { get; set; } = string.Empty;
    public string SandboxClientId { get; set; } = string.Empty;
    public long AgreementId { get; set; }
    public string Iban { get; set; } = string.Empty;
    public string CertificatePath { get; set; } = string.Empty;
    public string CertificatePassword { get; set; } = string.Empty;
    public bool UseSandbox { get; set; } = false;
}

public class StripeAccountSettings
{
    public string Name { get; set; } = string.Empty;   // e.g. "SIA" or "INC"
    public string ApiKey { get; set; } = string.Empty;
}

public class HorizonSettings
{
    public string BaseUrl { get; set; } = string.Empty;
    public string UserNameSia { get; set; } = string.Empty;
    public string PasswordSia { get; set; } = string.Empty;
    public string UserNameInc { get; set; } = string.Empty;
    public string PasswordInc { get; set; } = string.Empty;
}
