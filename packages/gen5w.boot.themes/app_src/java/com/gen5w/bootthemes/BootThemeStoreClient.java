package com.gen5w.bootthemes;

import org.json.JSONArray;
import org.json.JSONObject;
import org.json.JSONTokener;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URI;
import java.net.URL;
import java.net.URLEncoder;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;

/**
 * Pure Java HTTP/HTTPS store client for https://market.ioniqthemes.com/api/v1/boot-themes
 * compatible with Android 4.4.2 KitKat.
 */
public final class BootThemeStoreClient {
    public static final String DEFAULT_BASE_URL = "https://market.ioniqthemes.com";
    public static final int MAX_CATALOG_BYTES = 4 * 1024 * 1024;
    public static final int MAX_PREVIEW_BYTES = 12 * 1024 * 1024;
    public static final long MAX_PACKAGE_BYTES = 65L * 1024L * 1024L;
    private static final int CONNECT_TIMEOUT_MS = 12000;
    private static final int READ_TIMEOUT_MS = 25000;
    private static final int MAX_REDIRECTS = 4;

    public interface ProgressListener {
        void onProgress(long bytesRead, long totalBytes);
    }

    public interface CancelFlag {
        boolean isCancelled();
    }

    private final String baseUrl;
    private final boolean enforceHttps;

    public BootThemeStoreClient() {
        this(DEFAULT_BASE_URL);
    }

    public BootThemeStoreClient(String baseUrl) {
        String clean = (baseUrl == null || baseUrl.trim().isEmpty())
                ? DEFAULT_BASE_URL
                : baseUrl.trim().replaceAll("/+$", "");
        this.baseUrl = clean;
        this.enforceHttps = !isLoopbackHttp(clean);
    }

    public BootThemeItem.CatalogResult fetchCatalog(String sort, String category, String partFilter, String query) {
        try {
            String qs = buildQueryString(sort, category, partFilter, query);
            HttpURLConnection conn = openConnectionWithRedirects(baseUrl + "/api/v1/boot-themes" + qs);
            int code = conn.getResponseCode();
            if (code < 200 || code >= 300) {
                String errBody = readBoundedText(conn.getErrorStream(), 2048);
                conn.disconnect();
                return new BootThemeItem.CatalogResult(
                        false,
                        "Store HTTP " + code + (errBody.isEmpty() ? "" : ": " + errBody),
                        null,
                        null);
            }
            String jsonText;
            try {
                jsonText = readBoundedText(conn.getInputStream(), MAX_CATALOG_BYTES);
            } finally {
                conn.disconnect();
            }
            return parseCatalogResponse(jsonText);
        } catch (Throwable t) {
            String msg = t.getMessage();
            if (msg == null || msg.trim().isEmpty()) {
                msg = t.getClass().getSimpleName();
            }
            return new BootThemeItem.CatalogResult(false, msg, null, null);
        }
    }

    private BootThemeItem.CatalogResult parseCatalogResponse(String jsonText) throws Exception {
        if (jsonText == null || jsonText.trim().isEmpty()) {
            return new BootThemeItem.CatalogResult(false, "Empty response from store.", null, null);
        }
        Object parsed = new JSONTokener(jsonText).nextValue();
        JSONObject rootObj = parsed instanceof JSONObject ? (JSONObject) parsed : null;
        JSONArray arr = null;
        List<String> cats = new ArrayList<String>();

        if (rootObj != null) {
            JSONArray catJson = rootObj.optJSONArray("categories");
            if (catJson != null) {
                Set<String> seen = new LinkedHashSet<String>();
                for (int i = 0; i < catJson.length(); i++) {
                    String c = catJson.optString(i, "").trim();
                    if (!c.isEmpty() && seen.add(c)) {
                        cats.add(c);
                    }
                }
            }
            if (rootObj.has("items")) arr = rootObj.optJSONArray("items");
            else if (rootObj.has("themes")) arr = rootObj.optJSONArray("themes");
        } else if (parsed instanceof JSONArray) {
            arr = (JSONArray) parsed;
        }

        List<BootThemeItem> items = new ArrayList<BootThemeItem>();
        if (arr != null) {
            for (int i = 0; i < arr.length(); i++) {
                JSONObject o = arr.optJSONObject(i);
                if (o == null) continue;
                BootThemeItem item = parseItemJson(o);
                if (item != null && !item.id.isEmpty()) {
                    items.add(item);
                }
            }
        }
        return new BootThemeItem.CatalogResult(true, "", cats, items);
    }

