package com.gen5w.bootthemes;

import android.app.Activity;
import android.app.AlertDialog;
import android.app.Dialog;
import android.content.DialogInterface;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.media.MediaPlayer;
import android.net.Uri;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.TextUtils;
import android.util.LruCache;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.view.inputmethod.EditorInfo;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.HorizontalScrollView;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.VideoView;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.InputStream;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * In-car Gen5W Boot Themes Manager & Online Store (`com.gen5w.bootthemes`).
 * Designed specifically for the 1920x720 landscape head unit running Android 4.4.2 KitKat.
 */
public class MainActivity extends Activity {
    private static final String NEMESIS_BIN = "/system/xbin/nemesis";
    private static final String HELPER_SVC = "gen5w.boot.themes/bootthemes-helper";
    private static final String[] HELPER_CANDIDATES = new String[] {
        "/data/nemesis/apps/gen5w.boot.themes/svc/bootthemes-helper.sh",
        "/data/nemesis/packages/gen5w.boot.themes/svc/bootthemes-helper.sh",
        "/data/nemesis/pkgs/gen5w.boot.themes/svc/bootthemes-helper.sh"
    };

    private static final int COL_BG = 0xFF0B1120;
    private static final int COL_PANEL = 0xFF131D33;
    private static final int COL_CARD = 0xFF18243E;
    private static final int COL_BORDER = 0xFF2B3B5E;
    private static final int COL_ACCENT = 0xFF38BDF8;
    private static final int COL_GREEN = 0xFF10B981;
    private static final int COL_AMBER = 0xFFF59E0B;
    private static final int COL_RED = 0xFFEF4444;
    private static final int COL_TEXT = 0xFFF8FAFC;
    private static final int COL_MUTED = 0xFF94A3B8;

    private final Handler ui = new Handler(Looper.getMainLooper());
    private final ExecutorService thumbPool = Executors.newFixedThreadPool(3);
    private final LocalThemeRepository repo = new LocalThemeRepository();
    private final BootThemeStoreClient storeClient = new BootThemeStoreClient();
    private final LruCache<String, Bitmap> bmpCache = new LruCache<String, Bitmap>(48);

    private boolean showingStoreTab = false;
    private volatile boolean isBusy = false;

    // Store filter state
    private String storeSort = "newest";
    private String storeCategory = "All";
    private String storePart = "all";
    private String storeQuery = "";
    private List<String> storeCategories = new ArrayList<String>();
    private List<BootThemeItem> lastStoreItems = new ArrayList<BootThemeItem>();
    private Map<String, BootThemeItem> installedMap = new HashMap<String, BootThemeItem>();

