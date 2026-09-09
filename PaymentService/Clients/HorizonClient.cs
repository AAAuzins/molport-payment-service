using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace PaymentService.Clients;

public sealed class HorizonClient : IDisposable
{
    private readonly string _baseUrl;
    private readonly string _username;
    private readonly string _password;
    private readonly ILogger<HorizonClient> _logger;
    private readonly HttpClient _http;
    private volatile bool _authenticated;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    public string AccountName { get; }

    public HorizonClient(string accountName, string baseUrl, string username, string password,
        ILogger<HorizonClient> logger)
    {
        AccountName = accountName;
        _baseUrl = baseUrl.TrimEnd('/');
        _username = username;
        _password = password;
        _logger = logger;

        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true
        };
        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/xml");
    }

    private async Task AuthenticateAsync()
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_username}:{_password}"));
        using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/rest/user");
        req.Headers.Add("Authorization", $"Basic {credentials}");
        var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        _authenticated = true;
        _logger.LogDebug("Horizon [{Account}]: authenticated", AccountName);
    }

    private async Task EnsureAuthenticatedAsync()
    {
        if (_authenticated) return;
        await _authLock.WaitAsync();
        try
        {
            if (!_authenticated)
                await AuthenticateAsync();
        }
        finally
        {
            _authLock.Release();
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync();
        throw new HttpRequestException(
            $"{(int)resp.StatusCode} {resp.ReasonPhrase} — {body[..Math.Min(800, body.Length)]}",
            null, resp.StatusCode);
    }

    // Horizon requires Content-Type: application/xml on all requests, including GETs
    private HttpRequestMessage BuildGetRequest(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Content-Type", "application/xml");
        return req;
    }

    // Sends the request built by buildRequest, and — since a request message can only be sent once —
    // rebuilds and retries exactly once if the session had expired (401), re-authenticating first.
    private async Task<HttpResponseMessage> SendWithReauthAsync(Func<HttpRequestMessage> buildRequest)
    {
        await EnsureAuthenticatedAsync();
        using var req1 = buildRequest();
        var resp = await _http.SendAsync(req1);
        if (resp.StatusCode != HttpStatusCode.Unauthorized)
            return resp;

        resp.Dispose();
        _authenticated = false;
        await EnsureAuthenticatedAsync();
        using var req2 = buildRequest();
        return await _http.SendAsync(req2);
    }

    private async Task<string> GetAsync(string path)
    {
        var url = _baseUrl + path;
        var resp = await SendWithReauthAsync(() => BuildGetRequest(url));
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadAsStringAsync();
    }

    private async Task<string> PostAsync(string path, string xml)
    {
        var url = _baseUrl + path;
        var resp = await SendWithReauthAsync(() => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml")
        });
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadAsStringAsync();
    }

    public Task<string> GetTemplateAsync(string templateUrl) => GetAsync(templateUrl);

    public async Task<bool> ExistsByDocumentNumberAsync(string documentNumber)
    {
        var escaped = documentNumber.Replace("'", "''");
        var xml = await GetAsync($"/rest/TDdmMUSar/query?filter=PDOK.DOK_NR eq '{escaped}'&columns=PDOK.PK_DOK");
        return XDocument.Parse(xml).Descendants().Any(e => e.Name.LocalName == "row");
    }

    public async Task<string?> GetCustomerRestIdByCodeAsync(string billingCode)
    {
        var xml = await GetAsync($"/rest/TDdmKlSar/query?filter=K.KODS eq {billingCode}");
        return ExtractHref(xml, "PK_KLIENTS");
    }

    public async Task<string?> GetCountryRestIdByCodeAsync(string countryCode)
    {
        var xml = await GetAsync($"/rest/TdmSLDValsts/query?filter=DV.KODS eq {countryCode}");
        return ExtractHref(xml, "PK_VALSTS");
    }

    public async Task<string> SaveAsync(string templateUrl, string xml)
    {
        var result = await PostAsync(templateUrl, xml);
        var href = ExtractHref(result);
        if (string.IsNullOrEmpty(href) || !href.StartsWith("/rest/"))
            throw new InvalidOperationException(
                $"Horizon [{AccountName}] save to {templateUrl} returned unexpected response: {result[..Math.Min(500, result.Length)]}");
        return href;
    }

    // Finds the given element by local name (or searches the whole document when omitted) and
    // returns its nested <href> value — the shape every Horizon query/save response comes back in.
    private static string? ExtractHref(string xml, string? wrapperLocalName = null)
    {
        var doc = XDocument.Parse(xml);
        XContainer? scope = wrapperLocalName == null
            ? doc
            : doc.Descendants().FirstOrDefault(e => e.Name.LocalName == wrapperLocalName);
        return scope?.Descendants().FirstOrDefault(e => e.Name.LocalName == "href")?.Value;
    }

    public void Dispose()
    {
        _http.Dispose();
        _authLock.Dispose();
    }
}
