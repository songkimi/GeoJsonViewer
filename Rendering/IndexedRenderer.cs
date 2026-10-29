namespace GeoJsonViewer.Rendering;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GeoJsonViewer.Logic;
using GeoJsonViewer.Models;

/// <summary>
/// 渲染器：把"一批部件"画进**一个** <see cref="DrawingVisual"/>（② 合并渲染）。
///
/// ── 为什么合并能救缩放 ────────────────────────────────────────────────────
///
/// 旧做法（SimpleRenderer）：每个部件造一个 <c>Polygon</c> / <c>Polyline</c> / <c>Ellipse</c>，
/// 塞进 <c>Canvas.Children</c>。ne_10m 有 8,531 个部件，画布上就是 8,531 个
/// <see cref="UIElement"/>。这笔钱**按控件个数付，不按屏幕上还剩多少点付**：
///
///     每个 Shape → 布局测量（Measure/Arrange）→ 属性系统 → 各自的渲染数据
///                 → 渲染线程逐份三角化 + 光栅化
///
/// 所以 8× 时视口里只剩 14.7 万个点，旧做法照样 1,840 ms —— 8,531 个 Shape 一个都没少。
///
/// 现在：画布上只有 1 个 <see cref="VisualHost"/>，它心里只有 1 张画好的图，
/// 1 份渲染数据。布局测量那一栏消失，光栅化从"8,531 份"变成"1 份"。
///
/// ── 为什么不是"把 8,531 个面的坐标全塞进一条路径" ────────────────────────
///
/// 先试过那种写法（一条 StreamGeometry 装 8,531 个 Figure），结果 1.5× 档要 **7.8 s**：
/// 单个巨大路径在填充时要做全屏的绕数分析，边一多就炸。所以这里改成
/// **每部件一条小几何、但都往同一个 DrawingContext 里画**——
/// 既没有 8,531 个控件，也没有一条病态的大路径。
///
/// 好处不止是快：这样每个部件仍然是"先填自己、再描自己的边"，和旧做法**同一顺序**，
/// 所以画面（含半透明填充的重叠处）与旧实现逐像素一致。
/// </summary>
public static class IndexedRenderer
{
    // 颜色/尺寸必须和 SimpleRenderer（旧工程 D:\AI\My work\GeoJsonViewer\Rendering\SimpleRenderer.cs）
    // 完全一致 —— 画图的行为只允许有一份真相，这里只是"换一种方式把它画出来"。
    private static readonly Color PolygonFillColor = Color.FromArgb(0x55, 0x6C, 0xC9, 0x8A);
    private static readonly Color PolygonStrokeColor = Color.FromRgb(0x6C, 0xC9, 0x8A);
    private static readonly Color LineStrokeColor = Color.FromRgb(0xF2, 0xC0, 0x5C);
    private static readonly Color PointStrokeColor = Color.FromRgb(0x5B, 0x8D, 0xD9);

    /// <summary>
    /// 预抽稀缓存（帧外算一次，帧内只查表）。
    /// 用 static 是因为它只跟"部件 + 容差"有关，和窗口实例无关；部件被换掉时旧条目
    /// 会因为 key 是引用而自然失效（GC 收掉图层，条目跟着没）。
    /// </summary>
    private static readonly LodCache Lod = new();

    /// <summary>
    /// 一个屏幕像素对应多少数据单位。抽稀容差就是它——"删掉之后偏不到 1 px"。
    /// 放大时 PixelsPerDataUnit 变大，容差自动变小、保留的点自动变多。
    /// </summary>
    private static double ToleranceFor(GeoBounds bounds) => 1.0 / bounds.PixelsPerDataUnit;

    /// <summary>渲染。返回画出来的部件数量（和 SimpleRenderer.Render 的返回值语义一致）。</summary>
    public static int Render(
        IEnumerable<GeoPartHit> parts,
        ProjectionSettings settings,
        GeoBounds bounds,
        Canvas target)
    {
        var features = new List<GeoFeature>();
        var seen = new HashSet<GeoFeature>();

        foreach (var hit in parts)
        {
            if (seen.Add(hit.Feature)) features.Add(hit.Feature);
        }

        LodCache.FrameBegin();
        var (visual, drawn) = Build(features, settings, bounds);
        LodCache.FrameEnd();
        target.Children.Add(new VisualHost(visual));
        return drawn;
    }

