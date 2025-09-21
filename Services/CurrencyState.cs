using System.Globalization;
using System.Net.Http.Json;
#nullable enable

public sealed class CurrencyState
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IHttpContextAccessor _httpContext;

    public string CultureName { get; private set; } = "es-CO"; // fallback
    public string CurrencyCode { get; private set; } = "COP";  // fallback
    public string CurrencySymbol { get; private set; } = "$";  // fallback
    public decimal UsdToLocal { get; private set; } = 1m;
    public bool IsReady { get; private set; }

    // de-dupe concurrent loads
    private Task? _loadTask;
    private static readonly TimeSpan RateTtl = TimeSpan.FromHours(12);
    private DateTimeOffset _lastRateAt = DateTimeOffset.MinValue;

    public CurrencyState(IHttpClientFactory httpFactory, IHttpContextAccessor httpContext)
    {
        _httpFactory = httpFactory;
        _httpContext = httpContext;
    }

    public Task EnsureLoadedAsync()
    {
        if (IsReady) return Task.CompletedTask;
        lock (this)
        {
            _loadTask ??= LoadAsync();
            return _loadTask;
        }
    }

    private async Task LoadAsync()
    {
        var q = _httpContext.HttpContext?.Request?.Query;

        var ccOverride      = q?["cc"].ToString();        // country code: BR, ES, DE, CN, US, CO...
        var currencyOverride= q?["currency"].ToString();  // ISO currency: BRL, EUR, CNY, JPY...
        var cultureOverride = q?["culture"].ToString();   // culture: pt-BR, es-ES, de-DE, zh-CN...

        if (!string.IsNullOrWhiteSpace(cultureOverride))
        {
            CultureName = cultureOverride!;
        }
        else if (!string.IsNullOrWhiteSpace(ccOverride))
        {
            CultureName = CultureForCountry(ccOverride!.ToUpperInvariant());
        }

        if (!string.IsNullOrWhiteSpace(currencyOverride))
        {
            CurrencyCode = currencyOverride!.ToUpperInvariant();
            try { CurrencySymbol = new RegionInfo(CultureName).CurrencySymbol; } catch { /* keep fallback */ }
            // pull rate and finish; skip IP call
            await RefreshRateAsync(force: true);
            IsReady = true;
            return;
        }
        try
        {
            // 1) Try IP API (may 429)
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(3);

            try
            {
                var geo = await http.GetFromJsonAsync<IpInfo>("https://ipapi.co/json/");
                var country = (geo?.country ?? "").ToUpperInvariant();
                var curr = (geo?.currency ?? "").ToUpperInvariant();

                if (!string.IsNullOrWhiteSpace(country))
                    CultureName = CultureForCountry(country);

                if (!string.IsNullOrWhiteSpace(curr))
                    CurrencyCode = curr;

                CurrencySymbol = new RegionInfo(CultureName).CurrencySymbol;
            }
            catch
            {
                // 2) Fallback to Accept-Language header
                var accept = _httpContext.HttpContext?.Request?.Headers["Accept-Language"].ToString();
                var lang = ParseFirstCulture(accept) ?? "es-CO";
                CultureName = lang;
                var ri = new RegionInfo(CultureName);
                CurrencyCode = ri.ISOCurrencySymbol;
                CurrencySymbol = ri.CurrencySymbol;
            }

            // 3) Get USD→local rate (only if not USD)
            if (!CurrencyCode.Equals("USD", StringComparison.OrdinalIgnoreCase))
                await RefreshRateAsync(force: true);
        }
        finally
        {
            IsReady = true; // never block the UI
        }
    }

    public async Task RefreshRateAsync(bool force = false)
    {
        if (!force && DateTimeOffset.UtcNow - _lastRateAt < RateTtl) return;

        try
        {
            var http = _httpFactory.CreateClient();
            var resp = await http.GetFromJsonAsync<RateResp>(
                $"https://api.exchangerate.host/latest?base=USD&symbols={CurrencyCode}");
            if (resp?.rates != null && resp.rates.TryGetValue(CurrencyCode, out var r) && r > 0m)
            {
                UsdToLocal = r;
                _lastRateAt = DateTimeOffset.UtcNow;
            }
        }
        catch
        {
            // keep previous (or 1m fallback)
        }
    }

    public string FormatFromUsd(decimal usd)
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo(CultureName).Clone();
        culture.NumberFormat.CurrencySymbol = CurrencySymbol; // make symbol consistent
        return (usd * UsdToLocal).ToString("C", culture);
    }

    private static string? ParseFirstCulture(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        // e.g. "de-DE,de;q=0.9,en;q=0.8"
        var first = header.Split(',').FirstOrDefault();
        return string.IsNullOrWhiteSpace(first) ? null : first.Split(';')[0].Trim();
    }

    private static string CultureForCountry(string country) => country switch
    {
        "BR" => "pt-BR",
        "ES" => "es-ES",
        "DE" => "de-DE",
        "CN" => "zh-CN",
        "JP" => "ja-JP",
        "US" => "en-US",
        "CO" => "es-CO",
        _ => "en-US"
    };

    private sealed class IpInfo { public string? country { get; set; } public string? currency { get; set; } }
    private sealed class RateResp { public Dictionary<string, decimal>? rates { get; set; } }
}

#nullable restore
