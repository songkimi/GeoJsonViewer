namespace GeoJsonViewer.Rendering;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using GeoJsonViewer.Logic;
using GeoJsonViewer.Models;

/// <summary>
/// 把图层画到 Canvas 上。整层重建（先 Clear 再画），不做增量更新。
///
/// 为什么选择"整层重建"而不是增量更新：
///   第一版的数据量只有几万个图元，清空重画只要几毫秒；而增量更新要处理
///   "哪些对象进了视野 / 哪些出视野 / 缩放后哪些要改坐标"，状态一多就必然出错。
///   用最笨的办法先保证"看到的永远是对的"，性能问题留到真的卡了再说。
///
/// 为什么图元的线宽/点半径要跟缩放挂钩：
///   屏幕像素是固定单位，地理跨度不是。1.5px 的线在放大 10 倍后如果跟着坐标一起放大，
///   会变成 15px 的色块；如果完全不放大，缩到最小时又看不见。所以按 zoom 取一个
///   有上下限的中间值（见 SizeForZoom），既跟手又不失控。
/// </summary>
public sealed class SimpleRenderer
{
    /// <summary>渲染。返回画出来的图元数量（用于状态栏）。</summary>
    public int Render(GeoLayer layer, ProjectionSettings settings, GeoBounds bounds, Canvas target)
    {
        var drawn = 0;

        foreach (var feature in layer.Features)
        {
            // 先把这一条几何的所有点投影 + 映射成屏幕坐标。
            // 投影和屏幕映射是两个独立步骤，这里不要合并成一个小函数，
            // 否则以后排查"是投影错了还是映射错了"就得重新拆一遍。
            var pts = new List<Point>(feature.Geometry.Points.Count);
            foreach (var p in feature.Geometry.Points)
            {
                var (x, y) = Projection.Project(p, settings);
                var (sx, sy) = bounds.ToScreen(x, y);
                pts.Add(new Point(sx, sy));
            }

            if (pts.Count == 0) continue;

            switch (feature.Geometry.Kind)
            {
                case GeometryKind.Point:
                    target.Children.Add(CreatePointVisual(pts[0], bounds));
                    drawn++;
                    break;

                case GeometryKind.LineString:
                    if (pts.Count < 2) continue;                 // 少于两个点画不出线，跳过
                    target.Children.Add(CreateLineVisual(pts, bounds));
                    drawn++;
                    break;

                case GeometryKind.Polygon:
                    if (pts.Count < 3) continue;                 // 少于三个点围不面积，跳过
                    target.Children.Add(CreatePolygonVisual(pts, bounds));
                    drawn++;
                    break;
            }
        }

        return drawn;
    }

    // ─────────────────────────────── 图元构造 ───────────────────────────────

    private static Ellipse CreatePointVisual(Point center, GeoBounds bounds)
    {
        var r = SizeForZoom(5.5, bounds.Zoom, 3.0, 26.0);
        var dot = new Ellipse
        {
            Width = r * 2,
            Height = r * 2,
            Fill = Brushes.White,
            Stroke = new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xD9)),
            StrokeThickness = 2,
        };

        // 用 Canvas.Left/Top 定位，比 RenderTransform 更容易在调试器里看懂：
        // 左边距 = 圆心 x 减半径，上边距 = 圆心 y 减半径。
        Canvas.SetLeft(dot, center.X - r);
        Canvas.SetTop(dot, center.Y - r);
        return dot;
    }

    private static Polyline CreateLineVisual(List<Point> pts, GeoBounds bounds) => new()
    {
        Points = new PointCollection(pts),
        Stroke = new SolidColorBrush(Color.FromRgb(0xF2, 0xC0, 0x5C)),
        StrokeThickness = SizeForZoom(1.8, bounds.Zoom, 1.0, 6.0),
        StrokeLineJoin = PenLineJoin.Round,        // 圆角拐点：折线在锐角处不会出现尖刺
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
    };

    private static Polygon CreatePolygonVisual(List<Point> pts, GeoBounds bounds)
    {
        // GeoJSON 规定面是闭合的（首尾点相同），
        // 但真实数据里确实有没闭合的。渲染前自己补一个首点，比画出一个缺口的三角形好。
        var closed = new List<Point>(pts);
        if (closed[0] != closed[^1]) closed.Add(closed[0]);

        return new Polygon
        {
            Points = new PointCollection(closed),
            // 半透明填充：面的边界线和内部的点都要能看见
            Fill = new SolidColorBrush(Color.FromArgb(0x55, 0x6C, 0xC9, 0x8A)),
            Stroke = new SolidColorBrush(Color.FromRgb(0x6C, 0xC9, 0x8A)),
            StrokeThickness = SizeForZoom(1.5, bounds.Zoom, 0.8, 5.0),
            StrokeLineJoin = PenLineJoin.Round,
        };
    }

    /// <summary>
    /// 把"基础尺寸"按当前缩放调整，并夹在上下限之间。
    ///
    /// 为什么用 Math.Sqrt(zoom) 而不是 zoom：直接乘 zoom，放大 20 倍时线宽也变 20 倍，
    /// 画面会糊成一片；开平方之后 20 倍缩放只让尺寸涨 4.5 倍，视觉上"跟手"但还能看清。
    /// 上下限则是防止缩到最小时线细到消失、放到最大时点占满屏幕。
    ///
    /// 这里**只看 Zoom，不看 BaseScale**（fit 比例）：只有一个点的文件会被 fit 出极大的
    /// BaseScale（位置靠 SpanFloor 兜住），若尺寸也乘上它，那个点就会变成占满屏幕的巨点。
    /// 位置归 BaseScale 管，图元尺寸归 Zoom 管，两件事分开。
    /// </summary>
    private static double SizeForZoom(double baseSize, double zoom, double min, double max)
        => Math.Clamp(baseSize * Math.Sqrt(Math.Max(zoom, 0.01)), min, max);
}
