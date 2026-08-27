using Dapper;
using Oracle.ManagedDataAccess.Client;
using PaymentService.Models;

namespace PaymentService.DB;

public class OracleRepository
{
    private readonly string _connectionString;

    public OracleRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    private OracleConnection OpenConnection() => new(_connectionString);

    public async Task<IEnumerable<OpenInvoice>> GetOpenAdvancedPaymentInvoicesAsync()
    {
        const string sql = """
            SELECT * FROM (
                SELECT
                    i.ID                    AS InvoiceId,
                    i.ORDER_ID              AS OrderId,
                    i.invoice_number        AS InvoiceNumber,
                    o.Molport_Order_Number  AS OrderNumber,
                    NVL(i.BALANCE_DUE,
                        (NVL(i.PRICE, 0) - NVL(i.PRICE_DISCOUNT, 0)
                         + NVL(i.QC_PRICE, 0) + NVL(i.REFORMATTING_PRICE, 0) + NVL(i.REWEIGHING_PRICE, 0)
                           + NVL(i.DRY_ICE_PRICE, 0) + NVL(i.HANDLING_COST_PRICE, 0)
                         + NVL(i.VAT21_PRICE, 0) + NVL(i.REIMBURSEMENT_PRICE, 0) + NVL(i.SALES_TAX_PRICE, 0)
                         + NVL(i.SHIPPING_PRICE, 0) + NVL(i.TARIFF_AMOUNT, 0))
                        - NVL(i.PAID_AMOUNT, 0)) AS Amount,
                    c.CODE                  AS CurrencyCode,
                    i.CURRENCY_ID           AS CurrencyId
                FROM ORDER_TRACKING.OT_INVOICE i
                JOIN ORDER_TRACKING.OT_ORDER o ON o.ID = i.ORDER_ID
                JOIN MOLPORT.ADM_CODIF_ENTRY c ON c.ID = i.CURRENCY_ID
                WHERE i.PRICE > 0
                  AND NOT EXISTS (
                      SELECT 1 FROM ORDER_TRACKING.OT_PAYMENT p
                      WHERE p.INVOICE_ID = i.ID
                  )
            )
            WHERE Amount > 0
            """;

        using var conn = OpenConnection();
        return await conn.QueryAsync<OpenInvoice>(sql);
    }

    public async Task<bool> PaymentExistsForTransactionAsync(string transactionId)
    {
        const string sql = """
            SELECT COUNT(1) FROM ORDER_TRACKING.OT_PAYMENT
            WHERE BATCH_NR = :transactionId
            """;

        using var conn = OpenConnection();
        var count = await conn.ExecuteScalarAsync<int>(sql, new { transactionId });
        return count > 0;
    }

    public async Task InsertPaymentAsync(long invoiceId, long orderId, decimal amount, long currencyId,
        DateTime receivedDate, string transactionId, bool isPaidByCC)
    {
        const string sql = """
            INSERT INTO ORDER_TRACKING.OT_PAYMENT
                (INVOICE_ID, ORDER_ID, PAYMENT_AMOUNT, PAYMENT_CURRENCY_ID,
                 PAYMENT_RECEIVED_DATE, BATCH_NR, IS_PREPAID_BY_CC,
                 IS_CHECQUE_RECEIVED, IS_CREDIT, CREATED, MODIFIED)
            VALUES
                (:invoiceId, :orderId, :amount, :currencyId,
                 :receivedDate, :transactionId, :isPaidByCC,
                 0, 0, SYSDATE, SYSDATE)
            """;

        using var conn = OpenConnection();
        await conn.ExecuteAsync(sql, new
        {
            invoiceId,
            orderId,
            amount,
            currencyId,
            receivedDate,
            transactionId,
            isPaidByCC = isPaidByCC ? -1 : 0
        });
    }

    public async Task<Dictionary<string, long>> GetCurrencyMapAsync()
    {
        const string sql = """
            SELECT CODE, ID FROM MOLPORT.ADM_CODIF_ENTRY
            WHERE ADM_CODIFICATOR_ID = 1
            AND DELETED = 'N'
            """;

        using var conn = OpenConnection();
        var rows = await conn.QueryAsync<(string Code, long Id)>(sql);
        return rows.ToDictionary(r => r.Code.ToUpperInvariant(), r => r.Id);
    }

    public async Task InsertReviewItemAsync(string source, string transactionId, DateTime transactionDate,
        decimal transactionAmount, long transactionCurrencyId, long? invoiceId, long? orderId,
        decimal? expectedAmount, long? expectedCurrencyId, string matchType, string rawDescription)
    {
        const string sql = """
            INSERT INTO ORDER_TRACKING.OT_PAYMENT_REVIEW
                (SOURCE, TRANSACTION_ID, TRANSACTION_DATE, TRANSACTION_AMOUNT,
                 TRANSACTION_CURRENCY_ID, INVOICE_ID, ORDER_ID,
                 EXPECTED_AMOUNT, EXPECTED_CURRENCY_ID, MATCH_TYPE,
                 RAW_DESCRIPTION, CREATED)
            VALUES
                (:source, :transactionId, :transactionDate, :transactionAmount,
                 :transactionCurrencyId, :invoiceId, :orderId,
                 :expectedAmount, :expectedCurrencyId, :matchType,
                 :rawDescription, SYSDATE)
            """;

        using var conn = OpenConnection();
        await conn.ExecuteAsync(sql, new
        {
            source,
            transactionId,
            transactionDate,
            transactionAmount,
            transactionCurrencyId,
            invoiceId,
            orderId,
            expectedAmount,
            expectedCurrencyId,
            matchType,
            rawDescription
        });
    }

    public async Task<InvoiceBillingOrg?> GetInvoiceBillingOrgAsync(long invoiceId)
    {
        const string sql = """
            SELECT
                o.BILLING_CODE    AS BillingCode,
                o.BILLING_NAME    AS BillingName,
                o.BILLING_VAT     AS BillingVat,
                o.EIN_NUMBER      AS EinNumber,
                c.VALUETEXT1      AS CountryCode
            FROM ORDER_TRACKING.OT_INVOICE i
            JOIN ORDER_TRACKING.OT_ORGANISATION o ON o.ID = i.BILLING_ORG_ID
            LEFT JOIN MOLPORT.ADM_CODIF_ENTRY c ON c.ID = o.BILLING_COUNTRY_ID
            WHERE i.ID = :invoiceId
            """;

        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<InvoiceBillingOrg>(sql, new { invoiceId });
    }

    public async Task<DateTime?> GetLastSyncDateAsync(string source)
    {
        const string sql = """
            SELECT LAST_PROCESSED_DATE FROM ORDER_TRACKING.OT_PAYMENT_SYNC_STATE
            WHERE SOURCE = :source
            """;

        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<DateTime?>(sql, new { source });
    }

    public async Task UpsertSyncStateAsync(string source, DateTime processedDate)
    {
        const string sql = """
            MERGE INTO ORDER_TRACKING.OT_PAYMENT_SYNC_STATE t
            USING (SELECT :source AS SOURCE FROM DUAL) s
            ON (t.SOURCE = s.SOURCE)
            WHEN MATCHED THEN
                UPDATE SET LAST_PROCESSED_DATE = :processedDate, MODIFIED = SYSDATE
            WHEN NOT MATCHED THEN
                INSERT (SOURCE, LAST_PROCESSED_DATE, MODIFIED)
                VALUES (:source, :processedDate, SYSDATE)
            """;

        using var conn = OpenConnection();
        await conn.ExecuteAsync(sql, new { source, processedDate });
    }
}
