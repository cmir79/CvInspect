// 툴바 아이콘(📸⏯✋⛶➕➖🧹)이 흑백으로, 버튼 가운데에, WPF 판과 비슷한 크기로 그려지는지 — 그린 화면의 픽셀로 잰다.
// 결함: 글자(이모지)로 두었을 때 Skia 가 컬러 이모지 글꼴의 컬러 글리프를 작게, 버튼 위쪽에 그렸다(WPF 는 같은 글자를 흑백 기호로
// 가운데 그린다). 아이콘은 이제 벡터 경로이고 상속 글자색으로 칠한다.
// 판정(버튼마다, 테두리 안쪽 3 px 를 뺀 버튼 안쪽에서):
//  - 바탕 = 가장 흔한 색. 잉크 = 바탕과 RGB 거리 24 넘는 화소.
//  - 색: 모든 잉크 화소가 "바탕 → 글자색(템플릿 ContentPresenter 의 Foreground 를 그 알파로 바탕에 얹은 색)" 선분 위에 있다(잔차 ≤ 20) —
//    가장자리 안티에일리어싱은 이 선분 위의 섞임이라 통과하고, 컬러 글리프는 선분을 벗어난다. 가장 진한 화소는 그 선분의 끝 가까이(t ≥ 0.85)이고
//    무채색(최대−최소 채널 ≤ 12)이다. ⏯·눌린 ✋ 는 강조 바탕 위 흰색, 비활성 📸 는 테마의 비활성 글자색이어야 같은 판정을 지난다.
//  - 자리: 잉크 경계 상자 중심이 버튼 중심에서 가로·세로 1.5 px 안.
//  - 크기: 경계 상자 긴 변이 11~18 px(배율 1 기준, 배율만큼 늘린다) — WPF 판 툴바(Segoe UI Symbol) 실측 글리프 상자 가로 12~18·세로 11~17 px.
// 밝은·다크 변형(다크에서 평소 아이콘은 밝은 글자색), 위 도킹과 왼쪽 도킹(세로로 쌓인 버튼) 모두.
// 대조(살아 있음): 옛 방식 그대로 이모지 글자를 넣은 버튼(Windows — Segoe UI Emoji 가 있는 OS)과, 위쪽에 앉힌 주황 원을 넣은 버튼은 이 판정에 걸려야 한다.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using CvInspect.Avalonia.Controls;
using Xunit;
using static CvInspect.Avalonia.Tests.AvaTest;

namespace CvInspect.Avalonia.Tests;

public class CvDispCtrlToolbarIconTests
{
    private static readonly string[] Tips = ["Grab", "Play / Stop", "Pan", "Fit", "Zoom In", "Zoom Out", "Clear"];

    /// <summary>판정 한 번의 결과 — 실패 메시지에 그대로 싣는 한 줄과 가장 진한 화소.</summary>
    private readonly record struct IconProbe(bool Ok, string Report, Color Core);

    private static ContentPresenter? Part(Button b)
        => b.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault(c => c.Name == "PART_ContentPresenter");

    /// <summary>버튼 하나의 아이콘을 잰다 — 판정 규칙은 파일 머리.</summary>
    private static IconProbe Probe(Shot s, Button b, Window win)
    {
        var o = b.TranslatePoint(default, win);
        if (o is null) return new(false, "button is not in the window", default);
        int bx0 = (int)Math.Round(o.Value.X * s.S), by0 = (int)Math.Round(o.Value.Y * s.S);
        int bx1 = (int)Math.Round((o.Value.X + b.Bounds.Width) * s.S), by1 = (int)Math.Round((o.Value.Y + b.Bounds.Height) * s.S);
        int x0 = bx0 + 3, y0 = by0 + 3, x1 = bx1 - 3, y1 = by1 - 3;
        if (x1 <= x0 || y1 <= y0 || x1 > s.W || y1 > s.H) return new(false, $"button rect {bx0},{by0}..{bx1},{by1} is outside the shot", default);

        var hist = new Dictionary<Color, int>();
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                var c = s.At(x, y);
                hist[c] = hist.TryGetValue(c, out var n) ? n + 1 : 1;
            }
        var bg = hist.MaxBy(kv => kv.Value).Key;

