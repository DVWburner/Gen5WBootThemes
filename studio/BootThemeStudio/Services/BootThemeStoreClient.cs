using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BootThemeStudio.Models;

namespace BootThemeStudio.Services;

public sealed class StoreCatalogQueryResult
{
    public List<string> Categories { get; init; } = [];
    public List<StoreBootThemeListing> Items { get; init; } = [];
}

public sealed class BootThemeStoreException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string? ServerError { get; }
    public int? RetryAfterSeconds { get; }

    public BootThemeStoreException(
        HttpStatusCode statusCode,
        string message,
        string? serverError = null,
        int? retryAfterSeconds = null) : base(message)
    {
        StatusCode = statusCode;
        ServerError = serverError;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public string FormatFriendlyMessage()
    {
        if (RetryAfterSeconds is int secs && secs > 0)
        {
            string waitStr = secs >= 120
                ? $"~{Math.Ceiling(secs / 60.0)} minute(s)"
                : $"{secs} second(s)";
            return $"{Message}\nPlease try again in {waitStr}.";
        }
        return Message;
    }
}

/// <summary>
/// HTTP/JSON client for the v1.1 Boot Themes marketplace (`https://market.ioniqthemes.com/api/v1/boot-themes`)
/// and shared creator account authentication (`/api/v1/auth/*`).
/// </summary>
public static class BootThemeStoreClient
{
    public const string DefaultBaseUrl = StudioConstants.DefaultStoreUrl;
    private const int MaxPreviewDownloadBytes = 12 * 1024 * 1024;

    private static readonly HttpClient Http = CreateHttpClient(TimeSpan.FromSeconds(45));
    private static readonly HttpClient UploadHttp = CreateHttpClient(TimeSpan.FromMinutes(6));
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly object CategoriesLock = new();
    private static IReadOnlyList<string>? _lastServerCategories;

    public static IReadOnlyList<string> CachedOrDefaultCategories
    {
        get
        {
            lock (CategoriesLock)
            {
                if (_lastServerCategories is { Count: > 0 })
                    return _lastServerCategories;
            }
            return StudioConstants.DefaultCategories;
        }
    }

