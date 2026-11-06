package app.youmu.daynote;

import android.content.Context;
import android.content.SharedPreferences;
import android.content.res.Configuration;

/** Device-local appearance only. Never included in the calendar or cloud payload. */
public final class DisplayPreferences {
    public static final float[] FONT_SCALES = {.85f, 1f, 1.15f, 1.30f, 1.50f};
    public static final String[] FONT_LABELS = {"较小（85%）", "标准（100%）", "较大（115%）", "大（130%）", "特大（150%）"};
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
    public static float fontScale(Context context) {
        float value = preferences(context).getFloat("font_scale", 1f);
        for (float allowed : FONT_SCALES) if (Math.abs(allowed - value) < .001f) return allowed;
        return 1f;
    }
    public static int fontIndex(Context context) {
        float value = fontScale(context);
        for (int i = 0; i < FONT_SCALES.length; i++) if (FONT_SCALES[i] == value) return i;
        return 1;
    }
    public static boolean setTheme(Context context, String mode) {
        if (!"system".equals(mode) && !"light".equals(mode) && !"dark".equals(mode)) return false;
        return preferences(context).edit().putString("theme", mode).commit();
    }
    public static boolean setFontScale(Context context, float scale) {
        for (float allowed : FONT_SCALES) if (Math.abs(allowed - scale) < .001f)
            return preferences(context).edit().putFloat("font_scale", allowed).commit();
        return false;
    }
    public static Context wrap(Context base) {
        // Override only appearance, never freeze the device's width, height or orientation.
        // Android recalculates SP conversion, including platform accessibility font scaling.
        Configuration override = new Configuration();
        override.fontScale = base.getResources().getConfiguration().fontScale * fontScale(base);
        String mode = theme(base);
        if (!"system".equals(mode)) override.uiMode = "dark".equals(mode)
                ? Configuration.UI_MODE_NIGHT_YES : Configuration.UI_MODE_NIGHT_NO;
        return base.createConfigurationContext(override);
    }
    private DisplayPreferences() {}
}
