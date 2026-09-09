using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentService.Clients;
using PaymentService.DB;
using PaymentService.Models;

namespace PaymentService.Services;

public class HorizonService
{
    private const string PaymentOrderTemplateUrl = "/rest/TDdmInMu/template/10";
    private const string CustomerTemplateUrl = "/rest/TDdmCustomer/template/190";

    // Receiver accounts in Horizon
    private const string ReceiverStripeUsd = "/rest/TDdmSaviRek/1296";
    private const string ReceiverStripeEur = "/rest/TDdmSaviRek/1298";
    private const string ReceiverStripeInc = "/rest/TDdmSaviRek/47";
    private const string ReceiverHorizonSia = "/rest/TDdmSaviRekAll/7";
    private const string ReceiverHorizonInc = "/rest/TDdmSaviRekAll/8";

    // Fixed Horizon IDs used in memorial orders
    // Template 235 = SIA (integ), Template 15 = INC (US-INTEG) — company-specific
    private const string MemorialOrderTemplateSia = "/rest/TDdmMO/template/235";
    private const string MemorialOrderTemplateInc = "/rest/TDdmMO/template/15";
    private const string MemorialClientId = "/rest/TDdmCustomer/1";
    private const string CurrencyHref     = "/rest/TsdmValName/2";
    private const string PvnKatHref       = "/rest/TdmPvnKat/2";

    // Currency DB IDs (from MOLPORT.ADM_CODIF_ENTRY)
    private const long CurrencyIdUsd = 2;
    private const long CurrencyIdEur = 3;

    private readonly Dictionary<string, HorizonClient> _clients;
    private readonly OracleRepository _repo;
    private readonly ILogger<HorizonService> _logger;

    public HorizonService(IOptions<AppSettings> settings, OracleRepository repo,
        ILoggerFactory loggerFactory, ILogger<HorizonService> logger)
    {
        _repo = repo;
        _logger = logger;
        var s = settings.Value.Horizon;
        var clientLogger = loggerFactory.CreateLogger<HorizonClient>();
        _clients = new[]
        {
            new HorizonClient("SIA", s.BaseUrl, s.UserNameSia, s.PasswordSia, clientLogger),
            new HorizonClient("INC", s.BaseUrl, s.UserNameInc, s.PasswordInc, clientLogger),
        }.ToDictionary(c => c.AccountName);
    }

    public async Task<HorizonImportOutcome> ImportAsync(NormalizedTransaction tx, OpenInvoice invoice)
    {
        var accountName = tx.AccountName ?? "SIA";
        if (!_clients.TryGetValue(accountName, out var client))
        {
            _logger.LogError("Horizon: no client configured for account '{Account}'", accountName);
            return HorizonImportOutcome.NoClientConfigured;
        }

        var billingOrg = await _repo.GetInvoiceBillingOrgAsync(invoice.InvoiceId);
        if (billingOrg?.BillingCode == null)
        {
            _logger.LogWarning(
                "Horizon [{Account}]: invoice {InvoiceId} has no billing org or billing code — skipping",
                accountName, invoice.InvoiceId);
            return HorizonImportOutcome.MissingBillingOrg;
        }

        if (await client.ExistsByDocumentNumberAsync(tx.TransactionId))
        {
            _logger.LogInformation(
                "Horizon [{Account}]: payment order for {TxId} already exists — skipping",
                accountName, tx.TransactionId);
            return HorizonImportOutcome.AlreadyExists;
        }

        var customerRestId = await GetOrCreateCustomerAsync(client, billingOrg);
        if (customerRestId == null)
            return HorizonImportOutcome.CustomerCreationFailed;

        var receiverId = GetReceiverId(accountName, invoice.CurrencyId);
        var paymentOrderXml = await BuildPaymentOrderXmlAsync(client, tx, invoice, customerRestId, receiverId);
        var paymentOrderId = await client.SaveAsync(PaymentOrderTemplateUrl, paymentOrderXml);
        _logger.LogInformation(
            "Horizon [{Account}]: created payment order {HorizonId} for transaction {TxId}",
            accountName, paymentOrderId, tx.TransactionId);

        if (tx.StripeFee > 0)
        {
            try
            {
                await CreateMemorialOrderAsync(client, tx);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Horizon [{Account}]: memorial order (commission) failed for {TxId} — payment order was created, commission not recorded",
                    accountName, tx.TransactionId);
            }
        }

