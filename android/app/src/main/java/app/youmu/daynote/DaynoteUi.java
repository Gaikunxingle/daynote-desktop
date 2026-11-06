package app.youmu.daynote;

import android.content.Context;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.InsetDrawable;
import android.graphics.drawable.RippleDrawable;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;

final class DaynoteUi {
    final Context context;
    final boolean dark;
    final int background, surface, ink, muted, line, accent, softAccent;
    DaynoteUi(Context context) {
        this.context = context;
        dark = DisplayPreferences.isDark(context);
        background = Color.parseColor(dark ? "#171A1B" : "#FAF8F5");
        surface = Color.parseColor(dark ? "#24282A" : "#FFFFFF");
        ink = Color.parseColor(dark ? "#F2F0EA" : "#292724");
        muted = Color.parseColor(dark ? "#B4B3AB" : "#79756F");
        line = Color.parseColor(dark ? "#383E40" : "#E9E4DC");
        accent = Color.parseColor(dark ? "#F18B77" : "#C84F3B");
        softAccent = Color.parseColor(dark ? "#452D29" : "#FAE9E3");
    }
    int dp(float n) { return Math.round(n * context.getResources().getDisplayMetrics().density); }
    int sp(float n) { return Math.round(android.util.TypedValue.applyDimension(android.util.TypedValue.COMPLEX_UNIT_SP,n,context.getResources().getDisplayMetrics())); }
    TextView text(String value, float size, int color, boolean bold) {
        TextView t = new TextView(context);
        t.setText(value); t.setTextSize(size); t.setTextColor(color);
        t.setFontFeatureSettings("tnum");
        t.setTypeface(Typeface.create("sans-serif", bold ? Typeface.BOLD : Typeface.NORMAL));
        t.setIncludeFontPadding(false);
        return t;
    }
    LinearLayout column() { LinearLayout l = new LinearLayout(context); l.setOrientation(LinearLayout.VERTICAL); return l; }
    LinearLayout row() { LinearLayout l = new LinearLayout(context); l.setGravity(Gravity.CENTER_VERTICAL); return l; }
    GradientDrawable shape(int color, float radius, boolean border) {
        GradientDrawable d = new GradientDrawable(); d.setColor(color); d.setCornerRadius(dp(radius));
        if (border) d.setStroke(dp(1), line);
        return d;
    }
    RippleDrawable ripple(int color, float radius) {
        return new RippleDrawable(ColorStateList.valueOf(dark ? 0x30FFFFFF : 0x19000000), shape(color, radius, false), shape(Color.WHITE, radius, false));
    }
    Button button(String label, boolean primary, Runnable action) {
        Button b = new Button(context);
        b.setText(label); b.setTextSize(14); b.setAllCaps(false); b.setTextColor(primary ? (dark ? Color.BLACK : Color.WHITE) : ink);
        b.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        b.setMinHeight(dp(48)); b.setMinimumHeight(dp(48)); b.setMinWidth(dp(48)); b.setMinimumWidth(dp(48));
        b.setPadding(dp(14), 0, dp(14), 0); b.setBackground(ripple(primary ? accent : surface, 14));
        b.setOnClickListener(v -> action.run());
        return b;
    }
    Button completionButton(boolean completed, int color, Runnable action) {
        Button b = button(completed ? "✓" : "", false, action);
        b.setTextSize(android.util.TypedValue.COMPLEX_UNIT_DIP, 19);
        b.setTextColor(dark ? Color.BLACK : Color.WHITE); b.setPadding(0,0,0,0);
        GradientDrawable circle = new GradientDrawable(); circle.setShape(GradientDrawable.OVAL);
        circle.setColor(completed ? color : Color.TRANSPARENT); circle.setStroke(dp(2),color);
        b.setBackground(new RippleDrawable(ColorStateList.valueOf(dark ? 0x30FFFFFF : 0x19000000),
                new InsetDrawable(circle,dp(11)),shape(Color.WHITE,24,false)));
        if (android.os.Build.VERSION.SDK_INT >= 30) b.setStateDescription(completed ? "已完成" : "未完成");
        return b;
    }
    View space(int height) { View v = new View(context); v.setLayoutParams(new LinearLayout.LayoutParams(1, dp(height))); return v; }
    int color(String id) {
        if (id == null) return Color.parseColor("#6485C2");
        return Color.parseColor(switch (id) {
            case "teal" -> "#4A9C98"; case "purple" -> "#A183BD"; case "orange" -> "#CF9554";
            case "green" -> "#7D9A65"; case "red" -> "#D7776C"; case "pink" -> "#C983A3";
            case "gray" -> "#929693"; default -> "#6485C2";
        });
    }
}