    /// <summary>
    /// 粗版渲染（实验）：给"拖动中先画一张能看的"用。
    /// </summary>
    public static int RenderCoarse(
        IEnumerable<GeoPartHit> parts,
        ProjectionSettings settings,
        GeoBounds bounds,
        Canvas target,
        double tolerancePx,
        bool drawStrokes)
    {
        var features = new List<GeoFeature>();
        var seen = new HashSet<GeoFeature>();
        foreach (var hit in parts)
        {
            if (seen.Add(hit.Feature)) features.Add(hit.Feature);
        }

        LodCache.FrameBegin();
        var (visual, drawn) = Build(features, settings, bounds, tolerancePx, drawStrokes);
        LodCache.FrameEnd();
        target.Children.Add(new VisualHost(visual));
        return drawn;
    }

    /// <summary>
    /// 合并渲染的对照片：画进 Canvas 的那条老路（每个部件一个 Shape）。
    /// 只给 `_bench\drag-check` / `--bench` 做 A/B 用（环境变量 GJV2_NOMERGE=1 时走这里），
    /// 正常运行不会进来。留着它是为了"同一台机器、同一次运行里"能给出合并前后的对照。
    /// </summary>
    public static int RenderUnmerged(
        IEnumerable<GeoPartHit> parts,
        ProjectionSettings settings,
        GeoBounds bounds,
        Canvas target)
    {
        var features = new List<GeoFeature>();
        var seen = new HashSet<GeoFeature>();
        foreach (var hit in parts)
        {
            if (seen.Add(hit.Feature)) features.Add(hit.Feature);
        }

        return new SimpleRenderer().Render(features, settings, bounds, target);
    }

    /// <summary>
    /// 把一批对象画进一个 <see cref="DrawingVisual"/>。
    ///
    /// 结构上就是旧 <c>SimpleRenderer.Render</c> 的循环，只有一处不同：目标从
    /// <c>Canvas.Children</c> 换成了 <see cref="DrawingContext"/>。图元顺序、闭合规则、
    /// 跳过条件（线少于 2 点、面少于 3 点）全部照抄。
    /// </summary>
    private static (DrawingVisual Visual, int Drawn) Build(
        IReadOnlyList<GeoFeature> features,
        ProjectionSettings settings,
        GeoBounds bounds,
        double tolerancePx = 1.0,
        bool drawStrokes = true)
    {
        var zoom = bounds.Zoom;

        // 一帧只造一次画刷和笔（旧做法是每个 Shape 一份，8,531 份）。
        var fill = new SolidColorBrush(PolygonFillColor);
        var polygonPen = drawStrokes ? MakePen(PolygonStrokeColor, SizeForZoom(1.5, zoom, 0.8, 5.0)) : null;
        var linePen = MakePen(LineStrokeColor, SizeForZoom(1.8, zoom, 1.0, 6.0));
        var pointPen = MakePen(PointStrokeColor, 2);
        var pointRadius = SizeForZoom(5.5, zoom, 3.0, 26.0);
        var tolerance = tolerancePx <= 0 ? ToleranceFor(bounds) : tolerancePx / bounds.PixelsPerDataUnit;

        // Freeze：交给渲染线程后不再变，WPF 可以少做一次克隆，也避免跨线程访问检查。
        fill.Freeze();
        polygonPen?.Freeze();
        linePen.Freeze();
        pointPen.Freeze();

        var drawn = 0;
        var visual = new DrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            foreach (var feature in features)
            {
                var geometry = feature.Geometry;

                // 单部件：Points 就是全部；多部件：每个部件是独立的折线/面，逐个画。
                // 千万别把多部件的点串成一条——那会画出跨部件的假边，图上多出数据里没有的线。
                if (geometry.Parts.Count == 0)
                {
                    DrawPart(geometry.Kind, geometry.Points);
                    continue;
                }

                foreach (var part in geometry.Parts) DrawPart(geometry.Kind, part);
            }

            // 局部函数：和旧 DrawPart 一一对应（同样的判断、同样的笔刷、同样的顺序）。
            void DrawPart(GeometryKind kind, IReadOnlyList<GeoPoint> points)
            {
                switch (kind)
                {
                    case GeometryKind.Point:
                    {
                        if (points.Count == 0) return;      // 没有点，跳过
                        var center = ToScreen(points[0], settings, bounds);
                        dc.DrawEllipse(Brushes.White, pointPen, center, pointRadius, pointRadius);
                        drawn++;
                        return;
                    }

                    case GeometryKind.LineString:
                    {
                        if (points.Count < 2) return;       // 少于两个点画不出线，跳过
                        dc.DrawGeometry(null, linePen, BuildPolyline(points, settings, bounds, tolerance));
                        drawn++;
                        return;
                    }

                    case GeometryKind.Polygon:
                    {
                        if (points.Count < 3) return;       // 少于三个点围不面积，跳过

                        // 填充和描边共用同一条路径 —— 和旧实现里的 Polygon 元素行为一致
                        // （一个 Polygon 就是"填自己 + 描自己的边"两笔，不做拆分）。
                        dc.DrawGeometry(fill, polygonPen, BuildPolygon(points, settings, bounds, tolerance));
                        drawn++;
                        return;
                    }
                }
            }
        }