        // 글자색 — 템플릿이 실제로 내려보내는 색(테마 상태·PinAccent 반영). 알파·불투명도는 바탕에 얹어 화면 색으로 만든다.
        var fgBrush = Part(b)?.Foreground ?? b.Foreground;
        if (fgBrush is not ISolidColorBrush solid) return new(false, $"the presenter foreground is not a solid brush ({fgBrush?.GetType().Name ?? "null"})", default);
        double a = solid.Color.A / 255.0 * solid.Opacity;
        double fr = bg.R + a * (solid.Color.R - bg.R), fg = bg.G + a * (solid.Color.G - bg.G), fb = bg.B + a * (solid.Color.B - bg.B);
        double dr = fr - bg.R, dg = fg - bg.G, db = fb - bg.B, dd = dr * dr + dg * dg + db * db;
        if (dd < 40 * 40) return new(false, $"text color {solid.Color} barely differs from the background {bg}", default);

        int count = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        double maxRes = 0, coreDist = -1;
        Color core = default;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                var c = s.At(x, y);
                double pr = c.R - bg.R, pg = c.G - bg.G, pb = c.B - bg.B;
                double dist = Math.Sqrt(pr * pr + pg * pg + pb * pb);
                if (dist <= 24) continue;
                count++;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                double t = Math.Clamp((pr * dr + pg * dg + pb * db) / dd, 0, 1);
                double er = pr - t * dr, eg = pg - t * dg, eb = pb - t * db;
                maxRes = Math.Max(maxRes, Math.Sqrt(er * er + eg * eg + eb * eb));
                if (dist > coreDist) { coreDist = dist; core = c; }
            }
        if (count == 0) return new(false, $"no ink on background {bg}", default);

        double coreT = ((core.R - bg.R) * dr + (core.G - bg.G) * dg + (core.B - bg.B) * db) / dd;
        int chroma = Math.Max(core.R, Math.Max(core.G, core.B)) - Math.Min(core.R, Math.Min(core.G, core.B));
        double dx = (minX + maxX + 1) / 2.0 - (bx0 + bx1) / 2.0, dy = (minY + maxY + 1) / 2.0 - (by0 + by1) / 2.0;
        int w = maxX - minX + 1, h = maxY - minY + 1, longSide = Math.Max(w, h);
        bool colorOk = maxRes <= 20 && coreT >= 0.85 && chroma <= 12;
        bool centered = Math.Abs(dx) <= 1.5 && Math.Abs(dy) <= 1.5;
        bool sized = longSide >= 11 * s.S && longSide <= 18 * s.S;
        var rep = $"bg={bg} text={solid.Color}x{a:0.00} ink={count}px core={core} t={coreT:0.00} chroma={chroma} maxResidual={maxRes:0.0} " +
                  $"box={w}x{h} offset=({dx:+0.0;-0.0},{dy:+0.0;-0.0}) [color {(colorOk ? "ok" : "BAD")}, center {(centered ? "ok" : "BAD")}, size {(sized ? "ok" : "BAD")}]";
        return new(colorOk && centered && sized, rep, core);
    }

    /// <summary>툴바 버튼과 같은 크기의 대조 버튼.</summary>
    private static Button ControlButton(object content) => new()
    {
        Content = content,
        Padding = new Thickness(10, 4, 10, 4),
        MinWidth = 44,
        MinHeight = 40,
        Margin = new Thickness(2, 0, 2, 0),
    };

    /// <summary>일곱 버튼 모두 흑백·가운데·WPF 크기다 — 평소, 라이브(초록 바탕)·정지(빨강 바탕)의 ⏯, 눌린 ✋(강조 바탕), 비활성 📸 까지.
    /// 대조로 옛 이모지 글자 버튼과 위에 앉힌 주황 원 버튼은 걸린다.
    /// 다크 변형에서는 평소 아이콘이 테마의 밝은 글자색으로 그려져야 한다(검정 고정이면 어두운 바탕에서 안 읽힌다).</summary>
    [AvaloniaTheory]
    [InlineData(1.0, false)]
    [InlineData(1.5, false)]
    [InlineData(1.0, true)]
    public void ToolbarIconsAreMonochromeCenteredAndWpfSized(double scaling, bool dark)
    {
        var ctrl = new CvDispCtrl { Height = 400 };
        // 다크 변형은 툴바 바탕도 어둡게 준다 — 바탕 고정색(#FAFAFA)은 테마 변형을 따르지 않아, 주지 않으면 다크 테마의 흰 글자가 밝은 바탕에 얹힌다.
        if (dark) ctrl.ToolbarBackground = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        var emojiBtn = ControlButton("🧹");
        emojiBtn.FontFamily = new FontFamily("Segoe UI Emoji, Apple Color Emoji, Noto Color Emoji");
        emojiBtn.FontSize = 16;
        var dotBtn = ControlButton(new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(Color.FromRgb(0xF0, 0x80, 0x10)) });
        dotBtn.VerticalContentAlignment = VerticalAlignment.Top;
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8), Children = { emojiBtn, dotBtn } };
        var host = new StackPanel { Children = { ctrl, controls } };
        var scope = new ThemeVariantScope { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light, Child = host };
        var win = WindowOf(scope, 800, 520, scaling);
        var tag = $"@{scaling}{(dark ? " dark" : "")}";

        IconProbe Snap(Button b)
        {
            Settle(win);
            return Probe(Capture(win), b, win);
        }

        foreach (var tip in Tips)
        {
            var b = FindByTip<Button>(ctrl, tip) ?? FindToggle(ctrl, tip);
            Check(b is not null, $"the '{tip}' toolbar button is found");
            var p = Snap(b!);
            Check(p.Ok, $"{tag}: the '{tip}' icon is monochrome in the button's text color, centered and WPF-sized: {p.Report}");
            if (dark && tip is not "Play / Stop")
                Check(p.Core.R >= 200 && p.Core.G >= 200 && p.Core.B >= 200, $"{tag}: the '{tip}' icon is light on the dark variant ({p.Core})");
        }

        // 강조 바탕 위 흰색 — 템플릿이 정말 흰색을 내려보내는지도 본다(흰색이 아닌데 판정만 통과하면 "글자색을 따른다" 가 공짜다).
        var play = FindToggle(ctrl, "Play / Stop")!;
        Check(ColorOf(Part(play)?.Foreground) == Colors.White, $"the stopped ⏯ draws its icon in white on its accent ({ColorOf(Part(play)?.Foreground)})");
        ctrl.IsRunning = true;
        var live = Snap(play);
        Check(live.Ok && ColorOf(Part(play)?.Foreground) == Colors.White, $"{tag}: the live ⏯ icon is white on its accent: {live.Report}");
        ctrl.IsRunning = false;

        var pan = FindToggle(ctrl, "Pan")!;
        var released = Snap(pan);
        pan.IsChecked = true;
        var pressed = Snap(pan);
        Check(pressed.Ok && ColorOf(Part(pan)?.Foreground) == Colors.White,
            $"{tag}: the pressed ✋ icon is white on its accent: {pressed.Report}");
        // 다크에서는 평소 글자색도 흰색이라 눌림이 색을 바꾸지 않는다 — 이 대조는 밝은 변형에서만
        if (!dark) Check(released.Core != pressed.Core,$"control: releasing and pressing ✋ change the icon color ({released.Core} vs {pressed.Core})");
        pan.IsChecked = false;

        // 비활성 — 테마의 비활성 글자색(템플릿이 :disabled 에서 내려보내는 색)으로 그린다
        var grab = FindButton(ctrl, "Grab")!;
        var enabled = Snap(grab);
        ctrl.GrabCommand = new NoCmd();
        var disabled = Snap(grab);
        Check(!grab.IsEnabled && disabled.Ok, $"{tag}: a disabled 📸 draws its icon in the theme's disabled text color: {disabled.Report}");
        Check(enabled.Core != disabled.Core, $"control: disabling 📸 changes the icon color ({enabled.Core} -> {disabled.Core})");

        // 대조 — 이 판정이 결함을 볼 수 있다는 증거
        var dot = Snap(dotBtn);
        Check(!dot.Ok, $"control: an orange dot placed at the top of a button fails the check: {dot.Report}");
        if (OperatingSystem.IsWindows())
        {
            var emoji = Snap(emojiBtn);
            Check(!emoji.Ok, $"control: the old emoji glyph button (Segoe UI Emoji) fails the check: {emoji.Report}");
        }

        // 세로 도킹 — 버튼이 세로로 쌓여도 아이콘은 버튼 가운데다
        ctrl.GrabCommand = null;
        ctrl.ToolbarDock = Dock.Left;
        foreach (var tip in Tips)
        {
            var b = FindByTip<Button>(ctrl, tip) ?? FindToggle(ctrl, tip);
            var p = Snap(b!);
            Check(p.Ok, $"{tag} docked left: the '{tip}' icon is monochrome, centered and WPF-sized: {p.Report}");
        }
    }

    /// <summary>실행할 수 없는 명령 — 📸 를 회색으로 만든다.</summary>
    private sealed class NoCmd : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => false;
        public void Execute(object? parameter) { }
    }
}
