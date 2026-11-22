using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;

namespace BootThemeStudio.Models;

public static class StudioConstants
{
    public const string StudioVersion = "1.0.0";
    public const string DefaultStoreUrl = "https://market.ioniqthemes.com/";
    public const long MaxTotalZipBytes = 60L * 1024L * 1024L;
    public const long MaxSingleEntryBytes = 40L * 1024L * 1024L;
    public const long MaxPayloadMp4Bytes = 35L * 1024L * 1024L;
    public const long MaxPowerOffPreviewBytes = 10L * 1024L * 1024L;
    public const long MaxSplashBytes = 5L * 1024L * 1024L;
    public const long MaxJpuSafeBytes = 450L * 1024L;
    public const long MaxStaticPreviewBytes = 2L * 1024L * 1024L;
    public const int MaxNonOsFiles = 20;

    public static readonly string[] DefaultCategories =
    [
        "OEM+",
        "Cars",
        "Fantasy / Art",
        "Games / Comics",
        "TV / Movie / Music",
        "Anime",
        "Nature",
        "Animals",
        "Sports",
        "Utility",
        "Other"
    ];
}

/// <summary>
/// Root `boottheme.json` inside `<id>_v<version>_r<revision>.zip` AND `metadata` JSON part on upload.
/// </summary>
public sealed class BootThemeManifest
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; } = 1;

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    [JsonPropertyName("revision")]
    public int Revision { get; set; } = 1;

    [JsonPropertyName("category")]
    public string Category { get; set; } = "OEM+";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("hasOnBoot")]
    public bool HasOnBoot { get; set; } = true;

    [JsonPropertyName("hasPowerOff")]
    public bool HasPowerOff { get; set; } = true;

    [JsonPropertyName("hasWelcomeVideo")]
    public bool HasWelcomeVideo { get; set; }

    [JsonPropertyName("powerOffDurationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PowerOffDurationMs { get; set; }

    [JsonPropertyName("resolution")]
    public string Resolution { get; set; } = "1920x720";

    [JsonPropertyName("minAppVersion")]
    public string MinAppVersion { get; set; } = "1.0.0";

    [JsonPropertyName("compatibleVersions")]
    public List<string> CompatibleVersions { get; set; } = ["1.0.0+"];

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];
}