    public BootThemeItem parseItemJson(JSONObject o) {
        BootThemeItem it = new BootThemeItem();
        it.id = o.optString("id", "").trim();
        it.name = o.optString("name", it.id).trim();
        it.author = o.optString("author", "Unknown").trim();
        it.version = o.optString("version", "1.0.0").trim();
        it.revision = o.optInt("revision", 1);
        it.category = o.optString("category", "Other").trim();
        it.description = o.optString("description", "").trim();
        it.hasOnBoot = o.optBoolean("hasOnBoot", false);
        it.hasPowerOff = o.optBoolean("hasPowerOff", false);
        it.hasWelcomeVideo = o.optBoolean("hasWelcomeVideo", false);
        it.powerOffDurationMs = o.optInt("powerOffDurationMs", 0);
        it.resolution = o.optString("resolution", "1920x720").trim();

        it.onBootPreviewUrl = resolveStoreUrl(optCleanString(o, "onBootPreviewUrl"));
        it.onBootPreviewType = optCleanString(o, "onBootPreviewType");
        it.powerOffPreviewUrl = resolveStoreUrl(optCleanString(o, "powerOffPreviewUrl"));
        it.powerOffPreviewType = optCleanString(o, "powerOffPreviewType");
        it.powerOffPosterUrl = resolveStoreUrl(optCleanString(o, "powerOffPosterUrl"));
        it.powerOffPosterType = optCleanString(o, "powerOffPosterType");
        it.downloadUrl = resolveStoreUrl(optCleanString(o, "downloadUrl"));

        it.fileSizeBytes = o.optLong("fileSizeBytes", 0L);
        it.sha256 = optCleanString(o, "sha256").toLowerCase(Locale.US);
        it.downloads = o.optLong("downloads", 0L);

        JSONArray tagsArr = o.optJSONArray("tags");
        if (tagsArr != null) {
            for (int i = 0; i < tagsArr.length(); i++) {
                String t = tagsArr.optString(i, "").trim();
                if (!t.isEmpty()) it.tags.add(t);
            }
        }
        return it;
    }

    private static String optCleanString(JSONObject o, String key) {
        if (o == null || o.isNull(key)) return "";
        String v = o.optString(key, "");
        return "null".equalsIgnoreCase(v) ? "" : v.trim();
    }

    public byte[] downloadPreviewBytes(String url, int maxBytes) throws IOException {
        String abs = resolveStoreUrl(url);
        if (abs.isEmpty()) return null;
        int cap = maxBytes > 0 ? maxBytes : MAX_PREVIEW_BYTES;
        HttpURLConnection conn = openConnectionWithRedirects(abs);
        try {
            int code = conn.getResponseCode();
            if (code < 200 || code >= 300) {
                throw new IOException("Preview HTTP " + code);
            }
            return readBoundedBytes(conn.getInputStream(), cap);
        } finally {
            conn.disconnect();
        }
    }

    public void downloadFileStreamed(
            String url,
            File destFile,
            String expectedSha256,
            long maxBytes,
            ProgressListener listener,
            CancelFlag cancel) throws Exception {
        String abs = resolveStoreUrl(url);
        if (abs.isEmpty()) {
            throw new IllegalArgumentException("Missing download URL.");
        }
        File parent = destFile.getParentFile();
        if (parent != null && !parent.exists()) {
            parent.mkdirs();
        }
        File partFile = new File(destFile.getAbsolutePath() + ".part");
        if (partFile.exists()) partFile.delete();

        HttpURLConnection conn = openConnectionWithRedirects(abs);
        InputStream in = null;
        FileOutputStream fos = null;
        boolean ok = false;
        try {
            int code = conn.getResponseCode();
            if (code < 200 || code >= 300) {
                throw new IOException("Download failed with HTTP " + code);
            }
            long contentLen = conn.getContentLength();
            if (contentLen > maxBytes) {
                throw new IOException("File exceeds maximum allowed size.");
            }
            MessageDigest md = MessageDigest.getInstance("SHA-256");
            in = conn.getInputStream();
            fos = new FileOutputStream(partFile);
            byte[] buf = new byte[32768];
            long totalRead = 0L;
            int r;
            while ((r = in.read(buf)) != -1) {
                if (cancel != null && cancel.isCancelled()) {
                    throw new IOException("Download cancelled.");
                }
                totalRead += r;
                if (totalRead > maxBytes) {
                    throw new IOException("Download exceeded size limit.");
                }
                md.update(buf, 0, r);
                fos.write(buf, 0, r);
                if (listener != null) {
                    listener.onProgress(totalRead, contentLen);
                }
            }
            fos.flush();
            fos.close();
            fos = null;

            if (expectedSha256 != null && !expectedSha256.trim().isEmpty()) {
                String actualHex = toHexLower(md.digest());
                if (!actualHex.equalsIgnoreCase(expectedSha256.trim())) {
                    throw new IOException("SHA-256 mismatch on downloaded package.");
                }
            }
            if (destFile.exists()) destFile.delete();
            if (!partFile.renameTo(destFile)) {
                throw new IOException("Failed saving downloaded file.");
            }
            destFile.setReadable(true, false);
            ok = true;
        } finally {
            if (in != null) try { in.close(); } catch (Exception ignored) {}
            if (fos != null) try { fos.close(); } catch (Exception ignored) {}
            conn.disconnect();
            if (!ok && partFile.exists()) {
                partFile.delete();
            }
        }
    }