    private static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"BootThemeStudio/{StudioConstants.StudioVersion}");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public static bool ValidateUsername(string? username, out string error)
    {
        string u = username ?? "";
        if (u.Length < 3 || u.Length > 32)
        {
            error = "Username must be between 3 and 32 characters.";
            return false;
        }
        foreach (char c in u)
        {
            bool ok = (c >= 'a' && c <= 'z')
                      || (c >= 'A' && c <= 'Z')
                      || (c >= '0' && c <= '9')
                      || c == '_'
                      || c == '-';
            if (!ok)
            {
                error = "Username may contain only letters, digits, underscores (_), and hyphens (-).";
                return false;
            }
        }
        error = "";
        return true;
    }

    public static bool ValidatePassword(string? password, out string error)
    {
        string p = password ?? "";
        if (p.Length < 8 || p.Length > 128)
        {
            error = "Password must be between 8 and 128 characters.";
            return false;
        }
        if (p.Contains('\0'))
        {
            error = "Password cannot contain null characters.";
            return false;
        }
        error = "";
        return true;
    }

    public static async Task<StoreAuthResponse> RegisterAsync(
        string? baseUrl,
        string username,
        string password,
        CancellationToken ct = default)
    {
        string trimmedUser = (username ?? "").Trim();
        if (!ValidateUsername(trimmedUser, out string userErr))
            throw new BootThemeStoreException(HttpStatusCode.BadRequest, userErr);
        if (!ValidatePassword(password, out string passErr))
            throw new BootThemeStoreException(HttpStatusCode.BadRequest, passErr);

        return await PostAuthAsync(baseUrl, "api/v1/auth/register", trimmedUser, password, "Registration failed", ct)
            .ConfigureAwait(false);
    }

    public static async Task<StoreAuthResponse> LoginAsync(
        string? baseUrl,
        string username,
        string password,
        CancellationToken ct = default)
    {
        string trimmedUser = (username ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmedUser))
            throw new BootThemeStoreException(HttpStatusCode.BadRequest, "Username is required.");
        if (string.IsNullOrEmpty(password))
            throw new BootThemeStoreException(HttpStatusCode.BadRequest, "Password is required.");

        return await PostAuthAsync(baseUrl, "api/v1/auth/login", trimmedUser, password, "Sign in failed", ct)
            .ConfigureAwait(false);
    }

    private static async Task<StoreAuthResponse> PostAuthAsync(
        string? baseUrl,
        string relativePath,
        string username,
        string password,
        string errorPrefix,
        CancellationToken ct)
    {
        Uri root = NormalizeAndValidateBaseUri(baseUrl);
        Uri endpoint = new Uri(root, relativePath);
        string payload = JsonSerializer.Serialize(new StoreAuthRequest { Username = username, Password = password });

        using var req = CreateJsonRequest(HttpMethod.Post, endpoint.ToString(), bearerToken: null);
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);

        return await ReadApiResponseAsync(
            resp,
            (StoreAuthResponse? r) => r != null && r.Ok && !string.IsNullOrWhiteSpace(r.Token),
            r => r?.Error,
            r => r?.Message,
            errorPrefix,
            ct).ConfigureAwait(false);
    }

    public static async Task<StoreMeResponse> ValidateSessionAsync(
        string? baseUrl,
        string bearerToken,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
            throw new BootThemeStoreException(HttpStatusCode.Unauthorized, "No authentication token saved.");

        Uri root = NormalizeAndValidateBaseUri(baseUrl);
        Uri endpoint = new Uri(root, "api/v1/auth/me");

        using var req = CreateJsonRequest(HttpMethod.Get, endpoint.ToString(), bearerToken);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);

        return await ReadApiResponseAsync(
            resp,
            (StoreMeResponse? r) => r != null && r.Ok && r.Account != null,
            r => r?.Error,
            _ => null,
            "Session validation failed",
            ct).ConfigureAwait(false);
    }

    public static async Task<List<string>> RefreshCategoriesAsync(string? baseUrl, CancellationToken ct = default)
    {
        Uri root = NormalizeAndValidateBaseUri(baseUrl);
        try
        {
            var catRes = await FetchCatalogAsync(baseUrl, "newest", null, "all", null, null, ct).ConfigureAwait(false);
            if (catRes.Categories.Count > 0) return catRes.Categories;
        }
        catch
        {
            // Fall back to /api/v1/categories or /api/v1/themes if boot-themes isn't live yet
        }

        try
        {
            using var req = CreateJsonRequest(HttpMethod.Get, new Uri(root, "api/v1/categories").ToString(), null);
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (body.TrimStart().StartsWith("[", StringComparison.Ordinal))
                {
                    var arr = JsonSerializer.Deserialize<List<string>>(body, JsonOpts);
                    if (arr is { Count: > 0 })
                    {
                        UpdateCachedCategories(arr);
                        return [.. arr];
                    }
                }
            }
        }
        catch { }

        return [.. CachedOrDefaultCategories];
    }

    public static async Task<StoreCatalogQueryResult> FetchCatalogAsync(
        string? baseUrl,
        string sort = "newest",
        string? category = null,
        string part = "all",
        string? searchQuery = null,
        string? bearerToken = null,
        CancellationToken ct = default)
    {
        Uri root = NormalizeAndValidateBaseUri(baseUrl);
        string qs = BuildQueryString(sort, category, part, searchQuery);
        Uri endpoint = new Uri(root, "api/v1/boot-themes" + qs);

        using var req = CreateJsonRequest(HttpMethod.Get, endpoint.ToString(), bearerToken);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            string errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new BootThemeStoreException(
                resp.StatusCode,
                $"Store returned HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase}).",
                errBody);
        }

        string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<StoreCatalogResponse>(json, JsonOpts)
                     ?? new StoreCatalogResponse();

        var rawItems = parsed.Items ?? parsed.BootThemes ?? parsed.Data ?? [];
        foreach (var item in rawItems)
        {
            item.DownloadUrl = ResolveSafeUrl(item.DownloadUrl, root);
            item.OnBootPreviewUrl = ResolveSafeUrl(item.OnBootPreviewUrl, root);
            item.PowerOffPreviewUrl = ResolveSafeUrl(item.PowerOffPreviewUrl, root);
            item.PowerOffPosterUrl = ResolveSafeUrl(item.PowerOffPosterUrl, root);
        }

        var cats = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (parsed.Categories != null)
        {
            foreach (var c in parsed.Categories)
            {
                if (!string.IsNullOrWhiteSpace(c) && seen.Add(c.Trim()))
                    cats.Add(c.Trim());
            }
        }
        if (cats.Count > 0) UpdateCachedCategories(cats);
        else cats.AddRange(CachedOrDefaultCategories);

        return new StoreCatalogQueryResult
        {
            Categories = cats,
            Items = rawItems
        };
    }

    public static async Task<StoreUploadResponse> UploadBootThemeAsync(
        string? baseUrl,
        string zipPath,
        BootThemeManifest manifest,
        string bearerToken,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
            throw new BootThemeStoreException(HttpStatusCode.Unauthorized, "Please sign in to upload Boot Themes.");

        Uri root = NormalizeAndValidateBaseUri(baseUrl);
        Uri uploadEndpoint = new Uri(root, "api/v1/boot-themes");

        byte[] zipBytes = await File.ReadAllBytesAsync(zipPath, ct).ConfigureAwait(false);
        string metadataJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = false });
        string expectedName = $"{manifest.Id}_v{manifest.Version}_r{manifest.Revision}.zip";

        using var form = new MultipartFormDataContent();
        var pkgContent = new ByteArrayContent(zipBytes);
        pkgContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(pkgContent, "package", expectedName);

        var metaContent = new StringContent(metadataJson, Encoding.UTF8, "application/json");
        form.Add(metaContent, "metadata");

        using var req = new HttpRequestMessage(HttpMethod.Post, uploadEndpoint) { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());
        using var resp = await UploadHttp.SendAsync(req, ct).ConfigureAwait(false);

        return await ReadApiResponseAsync(
            resp,
            (StoreUploadResponse? r) => r == null || r.Ok || string.IsNullOrWhiteSpace(r.Error),
            r => r?.Error,
            r => r?.Message,
            "Upload rejected",
            ct,
            allowEmptySuccessBody: true,
            fallbackSuccessFactory: () => new StoreUploadResponse
            {
                Ok = true,
                Message = "Uploaded to Boot Themes Store successfully."
            }).ConfigureAwait(false);
    }

    public static async Task<StoreDeleteResponse> DeleteBootThemeAsync(
        string? baseUrl,
        string themeId,
        string bearerToken,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(themeId))
            throw new BootThemeStoreException(HttpStatusCode.BadRequest, "Boot Theme ID is required.");
        if (string.IsNullOrWhiteSpace(bearerToken))
            throw new BootThemeStoreException(HttpStatusCode.Unauthorized, "Please sign in to delete Boot Themes.");

        Uri root = NormalizeAndValidateBaseUri(baseUrl);
        Uri endpoint = new Uri(root, $"api/v1/boot-themes/{Uri.EscapeDataString(themeId.Trim())}");

        using var req = CreateJsonRequest(HttpMethod.Delete, endpoint.ToString(), bearerToken);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);

        return await ReadApiResponseAsync(
            resp,
            (StoreDeleteResponse? r) => r == null || r.Ok || string.IsNullOrWhiteSpace(r.Error),
            r => r?.Error,
            r => r?.Message,
            "Delete failed",
            ct,
            allowEmptySuccessBody: true,
            fallbackSuccessFactory: () => new StoreDeleteResponse
            {
                Ok = true,
                Message = "Boot Theme deleted successfully."
            }).ConfigureAwait(false);
    }

    public static Task<byte[]> DownloadPreviewAsync(string url, CancellationToken ct = default)
        => DownloadBytesAsync(url, MaxPreviewDownloadBytes, ct);

    public static async Task<byte[]> DownloadPackageVerifiedAsync(
        string url,
        string? expectedSha256,
        CancellationToken ct = default)
    {
        byte[] bytes = await DownloadBytesAsync(url, (int)StudioConstants.MaxTotalZipBytes, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Package SHA-256 mismatch (expected {expectedSha256}, got {actual}).");
        }
        return bytes;
    }

    public static async Task<byte[]> DownloadBytesAsync(string url, int maxBytes, CancellationToken ct = default)
    {
        if (!TryValidateRemoteUri(url, out Uri? uri) || uri == null)
            throw new InvalidOperationException("Blocked unsafe or non-HTTPS URL: " + url);

        using var resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength is long len && len > maxBytes)
            throw new InvalidOperationException($"Download exceeds safety limit ({maxBytes / (1024 * 1024)} MB).");

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        byte[] buf = new byte[65536];
        int read;
        while ((read = await stream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > maxBytes)
                throw new InvalidOperationException($"Downloaded payload exceeded {maxBytes / (1024 * 1024)} MB cap.");
            ms.Write(buf, 0, read);
        }
        return ms.ToArray();
    }

    private static void UpdateCachedCategories(IEnumerable<string> cats)
    {
        var clean = cats.Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Where(c => !c.Equals("all", StringComparison.OrdinalIgnoreCase)
                        && !c.Equals("All Categories", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (clean.Count > 0)
        {
            lock (CategoriesLock)
                _lastServerCategories = clean.AsReadOnly();
        }
    }

    private static string BuildQueryString(string? sort, string? category, string? part, string? searchQuery)
    {
        var qs = new List<string>
        {
            "sort=" + Uri.EscapeDataString(string.IsNullOrWhiteSpace(sort) ? "newest" : sort.Trim())
        };
        if (!string.IsNullOrWhiteSpace(category)
            && !category.Equals("All Categories", StringComparison.OrdinalIgnoreCase)
            && !category.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            qs.Add("category=" + Uri.EscapeDataString(category.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(part) && !part.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            qs.Add("part=" + Uri.EscapeDataString(part.Trim().ToLowerInvariant()));
        }
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            qs.Add("q=" + Uri.EscapeDataString(searchQuery.Trim()));
        }
        return "?" + string.Join("&", qs);
    }

    private static HttpRequestMessage CreateJsonRequest(HttpMethod method, string url, string? bearerToken)
    {
        var req = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(bearerToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());
        return req;
    }

    private static async Task<T> ReadApiResponseAsync<T>(
        HttpResponseMessage resp,
        Func<T?, bool> isOk,
        Func<T?, string?> getError,
        Func<T?, string?> getMessage,
        string errorPrefix,
        CancellationToken ct,
        bool allowEmptySuccessBody = false,
        Func<T>? fallbackSuccessFactory = null) where T : class
    {
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        T? parsed = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try { parsed = JsonSerializer.Deserialize<T>(body, JsonOpts); } catch { }
        }

        if (!resp.IsSuccessStatusCode || !isOk(parsed))
        {
            string? serverErr = getError(parsed);
            string detail = !string.IsNullOrWhiteSpace(serverErr)
                ? serverErr!
                : (!string.IsNullOrWhiteSpace(getMessage(parsed))
                    ? getMessage(parsed)!
                    : $"HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase})");

            string msg = string.Equals(detail, serverErr, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(serverErr)
                ? detail
                : $"{errorPrefix}: {detail}";

            throw new BootThemeStoreException(resp.StatusCode, msg, serverErr, ParseRetryAfterSeconds(resp));
        }

        if (parsed != null) return parsed;
        if (allowEmptySuccessBody && fallbackSuccessFactory != null) return fallbackSuccessFactory();
        throw new BootThemeStoreException(resp.StatusCode, $"{errorPrefix}: Empty response from server.");
    }

    private static int? ParseRetryAfterSeconds(HttpResponseMessage resp)
    {
        if (resp.Headers.RetryAfter?.Delta is TimeSpan delta && delta.TotalSeconds > 0)
            return (int)Math.Ceiling(delta.TotalSeconds);
        if (resp.Headers.RetryAfter?.Date is DateTimeOffset date)
        {
            double secs = (date - DateTimeOffset.UtcNow).TotalSeconds;
            if (secs > 0) return (int)Math.Ceiling(secs);
        }
        if (resp.Headers.TryGetValues("Retry-After", out var vals))
        {
            string? raw = vals.FirstOrDefault()?.Trim();
            if (int.TryParse(raw, out int s) && s > 0) return s;
        }
        return null;
    }

    public static Uri NormalizeAndValidateBaseUri(string? baseUrl)
    {
        string raw = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim();
        if (!raw.EndsWith("/", StringComparison.Ordinal)) raw += "/";
        if (!TryValidateRemoteUri(raw, out Uri? uri) || uri == null)
            throw new InvalidOperationException("Marketplace URL must use HTTPS (e.g. https://market.ioniqthemes.com/).");
        return uri;
    }

    private static string? ResolveSafeUrl(string? raw, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string trimmed = raw.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? abs))
            return TryValidateRemoteUri(abs.ToString(), out _) ? abs.ToString() : null;
        if (Uri.TryCreate(baseUri, trimmed, out Uri? rel))
            return TryValidateRemoteUri(rel.ToString(), out _) ? rel.ToString() : null;
        return null;
    }

    private static bool TryValidateRemoteUri(string? raw, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            uri = parsed;
            return true;
        }
        if (parsed.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && parsed.IsLoopback)
        {
            uri = parsed;
            return true;
        }
        return false;
    }
}