        return (visual, drawn);
    }

    /// <summary>把一个面的轮廓装进一条小几何（每个面一条，几百到几万个点）。</summary>
    private static StreamGeometry BuildPolygon(
        IReadOnlyList<GeoPoint> points, ProjectionSettings settings, GeoBounds bounds, double tolerance)
    {
        var pts = ToScreenPoints(points, settings, bounds, tolerance);

        // GeoJSON 规定面是闭合的（首尾点相同），但真实数据里确实有没闭合的。
        // 渲染前自己补一个首点，比画出一个缺口的三角形好。
        if (pts[0] != pts[^1]) pts.Add(pts[0]);

        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(pts[0], isFilled: true, isClosed: true);
        for (var i = 1; i < pts.Count; i++) ctx.LineTo(pts[i], isStroked: true, isSmoothJoin: true);
        geometry.Freeze();

        return geometry;
    }

    private static StreamGeometry BuildPolyline(
        IReadOnlyList<GeoPoint> points, ProjectionSettings settings, GeoBounds bounds, double tolerance)
    {
        var pts = ToScreenPoints(points, settings, bounds, tolerance);

        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(pts[0], isFilled: false, isClosed: false);
        for (var i = 1; i < pts.Count; i++) ctx.LineTo(pts[i], isStroked: true, isSmoothJoin: true);
        geometry.Freeze();

        return geometry;
    }

    /// <summary>
    /// 把一个部件映射成屏幕坐标。
    /// 先问 LOD 要"该档该留的点"（帧外算过就只查表），再逐点投影 + 映射。
    /// </summary>
    private static List<Point> ToScreenPoints(
        IReadOnlyList<GeoPoint> points, ProjectionSettings settings, GeoBounds bounds, double tolerance)
    {
        var source = Lod.Simplify(points, tolerance);

        var pts = new List<Point>(source.Count);
        foreach (var p in source) pts.Add(ToScreen(p, settings, bounds));
        return pts;
    }

    /// <summary>
    /// 单点映射：先投影，再映射到屏幕。
    /// 这两步故意不合并成"一步到位"——以后排查"是投影错了还是映射错了"要能分别替换。
    /// </summary>
    private static Point ToScreen(GeoPoint p, ProjectionSettings settings, GeoBounds bounds)
    {
        var (x, y) = Projection.Project(p, settings);
        var (sx, sy) = bounds.ToScreen(x, y);
        return new Point(sx, sy);
    }

    private static Pen MakePen(Color color, double thickness) => new(new SolidColorBrush(color), thickness)
    {
        LineJoin = PenLineJoin.Round,      // 圆角拐点：折线在锐角处不会出现尖刺
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
    };

    /// <summary>
    /// 把"基础尺寸"按当前缩放调整，并夹在上下限之间。
    /// 公式与 SimpleRenderer.SizeForZoom（旧工程 SimpleRenderer.cs:166）必须一字不差。
    /// 上下限是防止缩到最小时线细到消失、放到最大时点占满屏幕。
    /// </summary>
    private static double SizeForZoom(double baseSize, double zoom, double min, double max)
        => Math.Clamp(baseSize * Math.Sqrt(Math.Max(zoom, 0.01)), min, max);
}