public sealed class StoreBootThemeListing
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    [JsonPropertyName("revision")]
    public int Revision { get; set; } = 1;

    [JsonPropertyName("category")]
    public string Category { get; set; } = "Other";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("hasOnBoot")]
    public bool HasOnBoot { get; set; }

    [JsonPropertyName("hasPowerOff")]
    public bool HasPowerOff { get; set; }

    [JsonPropertyName("hasWelcomeVideo")]
    public bool HasWelcomeVideo { get; set; }

    [JsonPropertyName("powerOffDurationMs")]
    public int? PowerOffDurationMs { get; set; }

    [JsonPropertyName("onBootPreviewUrl")]
    public string? OnBootPreviewUrl { get; set; }

    [JsonPropertyName("onBootPreviewType")]
    public string? OnBootPreviewType { get; set; }

    [JsonPropertyName("powerOffPreviewUrl")]
    public string? PowerOffPreviewUrl { get; set; }

    [JsonPropertyName("powerOffPreviewType")]
    public string? PowerOffPreviewType { get; set; }

    [JsonPropertyName("powerOffPosterUrl")]
    public string? PowerOffPosterUrl { get; set; }

    [JsonPropertyName("powerOffPosterType")]
    public string? PowerOffPosterType { get; set; }

    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }

    [JsonPropertyName("minAppVersion")]
    public string? MinAppVersion { get; set; }

    [JsonPropertyName("compatibleVersions")]
    public List<string>? CompatibleVersions { get; set; }

    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; } = 1;

    [JsonPropertyName("createdAt")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public string? UpdatedAt { get; set; }

    [JsonPropertyName("downloadUrl")]
    public string? DownloadUrl { get; set; }

    [JsonPropertyName("fileSizeBytes")]
    public long? FileSizeBytes { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("downloads")]
    public int? Downloads { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("isOwner")]
    public bool? IsOwner { get; set; }

    [JsonIgnore]
    public bool IsOwnedByCurrentUser => IsOwner == true;

    [JsonIgnore]
    public string PartsBadge
    {
        get
        {
            if (HasOnBoot && HasPowerOff)
                return HasWelcomeVideo ? "Boot + Welcome + Goodbye" : "Boot Image + Goodbye Video";
            if (HasOnBoot) return "Boot Image Only";
            if (HasPowerOff) return "Goodbye Video Only";
            return "Empty";
        }
    }

    [JsonIgnore]
    public string FriendlySizeLabel
    {
        get
        {
            long b = FileSizeBytes ?? 0;
            if (b <= 0) return "";
            if (b < 1024) return $"{b} B";
            if (b < 1024 * 1024) return $"{b / 1024.0:0.#} KB";
            return $"{b / (1024.0 * 1024.0):0.##} MB";
        }
    }

    [JsonIgnore]
    public string FriendlyMetaLine =>
        $"by {Author}  •  v{Version} (r{Revision})  •  {Category}"
        + (Downloads is int d ? $"  •  {d} downloads" : "")
        + (!string.IsNullOrEmpty(FriendlySizeLabel) ? $"  •  {FriendlySizeLabel}" : "");

    public DateTimeOffset ResolveSortDate()
    {
        if (DateTimeOffset.TryParse(UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var upd))
            return upd;
        if (DateTimeOffset.TryParse(CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var cre))
            return cre;
        return DateTimeOffset.MinValue;
    }
}

public sealed class StoreCatalogResponse
{
    [JsonPropertyName("apiVersion")]
    public int ApiVersion { get; set; } = 1;

    [JsonPropertyName("generatedAt")]
    public string? GeneratedAt { get; set; }

    [JsonPropertyName("categories")]
    public List<string>? Categories { get; set; }

    [JsonPropertyName("total")]
    public int? Total { get; set; }

    [JsonPropertyName("items")]
    public List<StoreBootThemeListing>? Items { get; set; }

    [JsonPropertyName("bootThemes")]
    public List<StoreBootThemeListing>? BootThemes { get; set; }

    [JsonPropertyName("data")]
    public List<StoreBootThemeListing>? Data { get; set; }
}

public sealed class StoreUploadResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("item")]
    public StoreBootThemeListing? Item { get; set; }
}

public sealed class StoreDeleteResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class StoreAccountInfo
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public string? CreatedAt { get; set; }
}

public sealed class StoreAuthRequest
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";
}

public sealed class StoreAuthResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("tokenType")]
    public string? TokenType { get; set; }

    [JsonPropertyName("expiresAt")]
    public string? ExpiresAt { get; set; }

    [JsonPropertyName("account")]
    public StoreAccountInfo? Account { get; set; }
}

public sealed class StoreMeResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("account")]
    public StoreAccountInfo? Account { get; set; }
}

/// <summary>
/// Matches Gen5WStudio's exact DPAPI-serialized session object so accounts are shared automatically!
/// </summary>
public sealed class StoredStoreSession
{
    [JsonPropertyName("baseOrigin")]
    public string BaseOrigin { get; set; } = "";

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("expiresAt")]
    public string? ExpiresAt { get; set; }

    public bool IsExpired(DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(ExpiresAt)) return false;
        if (!DateTimeOffset.TryParse(
                ExpiresAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var exp))
        {
            return false;
        }
        return exp <= (now ?? DateTimeOffset.UtcNow).AddMinutes(1);
    }
}

public sealed class PackageValidationResult
{
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    public long TotalUncompressedBytes { get; set; }
    public int FileCount { get; set; }
    public bool IsValid => Errors.Count == 0;
}
