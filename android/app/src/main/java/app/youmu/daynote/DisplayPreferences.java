package app.youmu.daynote;

import android.content.Context;
import android.content.SharedPreferences;
import android.content.res.Configuration;

/** Device-local appearance only. Never included in the calendar or cloud payload. */
public final class DisplayPreferences {
    public static final float[] APP_FONT_SCALES = {.85f, 1f, 1.15f, 1.30f, 1.50f};
    public static final String[] APP_FONT_LABELS = {"较小（85%）", "标准（100%）", "较大（115%）", "大（130%）", "特大（150%）"};
    public static final float[] WIDGET_FONT_SCALES = {.85f, 1f, 1.15f, 1.30f, 1.50f, 1.75f, 2f};
    public static final String[] WIDGET_FONT_LABELS = {"较小（85%）", "标准（100%）", "较大（115%）", "大（130%）", "特大（150%）", "超大（175%）", "最大（200%）"};
    // The old API and preference key remain the application font setting.
    public static final float[] FONT_SCALES = APP_FONT_SCALES;
    public static final String[] FONT_LABELS = APP_FONT_LABELS;
    private static SharedPreferences preferences(Context context) {
        return context.getApplicationContext().getSharedPreferences("appearance", Context.MODE_PRIVATE);
    }
    public static String theme(Context context) {
        String value = preferences(context).getString("theme", "system");
        return "light".equals(value) || "dark".equals(value) ? value : "system";
    }
    public static boolean isDark(Context context) {
        String mode = theme(context);
        if (!"system".equals(mode)) return "dark".equals(mode);
        return (context.getApplicationContext().getResources().getConfiguration().uiMode
                & Configuration.UI_MODE_NIGHT_MASK) == Configuration.UI_MODE_NIGHT_YES;
    }
    private static float canonicalScale(float value, float[] scales) {
        for (float allowed : scales) if (Math.abs(allowed - value) < .001f) return allowed;
        return Float.NaN;
    }
    private static float readScale(SharedPreferences preferences, String key, float[] scales) {
        try {
            float value = canonicalScale(preferences.getFloat(key, 1f), scales);
            return Float.isNaN(value) ? 1f : value;
        } catch (ClassCastException invalidOldPreference) { return 1f; }
    }
    public static float appFontScale(Context context) {
        return readScale(preferences(context), "font_scale", APP_FONT_SCALES);
    }
    public static float widgetFontScale(Context context) {
        SharedPreferences saved = preferences(context);
        // Reading and previewing never write preferences. Until the first edit,
        // old shared settings retain their appearance on both surfaces.
        return saved.contains("widget_font_scale")
                ? readScale(saved, "widget_font_scale", WIDGET_FONT_SCALES)
                : readScale(saved, "font_scale", APP_FONT_SCALES);
    }
    private static int scaleIndex(float value, float[] scales) {
        for (int i = 0; i < scales.length; i++) if (scales[i] == value) return i;
        return 1;
    }
    public static int appFontIndex(Context context) { return scaleIndex(appFontScale(context), APP_FONT_SCALES); }
    public static int widgetFontIndex(Context context) { return scaleIndex(widgetFontScale(context), WIDGET_FONT_SCALES); }
    public static float fontScale(Context context) { return appFontScale(context); }
    public static int fontIndex(Context context) { return appFontIndex(context); }
    public static boolean setTheme(Context context, String mode) {
        if (!"system".equals(mode) && !"light".equals(mode) && !"dark".equals(mode)) return false;
        return preferences(context).edit().putString("theme", mode).commit();
    }
    public static synchronized boolean setAppFontScale(Context context, float scale) {
        float chosen = canonicalScale(scale, APP_FONT_SCALES);
        if (Float.isNaN(chosen)) return false;
        SharedPreferences saved = preferences(context);
        SharedPreferences.Editor edit = saved.edit();
        // Freeze the previous shared value in the same atomic edit as the new
        // app size. Changing app size can never implicitly resize the widget.
        if (!saved.contains("widget_font_scale"))
            edit.putFloat("widget_font_scale", readScale(saved, "font_scale", APP_FONT_SCALES));
        return edit.putFloat("font_scale", chosen).commit();
    }
    public static synchronized boolean setWidgetFontScale(Context context, float scale) {
        float chosen = canonicalScale(scale, WIDGET_FONT_SCALES);
        return !Float.isNaN(chosen) && preferences(context).edit().putFloat("widget_font_scale", chosen).commit();
    }
    public static boolean setFontScale(Context context, float scale) { return setAppFontScale(context, scale); }
    public static Context wrap(Context base) {
        // Override only appearance, never freeze the device's width, height or orientation.
        // Android recalculates SP conversion, including platform accessibility font scaling.
        Configuration override = new Configuration();
        override.fontScale = base.getResources().getConfiguration().fontScale * appFontScale(base);
        String mode = theme(base);
        if (!"system".equals(mode)) override.uiMode = "dark".equals(mode)
                ? Configuration.UI_MODE_NIGHT_YES : Configuration.UI_MODE_NIGHT_NO;
        return base.createConfigurationContext(override);
    }
    private DisplayPreferences() {}
}