        return HorizonImportOutcome.Imported;
    }

    private async Task<string?> GetOrCreateCustomerAsync(HorizonClient client, InvoiceBillingOrg billingOrg)
    {
        var code = billingOrg.BillingCode!.Value.ToString();
        var restId = await client.GetCustomerRestIdByCodeAsync(code);
        if (restId != null) return restId;

        if (string.IsNullOrEmpty(billingOrg.CountryCode))
        {
            _logger.LogError(
                "Horizon [{Account}]: cannot create customer for billing code {Code}: no country code",
                client.AccountName, code);
            return null;
        }

        var countryRestId = await client.GetCountryRestIdByCodeAsync(billingOrg.CountryCode);
        if (countryRestId == null)
        {
            _logger.LogError(
                "Horizon [{Account}]: cannot create customer for billing code {Code}: country '{Country}' not found",
                client.AccountName, code, billingOrg.CountryCode);
            return null;
        }

        var templateXml = await client.GetTemplateAsync(CustomerTemplateUrl);
        var customerXml = BuildCustomerXml(templateXml, billingOrg, countryRestId);
        restId = await client.SaveAsync(CustomerTemplateUrl, customerXml);
        _logger.LogInformation(
            "Horizon [{Account}]: created customer {HorizonId} for billing code {Code}",
            client.AccountName, restId, code);
        return restId;
    }

    private async Task<string> BuildPaymentOrderXmlAsync(HorizonClient client, NormalizedTransaction tx,
        OpenInvoice invoice, string customerRestId, string receiverId)
    {
        var templateXml = await client.GetTemplateAsync(PaymentOrderTemplateUrl);
        var doc = XDocument.Parse(templateXml);
        var ns = doc.Descendants().First(e => e.Name.LocalName == "entity").Name.Namespace;

        XElement Find(string localName) =>
            doc.Descendants().First(e => e.Name.LocalName == localName);

        Find("DOK_NR").Value = tx.TransactionId;
        Find("DAT_DOK").Value = tx.TransactionDate.ToString("yyyy-MM-dd");

        Find("PK_KLIENTS").Descendants().First(e => e.Name.LocalName == "href").Value = customerRestId;

        // Template 10 pre-fills PK_R_ES with /rest/TDdmSaviRekAll/8 (Alliance); replace it rather than adding a second href
        var pkREs = Find("PK_R_ES");
        var existingHref = pkREs.Descendants().FirstOrDefault(e => e.Name.LocalName == "href");
        if (existingHref != null)
            existingHref.Value = receiverId;
        else
            pkREs.Add(new XElement(ns + "href", receiverId));

        var amountStr = invoice.Amount.ToString("0.##", CultureInfo.InvariantCulture);
        Find("SUMMA").Value = amountStr;
        Find("SUMMA_APM").Value = amountStr;

        Find("PK_VAL").Descendants().First(e => e.Name.LocalName == "href").Value = CurrencyHref;
        Find("PK_APMVAL").Descendants().First(e => e.Name.LocalName == "href").Value = CurrencyHref;

        Find("qryPamat").Add(new XElement(ns + "row",
            new XElement(ns + "PAMAT", invoice.InvoiceNumber),
            new XElement(ns + "SIMBSKAITS", invoice.InvoiceNumber.Length.ToString())));

        return doc.ToString();
    }

    private async Task CreateMemorialOrderAsync(HorizonClient client, NormalizedTransaction tx)
    {
        var templateUrl = client.AccountName == "INC" ? MemorialOrderTemplateInc : MemorialOrderTemplateSia;
        var templateXml = await client.GetTemplateAsync(templateUrl);
        var doc = XDocument.Parse(templateXml);
        var ns = doc.Descendants().First(e => e.Name.LocalName == "entity").Name.Namespace;

        XElement Find(string localName) =>
            doc.Descendants().First(e => e.Name.LocalName == localName);

        Find("DOK_NR").Value = tx.TransactionId;
        Find("DAT_DOK").Value = tx.TransactionDate.ToString("yyyy-MM-dd");
        Find("DAT_GRAM").Value = tx.TransactionDate.ToString("yyyy-MM-dd");

        Find("SUMMA_APM").Value = tx.StripeFee!.Value.ToString("0.##", CultureInfo.InvariantCulture);

        Find("PK_KLIENTS").Descendants().First(e => e.Name.LocalName == "href").Value = MemorialClientId;
        Find("PK_VAL").Descendants().First(e => e.Name.LocalName == "href").Value = CurrencyHref;
        Find("PK_APMVAL").Descendants().First(e => e.Name.LocalName == "href").Value = CurrencyHref;

        // INC template 15 pre-fills PK_R_ES with /8; SIA template 235 leaves it empty.
        var receiverHref = client.AccountName == "INC" ? ReceiverHorizonInc : ReceiverHorizonSia;
        var pkREs = Find("PK_R_ES");
        var existingHref = pkREs.Descendants().FirstOrDefault(e => e.Name.LocalName == "href");
        if (existingHref != null)
            existingHref.Value = receiverHref;
        else
            pkREs.Add(new XElement(ns + "href", receiverHref));

        const string reason = "Bankas komisija";
        Find("qryPamat").Add(new XElement(ns + "row",
            new XElement(ns + "PAMAT", reason),
            new XElement(ns + "SIMBSKAITS", reason.Length.ToString())));

        var xml = doc.ToString();
        var memorialId = await client.SaveAsync(templateUrl, xml);
        _logger.LogInformation(
            "Horizon [{Account}]: created memorial order {HorizonId} for transaction {TxId} (fee {Fee})",
            client.AccountName, memorialId, tx.TransactionId, tx.StripeFee);
    }

    private static string BuildCustomerXml(string templateXml, InvoiceBillingOrg billingOrg, string countryRestId)
    {
        var doc = XDocument.Parse(templateXml);
        var ns = doc.Descendants().First(e => e.Name.LocalName == "entity").Name.Namespace;

        XElement? FindOrNull(string localName) =>
            doc.Descendants().FirstOrDefault(e => e.Name.LocalName == localName);

        XElement? SetHref(string parentLocalName, string value)
        {
            var parent = FindOrNull(parentLocalName);
            if (parent == null) return null;
            var existing = parent.Descendants().FirstOrDefault(e => e.Name.LocalName == "href");
            if (existing != null)
                existing.Value = value;
            else
                parent.Add(new XElement(ns + "href", value));
            return parent;
        }

        FindOrNull("KODS")!.Value = billingOrg.BillingCode!.Value.ToString();
        FindOrNull("NOSAUK")!.Value = billingOrg.BillingName ?? string.Empty;

        var vat = billingOrg.BillingVat ?? billingOrg.EinNumber ?? string.Empty;
        var pvnRegnr = FindOrNull("PVN_REGNR");
        if (pvnRegnr != null) pvnRegnr.Value = vat;

        var rezidents = FindOrNull("REZIDENTS");
        if (rezidents != null) rezidents.Value = "0";

        SetHref("PK_VALSTS", countryRestId);
        SetHref("PK_PVNK", PvnKatHref);

        return doc.ToString();
    }

    private static string GetReceiverId(string accountName, long currencyId) =>
        accountName == "INC"        ? ReceiverStripeInc :
        currencyId == CurrencyIdUsd ? ReceiverStripeUsd :
        currencyId == CurrencyIdEur ? ReceiverStripeEur :
                                      ReceiverHorizonSia;
}