    private String buildQueryString(String sort, String category, String partFilter, String query) throws Exception {
        StringBuilder sb = new StringBuilder();
        sb.append("?sort=").append(URLEncoder.encode(
                (sort != null && !sort.trim().isEmpty()) ? sort.trim() : "newest", "UTF-8"));
        if (category != null && !category.trim().isEmpty()
                && !"All".equalsIgnoreCase(category.trim())
                && !"All Categories".equalsIgnoreCase(category.trim())) {
            sb.append("&category=").append(URLEncoder.encode(category.trim(), "UTF-8"));
        }
        if (partFilter != null && !partFilter.trim().isEmpty() && !"all".equalsIgnoreCase(partFilter.trim())) {
            sb.append("&part=").append(URLEncoder.encode(partFilter.trim(), "UTF-8"));
        }
        if (query != null && !query.trim().isEmpty()) {
            sb.append("&q=").append(URLEncoder.encode(query.trim(), "UTF-8"));
        }
        return sb.toString();
    }

    public String resolveStoreUrl(String urlOrPath) {
        if (urlOrPath == null || urlOrPath.trim().isEmpty()) return "";
        String u = urlOrPath.trim();
        if (u.startsWith("https://") || u.startsWith("http://")) {
            validateUrlAllowed(u, enforceHttps);
            return u;
        }
        if (!u.startsWith("/")) u = "/" + u;
        String resolved = baseUrl + u;
        validateUrlAllowed(resolved, enforceHttps);
        return resolved;
    }

    private HttpURLConnection openConnectionWithRedirects(String initialUrl) throws IOException {
        String current = initialUrl;
        for (int hop = 0; hop <= MAX_REDIRECTS; hop++) {
            validateUrlAllowed(current, enforceHttps);
            URL u = new URL(current);
            HttpURLConnection conn = (HttpURLConnection) u.openConnection();
            StoreTlsHelper.configure(conn);
            conn.setInstanceFollowRedirects(false);
            conn.setConnectTimeout(CONNECT_TIMEOUT_MS);
            conn.setReadTimeout(READ_TIMEOUT_MS);
            conn.setRequestProperty("User-Agent", "Gen5WBootThemes/1.0.0 (Android 4.4.2; Gen5W)");
            conn.setRequestProperty("Accept", "application/json, image/*, video/mp4, application/zip, */*");

            int code = conn.getResponseCode();
            if (code == 301 || code == 302 || code == 303 || code == 307 || code == 308) {
                String loc = conn.getHeaderField("Location");
                conn.disconnect();
                if (loc == null || loc.trim().isEmpty()) {
                    throw new IOException("Redirect missing Location header");
                }
                URL nextUrl = new URL(u, loc.trim());
                current = nextUrl.toString();
                continue;
            }
            return conn;
        }
        throw new IOException("Too many HTTP redirects");
    }

    private static void validateUrlAllowed(String rawUrl, boolean requireHttps) {
        URI uri;
        try {
            uri = URI.create(rawUrl);
        } catch (Exception e) {
            throw new IllegalArgumentException("Invalid URL: " + rawUrl);
        }
        String scheme = uri.getScheme() == null ? "" : uri.getScheme().toLowerCase(Locale.US);
        if (requireHttps) {
            if (!"https".equals(scheme)) {
                throw new SecurityException("Store requires HTTPS: " + rawUrl);
            }
        } else if (!"https".equals(scheme) && !"http".equals(scheme)) {
            throw new SecurityException("Unsupported URL scheme: " + scheme);
        }
    }

    private static boolean isLoopbackHttp(String rawUrl) {
        try {
            URI u = URI.create(rawUrl);
            String scheme = u.getScheme() == null ? "" : u.getScheme().toLowerCase(Locale.US);
            String host = u.getHost() == null ? "" : u.getHost().toLowerCase(Locale.US);
            return "http".equals(scheme) && ("localhost".equals(host) || "127.0.0.1".equals(host));
        } catch (Exception e) {
            return false;
        }
    }

    private static String readBoundedText(InputStream in, int maxBytes) throws IOException {
        if (in == null) return "";
        byte[] raw = readBoundedBytes(in, maxBytes);
        return new String(raw, "UTF-8");
    }

    private static byte[] readBoundedBytes(InputStream in, int maxBytes) throws IOException {
        ByteArrayOutputStream bos = new ByteArrayOutputStream();
        byte[] buf = new byte[16384];
        int total = 0;
        int r;
        try {
            while ((r = in.read(buf)) != -1) {
                total += r;
                if (total > maxBytes) {
                    throw new IOException("Response exceeded size limit (" + maxBytes + " bytes)");
                }
                bos.write(buf, 0, r);
            }
        } finally {
            try { in.close(); } catch (Exception ignored) {}
        }
        return bos.toByteArray();
    }

    private static String toHexLower(byte[] bytes) {
        char[] hexChars = "0123456789abcdef".toCharArray();
        char[] out = new char[bytes.length * 2];
        for (int i = 0; i < bytes.length; i++) {
            int v = bytes[i] & 0xFF;
            out[i * 2] = hexChars[v >>> 4];
            out[i * 2 + 1] = hexChars[v & 0x0F];
        }
        return new String(out);
    }
}
