package com.gen5w.bootthemes;

import java.io.File;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

/**
 * Shared metadata model representing either an installed local Boot Theme
 * or a remote Store listing from https://market.ioniqthemes.com/api/v1/boot-themes.
 */
public final class BootThemeItem {
    public String id = "";
    public String name = "";
    public String author = "";
    public String version = "1.0.0";
    public int revision = 1;
    public String category = "Other";
    public String description = "";
    public boolean hasOnBoot = false;
    public boolean hasPowerOff = false;
    public boolean hasWelcomeVideo = false;
    public int powerOffDurationMs = 0;
    public String resolution = "1920x720";

    // Store URLs & download info
    public String onBootPreviewUrl = "";
    public String onBootPreviewType = "";
    public String powerOffPreviewUrl = "";
    public String powerOffPreviewType = "";
    public String powerOffPosterUrl = "";
    public String powerOffPosterType = "";
    public String downloadUrl = "";
    public long fileSizeBytes = 0L;
    public String sha256 = "";
    public long downloads = 0L;
    public final List<String> tags = new ArrayList<String>();

    // Local filesystem paths when installed in /data/nemesis/boot_themes/themes/<id>/
    public File localDir;
    public File localOnBootPreview;
    public File localPowerOffPreview; // .gif or .mp4
    public File localPowerOffPoster;
    public File localSplash1920;
    public File localSplash1280;
    public File localGoodbyeMp4;
    public File localWelcomeMp4;

    public String getPartsBadge() {
        if (hasOnBoot && hasPowerOff) {
            return "BOOT IMAGE + GOODBYE VIDEO";
        }
        if (hasOnBoot) {
            return "BOOT IMAGE ONLY";
        }
        if (hasPowerOff) {
            return "GOODBYE VIDEO ONLY";
        }
        return "EMPTY";
    }

    public String getDurationLabel() {
        if (powerOffDurationMs <= 0) return "";
        float sec = powerOffDurationMs / 1000.0f;
        return String.format("%.1fs", sec);
    }

    public static final class CatalogResult {
        public final boolean ok;
        public final String errorMessage;
        public final List<String> categories;
        public final List<BootThemeItem> items;

        public CatalogResult(boolean ok, String errorMessage, List<String> categories, List<BootThemeItem> items) {
            this.ok = ok;
            this.errorMessage = errorMessage == null ? "" : errorMessage;
            this.categories = categories == null ? Collections.<String>emptyList() : categories;
            this.items = items == null ? Collections.<BootThemeItem>emptyList() : items;
        }
    }
}