    // Views
    private Button tabInstalledBtn;
    private Button tabStoreBtn;
    private TextView activeSummaryText;
    private TextView statusBannerText;
    private LinearLayout storeFilterBar;
    private Button categoryDropdownBtn;
    private Button sortToggleBtn;
    private Button partToggleBtn;
    private EditText searchBox;
    private ScrollView contentScroll;
    private LinearLayout gridContainer;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        clearPreviewTempCache();
        buildUi();
        bootstrapInitialImportAndRefresh();
    }

    @Override
    protected void onStop() {
        super.onStop();
        clearPreviewTempCache();
    }

    @Override
    protected void onDestroy() {
        clearPreviewTempCache();
        super.onDestroy();
        thumbPool.shutdownNow();
    }

    private void clearPreviewTempCache() {
        try {
            File cacheDir = new File(repo.getBaseDir(), "cache");
            File[] files = cacheDir.listFiles();
            if (files != null) {
                for (File f : files) {
                    String n = f.getName();
                    if (n.startsWith("prev_") || n.endsWith(".part")) {
                        f.delete();
                    }
                }
            }
        } catch (Exception ignored) {
        }
    }

    private int dp(int v) {
        return Math.round(TypedValue.applyDimension(
                TypedValue.COMPLEX_UNIT_DIP, v, getResources().getDisplayMetrics()));
    }

    private GradientDrawable roundedBox(int fill, int stroke, int radiusDp) {
        GradientDrawable gd = new GradientDrawable();
        gd.setColor(fill);
        gd.setCornerRadius(dp(radiusDp));
        if (stroke != 0) {
            gd.setStroke(dp(1), stroke);
        }
        return gd;
    }

    private Button makeButton(String label, int bgColor, int textColor) {
        Button b = new Button(this);
        b.setText(label);
        b.setAllCaps(false);
        b.setTextColor(textColor);
        b.setTextSize(TypedValue.COMPLEX_UNIT_SP, 13.5f);
        b.setTypeface(Typeface.DEFAULT_BOLD);
        b.setMinHeight(0);
        b.setMinimumHeight(0);
        b.setMinWidth(0);
        b.setMinimumWidth(0);
        b.setBackground(roundedBox(bgColor, COL_BORDER, 7));
        b.setPadding(dp(12), dp(6), dp(12), dp(6));
        return b;
    }

    private void buildUi() {
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(COL_BG);

        // 1. Compact Single-Row Top Header Bar (Title | Installed | Store | Live Status / Active Summary | Import USB | Restore Stock)
        LinearLayout header = new LinearLayout(this);
        header.setOrientation(LinearLayout.HORIZONTAL);
        header.setGravity(Gravity.CENTER_VERTICAL);
        header.setBackgroundColor(COL_PANEL);
        header.setPadding(dp(14), dp(6), dp(14), dp(6));

        TextView title = new TextView(this);
        title.setText("Boot Themes");
        title.setTextColor(COL_TEXT);
        title.setTextSize(TypedValue.COMPLEX_UNIT_SP, 18);
        title.setTypeface(Typeface.DEFAULT_BOLD);
        header.addView(title);

        tabInstalledBtn = makeButton("Installed", COL_ACCENT, 0xFF08101E);
        LinearLayout.LayoutParams t1Lp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        t1Lp.leftMargin = dp(14);
        header.addView(tabInstalledBtn, t1Lp);

        tabStoreBtn = makeButton("Online Store", COL_CARD, COL_TEXT);
        LinearLayout.LayoutParams t2Lp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        t2Lp.leftMargin = dp(6);
        header.addView(tabStoreBtn, t2Lp);

        // Combined Center Status & Active Theme Summary (eliminates separate banner row!)
        LinearLayout centerInfo = new LinearLayout(this);
        centerInfo.setOrientation(LinearLayout.HORIZONTAL);
        centerInfo.setGravity(Gravity.CENTER_VERTICAL);
        centerInfo.setPadding(dp(14), 0, dp(14), 0);

        statusBannerText = new TextView(this);
        statusBannerText.setTextColor(COL_ACCENT);
        statusBannerText.setTextSize(TypedValue.COMPLEX_UNIT_SP, 12.5f);
        statusBannerText.setSingleLine(true);
        statusBannerText.setEllipsize(TextUtils.TruncateAt.END);
        statusBannerText.setText("Ready");
        centerInfo.addView(statusBannerText, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1.0f));

        activeSummaryText = new TextView(this);
        activeSummaryText.setTextColor(COL_MUTED);
        activeSummaryText.setTextSize(TypedValue.COMPLEX_UNIT_SP, 12.5f);
        activeSummaryText.setSingleLine(true);
        activeSummaryText.setEllipsize(TextUtils.TruncateAt.END);
        activeSummaryText.setGravity(Gravity.CENTER_VERTICAL | Gravity.RIGHT);
        activeSummaryText.setPadding(dp(10), 0, 0, 0);
        centerInfo.addView(activeSummaryText, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        header.addView(centerInfo, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1.0f));

        Button importUsbBtn = makeButton("Import USB", COL_CARD, COL_TEXT);
        LinearLayout.LayoutParams usbLp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        usbLp.rightMargin = dp(6);
        header.addView(importUsbBtn, usbLp);

        Button restoreStockBtn = makeButton("Restore Stock", 0xFF3F1D28, 0xFFFCA5A5);
        header.addView(restoreStockBtn);

        root.addView(header, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        // 2. Slim Single-Row Store Filter Bar (Category | Sort | Parts | Search | Refresh — eliminates 2nd chip row!)
        storeFilterBar = new LinearLayout(this);
        storeFilterBar.setOrientation(LinearLayout.HORIZONTAL);
        storeFilterBar.setGravity(Gravity.CENTER_VERTICAL);
        storeFilterBar.setBackgroundColor(0xFF0E1729);
        storeFilterBar.setPadding(dp(14), dp(5), dp(14), dp(5));
        storeFilterBar.setVisibility(View.GONE);

        categoryDropdownBtn = makeButton("Category: All ▾", COL_CARD, COL_ACCENT);
        storeFilterBar.addView(categoryDropdownBtn);

        sortToggleBtn = makeButton("Sort: Newest", COL_CARD, COL_TEXT);
        LinearLayout.LayoutParams stLp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        stLp.leftMargin = dp(6);
        storeFilterBar.addView(sortToggleBtn, stLp);

        partToggleBtn = makeButton("Parts: All", COL_CARD, COL_TEXT);
        LinearLayout.LayoutParams pLp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        pLp.leftMargin = dp(6);
        storeFilterBar.addView(partToggleBtn, pLp);

        searchBox = new EditText(this);
        searchBox.setHint("Search name, author, tag...");
        searchBox.setHintTextColor(0xFF64748B);
        searchBox.setTextColor(COL_TEXT);
        searchBox.setTextSize(TypedValue.COMPLEX_UNIT_SP, 13.5f);
        searchBox.setSingleLine(true);
        searchBox.setImeOptions(EditorInfo.IME_ACTION_SEARCH);
        searchBox.setBackground(roundedBox(0xFF0B1120, COL_BORDER, 7));
        searchBox.setPadding(dp(12), dp(5), dp(12), dp(5));
        LinearLayout.LayoutParams sLp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1.0f);
        sLp.leftMargin = dp(8);
        sLp.rightMargin = dp(8);
        storeFilterBar.addView(searchBox, sLp);

        Button searchBtn = makeButton("Search / Refresh", COL_ACCENT, 0xFF08101E);
        storeFilterBar.addView(searchBtn);

        root.addView(storeFilterBar, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        // 3. Main Scrollable Card Grid (2 wide widescreen cards per row on 1920x720)
        contentScroll = new ScrollView(this);
        gridContainer = new LinearLayout(this);
        gridContainer.setOrientation(LinearLayout.VERTICAL);
        gridContainer.setPadding(dp(14), dp(8), dp(14), dp(18));
        contentScroll.addView(gridContainer, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        root.addView(contentScroll, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, 0, 1.0f));

        setContentView(root);

        // Wire Listeners
        tabInstalledBtn.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                switchTab(false);
            }
        });
        tabStoreBtn.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                switchTab(true);
            }
        });
        importUsbBtn.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                importFromUsbAsync();
            }
        });
        restoreStockBtn.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                promptRestoreStockDialog();
            }
        });
        categoryDropdownBtn.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                showCategoryPickerDialog();
            }
        });
        sortToggleBtn.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                storeSort = "newest".equals(storeSort) ? "popular" : "newest";
                sortToggleBtn.setText("Sort: " + ("newest".equals(storeSort) ? "Newest" : "Popular"));
                refreshStoreCatalogAsync();
            }
        });
        partToggleBtn.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                if ("all".equals(storePart)) storePart = "both";
                else if ("both".equals(storePart)) storePart = "onboot";
                else if ("onboot".equals(storePart)) storePart = "poweroff";
                else storePart = "all";

                String lbl = "All";
                if ("both".equals(storePart)) lbl = "Combo (Both)";
                else if ("onboot".equals(storePart)) lbl = "Boot Image";
                else if ("poweroff".equals(storePart)) lbl = "Goodbye Video";
                partToggleBtn.setText("Parts: " + lbl);
                refreshStoreCatalogAsync();
            }
        });
        searchBtn.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                storeQuery = searchBox.getText().toString().trim();
                refreshStoreCatalogAsync();
            }
        });
        searchBox.setOnEditorActionListener(new TextView.OnEditorActionListener() {
            @Override
            public boolean onEditorAction(TextView v, int actionId, KeyEvent event) {
                if (actionId == EditorInfo.IME_ACTION_SEARCH) {
                    storeQuery = searchBox.getText().toString().trim();
                    refreshStoreCatalogAsync();
                    return true;
                }
                return false;
            }
        });
    }

    private void switchTab(boolean toStore) {
        showingStoreTab = toStore;
        tabInstalledBtn.setBackground(roundedBox(!toStore ? COL_ACCENT : COL_CARD, COL_BORDER, 8));
        tabInstalledBtn.setTextColor(!toStore ? 0xFF08101E : COL_TEXT);

        tabStoreBtn.setBackground(roundedBox(toStore ? COL_ACCENT : COL_CARD, COL_BORDER, 8));
        tabStoreBtn.setTextColor(toStore ? 0xFF08101E : COL_TEXT);

        storeFilterBar.setVisibility(toStore ? View.VISIBLE : View.GONE);
        if (toStore) {
            if (lastStoreItems.isEmpty()) {
                refreshStoreCatalogAsync();
            } else {
                renderStoreGrid();
            }
        } else {
            refreshInstalledListAndRender();
        }
    }

    private void setStatus(final String msg, final int color) {
        ui.post(new Runnable() {
            @Override
            public void run() {
                statusBannerText.setText(msg);
                statusBannerText.setTextColor(color);
            }
        });
    }

    private void updateActiveHeaderSummary() {
        String bootId = repo.getActiveBootThemeId();
        String goodbyeId = repo.getActiveGoodbyeThemeId();
        String bootName = bootId.isEmpty() ? "Stock" : resolveThemeName(bootId);
        String goodbyeName = goodbyeId.isEmpty() ? "Stock" : resolveThemeName(goodbyeId);
        activeSummaryText.setText("Boot: " + bootName + "  |  Goodbye: " + goodbyeName);
    }

    private String resolveThemeName(String id) {
        BootThemeItem it = installedMap.get(id);
        return (it != null && !it.name.isEmpty()) ? it.name : id;
    }

    private void bootstrapInitialImportAndRefresh() {
        new Thread(new Runnable() {
            @Override
            public void run() {
                execHelper("ENSURE_BACKUPS");
                execNemesis("service", "start", HELPER_SVC);
                int imported = drainImportQueue();
                final int impCount = imported;
                ui.post(new Runnable() {
                    @Override
                    public void run() {
                        refreshInstalledListAndRender();
                        if (impCount > 0) {
                            setStatus("Imported " + impCount + " starter Boot Theme(s) from USB!", COL_GREEN);
                        }
                    }
                });
            }
        }).start();
    }

    private int drainImportQueue() {
        int count = 0;
        File queueDir = new File(repo.getBaseDir(), "import_queue");
        if (!queueDir.isDirectory()) return 0;
        File[] zips = queueDir.listFiles();
        if (zips == null) return 0;
        for (File z : zips) {
            if (z.isFile() && z.getName().toLowerCase(Locale.US).endsWith(".zip")) {
                try {
                    repo.installFromZip(z);
                    count++;
                } catch (Exception ignored) {
                }
                z.delete();
            }
        }
        return count;
    }

    private void importFromUsbAsync() {
        if (isBusy) return;
        isBusy = true;
        setStatus("Scanning USB stick (/BootThemes/*.zip)...", COL_ACCENT);
        new Thread(new Runnable() {
            @Override
            public void run() {
                try {
                    execHelper("SCAN_USB");
                    int count = drainImportQueue();
                    // Also check direct /storage/usb0/BootThemes in case readable directly
                    String[] directUsb = new String[] { "/storage/usb0/BootThemes", "/storage/usb1/BootThemes" };
                    for (String p : directUsb) {
                        File d = new File(p);
                        if (d.isDirectory()) {
                            File[] zs = d.listFiles();
                            if (zs != null) {
                                for (File z : zs) {
                                    if (z.isFile() && z.getName().toLowerCase(Locale.US).endsWith(".zip")) {
                                        try {
                                            BootThemeItem existing = null;
                                            BootThemeItem inst = repo.installFromZip(z);
                                            if (inst != null) count++;
                                        } catch (Exception ignored) {
                                        }
                                    }
                                }
                            }
                        }
                    }
                    final int total = count;
                    ui.post(new Runnable() {
                        @Override
                        public void run() {
                            refreshInstalledListAndRender();
                            if (total > 0) {
                                setStatus("Imported " + total + " Boot Theme package(s) from USB /BootThemes/!", COL_GREEN);
                            } else {
                                setStatus("No .zip packages found in /BootThemes/ folder on USB.", COL_AMBER);
                            }
                        }
                    });
                } finally {
                    isBusy = false;
                }
            }
        }).start();
    }

    private void refreshInstalledListAndRender() {
        List<BootThemeItem> list = repo.listInstalledThemes();
        installedMap.clear();
        for (BootThemeItem it : list) {
            installedMap.put(it.id, it);
        }
        updateActiveHeaderSummary();
        if (!showingStoreTab) {
            renderCards(list, false);
        }
    }

    private void refreshStoreCatalogAsync() {
        setStatus("Loading Boot Themes from https://market.ioniqthemes.com ...", COL_ACCENT);
        final String sortSnap = storeSort;
        final String catSnap = storeCategory;
        final String partSnap = storePart;
        final String qSnap = storeQuery;

        new Thread(new Runnable() {
            @Override
            public void run() {
                final BootThemeItem.CatalogResult res = storeClient.fetchCatalog(sortSnap, catSnap, partSnap, qSnap);
                ui.post(new Runnable() {
                    @Override
                    public void run() {
                        if (!res.ok) {
                            setStatus("Store connection error: " + res.errorMessage, COL_RED);
                            if (showingStoreTab) {
                                renderEmptyMessage("Could not load Online Store.\n" + res.errorMessage
                                        + "\n\nConnect the car to Wi-Fi / mobile hotspot and tap 'Search / Refresh'.");
                            }
                            return;
                        }
                        if (!res.categories.isEmpty()) {
                            storeCategories = res.categories;
                        }
                        updateCategoryButtonLabel();
                        lastStoreItems = res.items;
                        setStatus("Store: " + lastStoreItems.size() + " theme(s)", COL_GREEN);
                        if (showingStoreTab) {
                            renderStoreGrid();
                        }
                    }
                });
            }
        }).start();
    }

    private void updateCategoryButtonLabel() {
        String label = (storeCategory == null || storeCategory.isEmpty() || "All".equalsIgnoreCase(storeCategory))
                ? "All"
                : storeCategory;
        categoryDropdownBtn.setText("Category: " + label + " ▾");
    }

    private void showCategoryPickerDialog() {
        final List<String> all = new ArrayList<String>();
        all.add("All");
        if (storeCategories.isEmpty()) {
            String[] defaults = new String[] {
                "Animals", "Anime", "Fantasy / Art", "Cars", "Games / Comics",
                "Nature", "OEM+", "Sports", "TV / Movie / Music", "Utility", "Adult", "Other"
            };
            Collections.addAll(all, defaults);
        } else {
            for (String c : storeCategories) {
                if (!"All".equalsIgnoreCase(c)) all.add(c);
            }
        }
        final String[] labels = all.toArray(new String[all.size()]);
        int checked = 0;
        for (int i = 0; i < labels.length; i++) {
            if (labels[i].equalsIgnoreCase(storeCategory)) {
                checked = i;
                break;
            }
        }
        new AlertDialog.Builder(this, AlertDialog.THEME_HOLO_DARK)
                .setTitle("Filter by Category")
                .setSingleChoiceItems(labels, checked, new DialogInterface.OnClickListener() {
                    @Override
                    public void onClick(DialogInterface dialog, int which) {
                        storeCategory = labels[which];
                        updateCategoryButtonLabel();
                        dialog.dismiss();
                        refreshStoreCatalogAsync();
                    }
                })
                .setNegativeButton("Cancel", null)
                .show();
    }

    private void renderStoreGrid() {
        List<BootThemeItem> local = repo.listInstalledThemes();
        installedMap.clear();
        for (BootThemeItem it : local) installedMap.put(it.id, it);
        updateActiveHeaderSummary();

        if (lastStoreItems.isEmpty()) {
            renderEmptyMessage("No Boot Themes published yet matching your filter.\nUse the Windows Boot Theme Studio app to create and upload themes!");
        } else {
            renderCards(lastStoreItems, true);
        }
    }

    private void renderEmptyMessage(String message) {
        gridContainer.removeAllViews();
        TextView tv = new TextView(this);
        tv.setText(message);
        tv.setTextColor(COL_MUTED);
        tv.setTextSize(TypedValue.COMPLEX_UNIT_SP, 17);
        tv.setGravity(Gravity.CENTER);
        tv.setPadding(dp(32), dp(56), dp(32), dp(56));
        gridContainer.addView(tv, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
    }

    private void renderCards(List<BootThemeItem> items, boolean isStoreMode) {
        gridContainer.removeAllViews();
        if (items.isEmpty()) {
            renderEmptyMessage("No Boot Themes installed locally yet.\nSwitch to 'Online Store' to download themes, or place .zip themes in /BootThemes/ on a USB stick and tap 'Import USB'.");
            return;
        }

        String activeBoot = repo.getActiveBootThemeId();
        String activeGoodbye = repo.getActiveGoodbyeThemeId();

        // 2 cards per row on 1920x720
        LinearLayout currentRow = null;
        for (int i = 0; i < items.size(); i++) {
            if (i % 2 == 0) {
                currentRow = new LinearLayout(this);
                currentRow.setOrientation(LinearLayout.HORIZONTAL);
                LinearLayout.LayoutParams rowLp = new LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
                rowLp.bottomMargin = dp(14);
                gridContainer.addView(currentRow, rowLp);
            }

            BootThemeItem item = items.get(i);
            View card = buildThemeCard(item, isStoreMode, activeBoot, activeGoodbye);
            LinearLayout.LayoutParams cardLp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1.0f);
            if (i % 2 == 0) cardLp.rightMargin = dp(7);
            else cardLp.leftMargin = dp(7);
            currentRow.addView(card, cardLp);

            if (i == items.size() - 1 && i % 2 == 0) {
                View spacer = new View(this);
                LinearLayout.LayoutParams spLp = new LinearLayout.LayoutParams(0, 1, 1.0f);
                spLp.leftMargin = dp(7);
                currentRow.addView(spacer, spLp);
            }
        }
    }

    private View buildThemeCard(final BootThemeItem item, final boolean isStore, String activeBoot, String activeGoodbye) {
        boolean isBootActive = item.id.equals(activeBoot);
        boolean isGoodbyeActive = item.id.equals(activeGoodbye);
        int borderCol = (isBootActive || isGoodbyeActive) ? COL_GREEN : COL_BORDER;

        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setBackground(roundedBox(COL_CARD, borderCol, 9));
        card.setPadding(dp(10), dp(9), dp(10), dp(9));

        // Dual side-by-side 8:3 widescreen preview strip (Boot Image | Goodbye Video)
        LinearLayout previewRow = new LinearLayout(this);
        previewRow.setOrientation(LinearLayout.HORIZONTAL);

        if (item.hasOnBoot) {
            FrameLayout p1 = createPreviewBox("BOOT IMAGE", item, true, isStore);
            LinearLayout.LayoutParams lp1 = new LinearLayout.LayoutParams(0, dp(148), 1.0f);
            if (item.hasPowerOff) lp1.rightMargin = dp(5);
            previewRow.addView(p1, lp1);
        }
        if (item.hasPowerOff) {
            String badge = "GOODBYE VIDEO" + (item.getDurationLabel().isEmpty() ? "" : (" (" + item.getDurationLabel() + ")"));
            FrameLayout p2 = createPreviewBox(badge, item, false, isStore);
            LinearLayout.LayoutParams lp2 = new LinearLayout.LayoutParams(0, dp(148), 1.0f);
            if (item.hasOnBoot) lp2.leftMargin = dp(5);
            previewRow.addView(p2, lp2);
        }
        card.addView(previewRow, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        // Title + Active / Installed Badges
        LinearLayout titleRow = new LinearLayout(this);
        titleRow.setOrientation(LinearLayout.HORIZONTAL);
        titleRow.setGravity(Gravity.CENTER_VERTICAL);
        titleRow.setPadding(0, dp(7), 0, dp(1));

        TextView nameTv = new TextView(this);
        nameTv.setText(item.name);
        nameTv.setTextColor(COL_TEXT);
        nameTv.setTextSize(TypedValue.COMPLEX_UNIT_SP, 16.5f);
        nameTv.setTypeface(Typeface.DEFAULT_BOLD);
        nameTv.setSingleLine(true);
        nameTv.setEllipsize(TextUtils.TruncateAt.END);
        titleRow.addView(nameTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1.0f));

        if (isBootActive || isGoodbyeActive) {
            String actTxt = (isBootActive && isGoodbyeActive) ? "ACTIVE (BOTH)"
                    : (isBootActive ? "ACTIVE BOOT" : "ACTIVE GOODBYE");
            TextView actBadge = makeBadge(actTxt, 0xFF064E3B, COL_GREEN);
            titleRow.addView(actBadge);
        } else if (isStore && installedMap.containsKey(item.id)) {
            BootThemeItem loc = installedMap.get(item.id);
            boolean hasUpdate = (item.revision > loc.revision) || !item.version.equals(loc.version);
            titleRow.addView(makeBadge(hasUpdate ? "UPDATE AVAILABLE" : "INSTALLED",
                    hasUpdate ? 0xFF451A03 : 0xFF1E3A5F,
                    hasUpdate ? COL_AMBER : COL_ACCENT));
        }

        card.addView(titleRow);

        // Author + Category + Size
        String meta = "by " + item.author + "  •  " + item.category + "  •  v" + item.version + " (r" + item.revision + ")";
        if (item.fileSizeBytes > 0) {
            meta += String.format(Locale.US, "  •  %.1f MB", item.fileSizeBytes / 1048576.0);
        }
        if (isStore && item.downloads > 0) {
            meta += "  •  " + item.downloads + " dl";
        }
        TextView metaTv = new TextView(this);
        metaTv.setText(meta);
        metaTv.setTextColor(COL_MUTED);
        metaTv.setTextSize(TypedValue.COMPLEX_UNIT_SP, 13);
        metaTv.setSingleLine(true);
        card.addView(metaTv);

        if (!item.description.isEmpty()) {
            TextView descTv = new TextView(this);
            descTv.setText(item.description);
            descTv.setTextColor(0xFFCBD5E1);
            descTv.setTextSize(TypedValue.COMPLEX_UNIT_SP, 12);
            descTv.setMaxLines(1);
            descTv.setEllipsize(TextUtils.TruncateAt.END);
            descTv.setPadding(0, dp(2), 0, dp(2));
            card.addView(descTv);
        }

        // Action Buttons Row
        LinearLayout btnRow = new LinearLayout(this);
        btnRow.setOrientation(LinearLayout.HORIZONTAL);
        btnRow.setPadding(0, dp(5), 0, 0);

        Button previewBtn = makeButton("Preview", COL_PANEL, COL_TEXT);
        previewBtn.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                showPreviewDialog(item, isStore);
            }
        });
        btnRow.addView(previewBtn);

        if (isStore) {
            BootThemeItem loc = installedMap.get(item.id);
            String dlLabel = "Download";
            if (loc != null) {
                boolean hasUpdate = (item.revision > loc.revision) || !item.version.equals(loc.version);
                dlLabel = hasUpdate ? "Update" : "Re-download";
            }
            Button dlBtn = makeButton(dlLabel, COL_ACCENT, 0xFF08101E);
            LinearLayout.LayoutParams dlLp = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            dlLp.leftMargin = dp(8);
            dlBtn.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    downloadAndInstallFromStoreAsync(item, false);
                }
            });
            btnRow.addView(dlBtn, dlLp);

            Button dlApplyBtn = makeButton("Download & Apply", COL_GREEN, 0xFF04130D);
            LinearLayout.LayoutParams daLp = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            daLp.leftMargin = dp(8);
            dlApplyBtn.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    downloadAndInstallFromStoreAsync(item, true);
                }
            });
            btnRow.addView(dlApplyBtn, daLp);
        } else {
            if (item.hasOnBoot && item.hasPowerOff) {
                Button applyBoth = makeButton("Apply Both", COL_GREEN, 0xFF04130D);
                LinearLayout.LayoutParams abLp = new LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
                abLp.leftMargin = dp(8);
                applyBoth.setOnClickListener(new View.OnClickListener() {
                    @Override
                    public void onClick(View v) {
                        applyThemeAsync(item, true, true);
                    }
                });
                btnRow.addView(applyBoth, abLp);

                Button applyBoot = makeButton("Boot Only", COL_ACCENT, 0xFF08101E);
                LinearLayout.LayoutParams boLp = new LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
                boLp.leftMargin = dp(8);
                applyBoot.setOnClickListener(new View.OnClickListener() {
                    @Override
                    public void onClick(View v) {
                        applyThemeAsync(item, true, false);
                    }
                });
                btnRow.addView(applyBoot, boLp);

                Button applyGood = makeButton("Goodbye Only", COL_ACCENT, 0xFF08101E);
                LinearLayout.LayoutParams goLp = new LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
                goLp.leftMargin = dp(8);
                applyGood.setOnClickListener(new View.OnClickListener() {
                    @Override
                    public void onClick(View v) {
                        applyThemeAsync(item, false, true);
                    }
                });
                btnRow.addView(applyGood, goLp);
            } else if (item.hasOnBoot) {
                Button applyBoot = makeButton("Apply Boot Image", COL_GREEN, 0xFF04130D);
                LinearLayout.LayoutParams boLp = new LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
                boLp.leftMargin = dp(8);
                applyBoot.setOnClickListener(new View.OnClickListener() {
                    @Override
                    public void onClick(View v) {
                        applyThemeAsync(item, true, false);
                    }
                });
                btnRow.addView(applyBoot, boLp);
            } else if (item.hasPowerOff) {
                Button applyGood = makeButton("Apply Goodbye Video", COL_GREEN, 0xFF04130D);
                LinearLayout.LayoutParams goLp = new LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
                goLp.leftMargin = dp(8);
                applyGood.setOnClickListener(new View.OnClickListener() {
                    @Override
                    public void onClick(View v) {
                        applyThemeAsync(item, false, true);
                    }
                });
                btnRow.addView(applyGood, goLp);
            }

            View spacer = new View(this);
            btnRow.addView(spacer, new LinearLayout.LayoutParams(0, 1, 1.0f));

            Button delBtn = makeButton("Delete", 0xFF3F1D28, 0xFFFCA5A5);
            delBtn.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    confirmDeleteLocalTheme(item);
                }
            });
            btnRow.addView(delBtn);
        }

        card.addView(btnRow);
        return card;
    }

    private TextView makeBadge(String text, int bg, int fg) {
        TextView tv = new TextView(this);
        tv.setText(text);
        tv.setTextColor(fg);
        tv.setTextSize(TypedValue.COMPLEX_UNIT_SP, 11);
        tv.setTypeface(Typeface.DEFAULT_BOLD);
        tv.setBackground(roundedBox(bg, fg, 6));
        tv.setPadding(dp(8), dp(3), dp(8), dp(3));
        return tv;
    }

    private FrameLayout createPreviewBox(String label, final BootThemeItem item, final boolean isBootSlot, final boolean isStore) {
        FrameLayout box = new FrameLayout(this);
        box.setBackground(roundedBox(0xFF050811, COL_BORDER, 8));

        final ImageView iv = new ImageView(this);
        iv.setScaleType(ImageView.ScaleType.FIT_CENTER);
        box.addView(iv, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        TextView tag = new TextView(this);
        tag.setText(label);
        tag.setTextColor(0xFFE2E8F0);
        tag.setTextSize(TypedValue.COMPLEX_UNIT_SP, 10);
        tag.setTypeface(Typeface.DEFAULT_BOLD);
        tag.setBackground(roundedBox(0xB00F172A, 0, 4));
        tag.setPadding(dp(6), dp(2), dp(6), dp(2));
        FrameLayout.LayoutParams tLp = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.TOP | Gravity.LEFT);
        tLp.leftMargin = dp(6);
        tLp.topMargin = dp(6);
        box.addView(tag, tLp);

        box.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                showPreviewDialog(item, isStore);
            }
        });

        loadCardThumbnailAsync(iv, item, isBootSlot, isStore);
        return box;
    }

    private void loadCardThumbnailAsync(final ImageView iv, final BootThemeItem item, final boolean isBootSlot, final boolean isStore) {
        final String cacheKey;
        if (isStore) {
            cacheKey = "store:" + item.id + ":" + item.revision + ":" + (isBootSlot ? "boot" : "poster");
        } else {
            cacheKey = "local:" + item.id + ":" + item.revision + ":" + (isBootSlot ? "boot" : "poster");
        }
        iv.setTag(cacheKey);

        Bitmap cached = bmpCache.get(cacheKey);
        if (cached != null && !cached.isRecycled()) {
            iv.setImageBitmap(cached);
            return;
        }

        thumbPool.execute(new Runnable() {
            @Override
            public void run() {
                Bitmap bmp = null;
                try {
                    if (isStore) {
                        String url = isBootSlot
                                ? item.onBootPreviewUrl
                                : (!item.powerOffPosterUrl.isEmpty() ? item.powerOffPosterUrl : item.powerOffPreviewUrl);
                        if (!url.isEmpty() && !url.toLowerCase(Locale.US).endsWith(".mp4")) {
                            byte[] raw = storeClient.downloadPreviewBytes(url, 3 * 1024 * 1024);
                            if (raw != null) {
                                bmp = decodeSampledBitmap(raw, 480, 180);
                            }
                        }
                    } else {
                        File f = isBootSlot ? item.localOnBootPreview : item.localPowerOffPoster;
                        if (f == null && !isBootSlot && item.localPowerOffPreview != null
                                && item.localPowerOffPreview.getName().toLowerCase(Locale.US).endsWith(".gif")) {
                            f = item.localPowerOffPreview;
                        }
                        if (f != null && f.isFile()) {
                            byte[] raw = JpuSplashBuilder.readFileBytes(f);
                            bmp = decodeSampledBitmap(raw, 480, 180);
                        }
                    }
                } catch (Exception ignored) {
                }

                if (bmp != null) {
                    bmpCache.put(cacheKey, bmp);
                    final Bitmap finalBmp = bmp;
                    ui.post(new Runnable() {
                        @Override
                        public void run() {
                            if (cacheKey.equals(iv.getTag())) {
                                iv.setImageBitmap(finalBmp);
                            }
                        }
                    });
                }
            }
        });
    }

    private static Bitmap decodeSampledBitmap(byte[] data, int reqW, int reqH) {
        BitmapFactory.Options opts = new BitmapFactory.Options();
        opts.inJustDecodeBounds = true;
        BitmapFactory.decodeByteArray(data, 0, data.length, opts);
        int inSample = 1;
        if (opts.outHeight > reqH || opts.outWidth > reqW) {
            int halfH = opts.outHeight / 2;
            int halfW = opts.outWidth / 2;
            while ((halfH / inSample) >= reqH && (halfW / inSample) >= reqW) {
                inSample *= 2;
            }
        }
        opts.inJustDecodeBounds = false;
        opts.inSampleSize = inSample;
        opts.inPreferredConfig = Bitmap.Config.RGB_565;
        return BitmapFactory.decodeByteArray(data, 0, data.length, opts);
    }

    private void showPreviewDialog(final BootThemeItem item, final boolean isStore) {
        final Dialog dlg = new Dialog(this, android.R.style.Theme_Holo_NoActionBar);
        LinearLayout wrap = new LinearLayout(this);
        wrap.setOrientation(LinearLayout.VERTICAL);
        wrap.setBackgroundColor(0xFF070B14);
        wrap.setPadding(dp(20), dp(14), dp(20), dp(16));

        LinearLayout top = new LinearLayout(this);
        top.setOrientation(LinearLayout.HORIZONTAL);
        top.setGravity(Gravity.CENTER_VERTICAL);

        TextView title = new TextView(this);
        title.setText(item.name + "  —  Live Preview");
        title.setTextColor(COL_TEXT);
        title.setTextSize(TypedValue.COMPLEX_UNIT_SP, 20);
        title.setTypeface(Typeface.DEFAULT_BOLD);
        top.addView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1.0f));

        final FrameLayout stage = new FrameLayout(this);
        stage.setBackground(roundedBox(Color.BLACK, COL_BORDER, 10));

        if (item.hasOnBoot && item.hasPowerOff) {
            Button showBootBtn = makeButton("Show Boot Image", COL_ACCENT, 0xFF08101E);
            Button showGoodbyeBtn = makeButton("Play Goodbye Video", COL_GREEN, 0xFF04130D);
            LinearLayout.LayoutParams b1 = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            b1.rightMargin = dp(8);
            top.addView(showBootBtn, b1);
            LinearLayout.LayoutParams b2 = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            b2.rightMargin = dp(12);
            top.addView(showGoodbyeBtn, b2);

            showBootBtn.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    populatePreviewStage(stage, item, true, isStore);
                }
            });
            showGoodbyeBtn.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    populatePreviewStage(stage, item, false, isStore);
                }
            });
        }

        Button closeBtn = makeButton("Close", COL_CARD, COL_TEXT);
        closeBtn.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                dlg.dismiss();
            }
        });
        top.addView(closeBtn);

        wrap.addView(top, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        LinearLayout.LayoutParams stageLp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, 0, 1.0f);
        stageLp.topMargin = dp(12);
        wrap.addView(stage, stageLp);

        dlg.setOnDismissListener(new DialogInterface.OnDismissListener() {
            @Override
            public void onDismiss(DialogInterface dialog) {
                stage.removeAllViews();
                clearPreviewTempCache();
            }
        });

        dlg.setContentView(wrap);
        dlg.show();

        // Default to playing Goodbye Video if present, else Boot Image
        populatePreviewStage(stage, item, !item.hasPowerOff, isStore);
    }

    private void populatePreviewStage(final FrameLayout stage, final BootThemeItem item, final boolean showBoot, final boolean isStore) {
        stage.removeAllViews();
        final TextView loadingTv = new TextView(this);
        loadingTv.setText(showBoot ? "Loading Boot Image preview..." : "Loading Goodbye Video preview...");
        loadingTv.setTextColor(COL_MUTED);
        loadingTv.setTextSize(TypedValue.COMPLEX_UNIT_SP, 16);
        loadingTv.setGravity(Gravity.CENTER);
        stage.addView(loadingTv, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        new Thread(new Runnable() {
            @Override
            public void run() {
                try {
                    if (showBoot) {
                        byte[] raw = null;
                        if (isStore) {
                            raw = storeClient.downloadPreviewBytes(item.onBootPreviewUrl, 5 * 1024 * 1024);
                        } else {
                            File f = item.localSplash1920 != null ? item.localSplash1920 : item.localOnBootPreview;
                            if (f != null && f.isFile()) raw = JpuSplashBuilder.readFileBytes(f);
                        }
                        if (raw == null) throw new Exception("No Boot Image preview available.");
                        final Bitmap bmp = decodeSampledBitmap(raw, 1920, 720);
                        ui.post(new Runnable() {
                            @Override
                            public void run() {
                                stage.removeAllViews();
                                ImageView iv = new ImageView(MainActivity.this);
                                iv.setScaleType(ImageView.ScaleType.FIT_CENTER);
                                iv.setImageBitmap(bmp);
                                stage.addView(iv, new FrameLayout.LayoutParams(
                                        ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
                            }
                        });
                    } else {
                        if (!isStore && item.localGoodbyeMp4 != null && item.localGoodbyeMp4.isFile()) {
                            final File mp4 = item.localGoodbyeMp4;
                            ui.post(new Runnable() {
                                @Override
                                public void run() {
                                    playMp4InStage(stage, mp4);
                                }
                            });
                            return;
                        }
                        if (isStore && !item.powerOffPreviewUrl.isEmpty()) {
                            String lower = item.powerOffPreviewUrl.toLowerCase(Locale.US);
                            if (lower.endsWith(".mp4") || "video/mp4".equalsIgnoreCase(item.powerOffPreviewType)) {
                                clearPreviewTempCache();
                                File cacheDir = new File(repo.getBaseDir(), "cache");
                                final File tmpMp4 = new File(cacheDir, "prev_" + LocalThemeRepository.sanitizeSlug(item.id) + ".mp4");
                                storeClient.downloadFileStreamed(item.powerOffPreviewUrl, tmpMp4, "", 12L * 1024L * 1024L, null, null);
                                ui.post(new Runnable() {
                                    @Override
                                    public void run() {
                                        if (stage.getWindowToken() == null) {
                                            tmpMp4.delete();
                                            return;
                                        }
                                        playMp4InStage(stage, tmpMp4);
                                    }
                                });
                            } else {
                                final byte[] gifBytes = storeClient.downloadPreviewBytes(item.powerOffPreviewUrl, 11 * 1024 * 1024);
                                ui.post(new Runnable() {
                                    @Override
                                    public void run() {
                                        stage.removeAllViews();
                                        GifMovieView gv = new GifMovieView(MainActivity.this);
                                        gv.setGifBytes(gifBytes);
                                        stage.addView(gv, new FrameLayout.LayoutParams(
                                                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
                                    }
                                });
                            }
                            return;
                        }
                        throw new Exception("No Goodbye Video preview available.");
                    }
                } catch (final Exception ex) {
                    ui.post(new Runnable() {
                        @Override
                        public void run() {
                            loadingTv.setText("Preview failed: " + ex.getMessage());
                        }
                    });
                }
            }
        }).start();
    }

    private void playMp4InStage(FrameLayout stage, final File mp4File) {
        stage.removeAllViews();
        final VideoView vv = new VideoView(this);
        FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT, Gravity.CENTER);
        stage.addView(vv, lp);
        vv.setVideoURI(Uri.fromFile(mp4File));
        vv.setOnPreparedListener(new MediaPlayer.OnPreparedListener() {
            @Override
            public void onPrepared(MediaPlayer mp) {
                mp.setLooping(true);
                vv.start();
            }
        });
    }

    private void downloadAndInstallFromStoreAsync(final BootThemeItem item, final boolean autoApplyAfter) {
        if (isBusy) return;
        isBusy = true;
        setStatus("Downloading '" + item.name + "'...", COL_ACCENT);

        new Thread(new Runnable() {
            @Override
            public void run() {
                File cacheDir = new File(repo.getBaseDir(), "cache");
                File zipDest = new File(cacheDir, LocalThemeRepository.sanitizeSlug(item.id) + "_dl.zip");
                try {
                    storeClient.downloadFileStreamed(
                            item.downloadUrl,
                            zipDest,
                            item.sha256,
                            BootThemeStoreClient.MAX_PACKAGE_BYTES,
                            new BootThemeStoreClient.ProgressListener() {
                                private long lastUiUpdate = 0L;
                                @Override
                                public void onProgress(long read, long total) {
                                    long now = System.currentTimeMillis();
                                    if (now - lastUiUpdate > 250L) {
                                        lastUiUpdate = now;
                                        String pct = total > 0
                                                ? String.format(Locale.US, " (%d%% — %.1f / %.1f MB)", (read * 100L) / total, read / 1048576.0, total / 1048576.0)
                                                : String.format(Locale.US, " (%.1f MB)", read / 1048576.0);
                                        setStatus("Downloading '" + item.name + "'" + pct + "...", COL_ACCENT);
                                    }
                                }
                            },
                            null);

                    setStatus("Verifying & unpacking '" + item.name + "'...", COL_ACCENT);
                    final BootThemeItem installed = repo.installFromZip(zipDest);
                    zipDest.delete();

                    ui.post(new Runnable() {
                        @Override
                        public void run() {
                            isBusy = false;
                            refreshInstalledListAndRender();
                            if (showingStoreTab) {
                                renderStoreGrid();
                            }
                            if (autoApplyAfter && installed != null) {
                                applyThemeAsync(installed, installed.hasOnBoot, installed.hasPowerOff);
                            } else {
                                setStatus("Installed '" + item.name + "'! Ready to apply.", COL_GREEN);
                            }
                        }
                    });
                } catch (final Exception ex) {
                    zipDest.delete();
                    isBusy = false;
                    setStatus("Download failed: " + ex.getMessage(), COL_RED);
                }
            }
        }).start();
    }

    private void applyThemeAsync(final BootThemeItem item, final boolean applyBoot, final boolean applyGoodbye) {
        if (isBusy) return;
        isBusy = true;
        setStatus("Applying '" + item.name + "'...", COL_ACCENT);

        new Thread(new Runnable() {
            @Override
            public void run() {
                try {
                    execHelper("ENSURE_BACKUPS");

                    if (applyBoot && item.hasOnBoot && item.localSplash1920 != null) {
                        setStatus("Encoding & flashing hardware-verified 1920x720 Boot Image...", COL_ACCENT);
                        File stockSplash = new File(repo.getBaseDir(), "splash_stock.img");
                        File candSplash = new File(repo.getActiveDir(), "splash_candidate.img");
                        JpuSplashBuilder.buildCustomSplashPartition(
                                stockSplash, item.localSplash1920, item.localSplash1280, candSplash);

                        CmdResult res = execHelper("FLASH_CANDIDATE_SPLASH");
                        if (res.rc != 0 || !res.out.contains("OK")) {
                            throw new Exception("Boot Image flash failed: " + res.out);
                        }
                        repo.setActiveBootThemeId(item.id);
                    }

                    if (applyGoodbye && item.hasPowerOff && item.localGoodbyeMp4 != null) {
                        setStatus("Building & mounting Goodbye Video in RAM (zero /system writes)...", COL_ACCENT);
                        File stockApk = new File(repo.getBaseDir(), "WelcomeGoodbye_stock.apk");
                        File stockOdex = new File(repo.getBaseDir(), "WelcomeGoodbye_stock.odex");
                        File outApk = new File(repo.getActiveDir(), "WelcomeGoodbye_themed.apk");
                        File outOdex = new File(repo.getActiveDir(), "WelcomeGoodbye_themed.odex");

                        WelcomeApkBuilder.buildThemedWelcomeFiles(
                                stockApk, stockOdex, item.localGoodbyeMp4, item.localWelcomeMp4, outApk, outOdex);
                        repo.setActiveGoodbyeTheme(item.id, true);

                        execNemesis("service", "start", HELPER_SVC);
                        CmdResult res = execHelper("APPLY_GOODBYE");
                        if (res.rc != 0) {
                            throw new Exception("Goodbye Video RAM mount failed: " + res.out);
                        }
                    }

                    ui.post(new Runnable() {
                        @Override
                        public void run() {
                            refreshInstalledListAndRender();
                            if (showingStoreTab) renderStoreGrid();
                            String what = (applyBoot && applyGoodbye) ? "Boot Image & Goodbye Video"
                                    : (applyBoot ? "Boot Image" : "Goodbye Video");
                            setStatus("Applied " + what + " from '" + item.name + "'!", COL_GREEN);
                        }
                    });
                } catch (final Exception ex) {
                    setStatus("Apply failed: " + ex.getMessage(), COL_RED);
                } finally {
                    isBusy = false;
                }
            }
        }).start();
    }

    private void promptRestoreStockDialog() {
        final String[] options = new String[] {
            "Restore BOTH to Factory Stock",
            "Restore Boot Image Only (keep Goodbye Video)",
            "Restore Goodbye Video Only (keep Boot Image)"
        };
        new AlertDialog.Builder(this, AlertDialog.THEME_HOLO_DARK)
                .setTitle("Restore Factory Stock")
                .setItems(options, new DialogInterface.OnClickListener() {
                    @Override
                    public void onClick(DialogInterface dialog, int which) {
                        restoreStockAsync(which == 0 || which == 1, which == 0 || which == 2);
                    }
                })
                .setNegativeButton("Cancel", null)
                .show();
    }

    private void restoreStockAsync(final boolean restoreBoot, final boolean restoreGoodbye) {
        if (isBusy) return;
        isBusy = true;
        setStatus("Restoring factory stock...", COL_AMBER);
        new Thread(new Runnable() {
            @Override
            public void run() {
                try {
                    if (restoreBoot && restoreGoodbye) {
                        CmdResult r = execHelper("RESTORE_ALL_STOCK");
                        if (r.rc != 0) throw new Exception(r.out);
                        repo.setActiveBootThemeId("");
                        repo.setActiveGoodbyeTheme("", false);
                    } else if (restoreBoot) {
                        CmdResult r = execHelper("RESTORE_STOCK_SPLASH");
                        if (r.rc != 0) throw new Exception(r.out);
                        repo.setActiveBootThemeId("");
                    } else if (restoreGoodbye) {
                        CmdResult r = execHelper("RESTORE_STOCK_GOODBYE");
                        if (r.rc != 0) throw new Exception(r.out);
                        repo.setActiveGoodbyeTheme("", false);
                    }
                    ui.post(new Runnable() {
                        @Override
                        public void run() {
                            refreshInstalledListAndRender();
                            if (showingStoreTab) renderStoreGrid();
                            setStatus("Restored factory stock successfully!", COL_GREEN);
                        }
                    });
                } catch (final Exception ex) {
                    setStatus("Restore failed: " + ex.getMessage(), COL_RED);
                } finally {
                    isBusy = false;
                }
            }
        }).start();
    }

    private void confirmDeleteLocalTheme(final BootThemeItem item) {
        boolean isActBoot = item.id.equals(repo.getActiveBootThemeId());
        boolean isActGood = item.id.equals(repo.getActiveGoodbyeThemeId());
        String extraWarn = (isActBoot || isActGood)
                ? "\n\nNote: This theme is currently active — deleting it will first restore its active slot(s) to Factory Stock."
                : "";

        new AlertDialog.Builder(this, AlertDialog.THEME_HOLO_DARK)
                .setTitle("Delete '" + item.name + "'?")
                .setMessage("Remove this Boot Theme from local storage?" + extraWarn)
                .setPositiveButton("Delete", new DialogInterface.OnClickListener() {
                    @Override
                    public void onClick(DialogInterface dialog, int which) {
                        deleteThemeAsync(item);
                    }
                })
                .setNegativeButton("Cancel", null)
                .show();
    }

    private void deleteThemeAsync(final BootThemeItem item) {
        if (isBusy) return;
        isBusy = true;
        new Thread(new Runnable() {
            @Override
            public void run() {
                try {
                    boolean isActBoot = item.id.equals(repo.getActiveBootThemeId());
                    boolean isActGood = item.id.equals(repo.getActiveGoodbyeThemeId());
                    if (isActBoot) {
                        execHelper("RESTORE_STOCK_SPLASH");
                        repo.setActiveBootThemeId("");
                    }
                    if (isActGood) {
                        execHelper("RESTORE_STOCK_GOODBYE");
                        repo.setActiveGoodbyeTheme("", false);
                    }
                    repo.deleteTheme(item.id);
                    ui.post(new Runnable() {
                        @Override
                        public void run() {
                            refreshInstalledListAndRender();
                            setStatus("Deleted '" + item.name + "'.", COL_GREEN);
                        }
                    });
                } finally {
                    isBusy = false;
                }
            }
        }).start();
    }

    private String resolveHelperPath() {
        for (String p : HELPER_CANDIDATES) {
            if (new File(p).isFile()) return p;
        }
        return HELPER_CANDIDATES[0];
    }

    private CmdResult execHelper(String action) {
        String script = resolveHelperPath();
        return execNemesis("root", "-c", "sh \"" + script + "\" " + action);
    }

    private CmdResult execNemesis(String... args) {
        try {
            String[] cmd = new String[args.length + 1];
            cmd[0] = NEMESIS_BIN;
            System.arraycopy(args, 0, cmd, 1, args.length);
            ProcessBuilder pb = new ProcessBuilder(cmd);
            pb.redirectErrorStream(true);
            Process p = pb.start();
            ByteArrayOutputStream bos = new ByteArrayOutputStream();
            InputStream in = p.getInputStream();
            byte[] buf = new byte[4096];
            int n;
            while ((n = in.read(buf)) > 0) {
                bos.write(buf, 0, n);
            }
            in.close();
            int rc = p.waitFor();
            return new CmdResult(rc, bos.toString("UTF-8").trim());
        } catch (Exception e) {
            return new CmdResult(-1, e.toString());
        }
    }

    private static final class CmdResult {
        final int rc;
        final String out;

        CmdResult(int rc, String out) {
            this.rc = rc;
            this.out = out == null ? "" : out;
        }
    }
}
