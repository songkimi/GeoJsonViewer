namespace GeoJsonViewer.Logic;

using System.Windows;
using GeoJsonViewer.Models;

/// <summary>
/// 屏幕坐标 → 最近的对象。
///
/// 为什么命中测试要跟渲染用完全一样的两步走（Projection → GeoBounds.ToScreen）：
///   在数据坐标系里算距离，和在屏幕坐标系里算距离，**结果不一样**。
///   用户看到的、鼠标点的都是屏幕上的图形，所以容差（tolerance）必须以像素为单位，
///   距离也必须在屏幕上量。先 ToScreen 再算，判定结果才和肉眼一致。
///
/// 为什么容差用 fixed 像素（默认 8px）而不是地理单位：
///   用户的操作精度是像素级的——"我点到它附近了"在缩放 20 倍时对应的地理距离，
///   比缩放 0.2 倍时小两个数量级。用像素单位，判定手感在任何缩放下都一致。
///
/// 实现说明（两处容易踩的地方，都是实测踩出来的）：
///   ① 每个顶点都必须走完 Projection.Project(v, settings) → bounds.ToScreen(x, y) 两步。
///      ToScreen 吃的是**平面数值**，不是经纬度；漏掉 Projection 时线性模式碰巧对
///      （线性是恒等映射），墨卡托就整体错位——这是本项目最阴的一类 bug。
///   ② 内外判定（射线法）放在屏幕坐标系里做，不要在数据坐标系里混着比：
///      ToData 的输出是"平面数值"，而多边形的顶点是"经纬度"，
///      两者只有在线性模式下才恰好相等。射线法本身只依赖边的上下次序和交点在
///      鼠标左右哪一侧，所以屏幕坐标系里的等比缩放不改变结论。
/// </summary>
public static class HitTester
{
    /// <summary>
    /// 找离 (screenX, screenY) 最近的对象；超出 tolerance 像素就返回 null。
    /// 点的判定是"距离 ≤ tolerance"；线/面的判定是"到任意一条边的距离 ≤ tolerance"；
    /// 面额外支持"点在里面也算命中"（不然小图还行、大三角形只有一圈细边框能选中）。
    /// </summary>
    public static GeoFeature? HitTest(
        GeoLayer layer, ProjectionSettings settings, GeoBounds bounds,
        double screenX, double screenY, double tolerance = 8)
    {
        GeoFeature? best = null;
        var bestDistance = double.MaxValue;
        var screen = new Point(screenX, screenY);

        foreach (var feature in layer.Features)
        {
            var geometry = feature.Geometry;
            if (geometry.Points.Count == 0) continue;   // 坏数据不该让鼠标一动就炸

            // 把每个顶点投影 → 映射到屏幕。这一步对三种几何都要做，
            // 而且必须放在循环里逐顶点做：投影是非线性的（墨卡托），
            // 不能在经纬度空间里先插值再投影。
            var screenPoints = new List<Point>(geometry.Points.Count);
            foreach (var p in geometry.Points)
            {
                var (px, py) = Projection.Project(p, settings);
                var (sx, sy) = bounds.ToScreen(px, py);
                screenPoints.Add(new Point(sx, sy));
            }

            double? distance = geometry.Kind switch
            {
                GeometryKind.Point => Distance(screen, screenPoints[0]),

                // 线：量到每一条相邻顶点连成的线段，而不是量到顶点本身。
                // 只量顶点的话，一条长线的中段会点不中。
                GeometryKind.LineString => MinDistanceToEdges(screen, screenPoints),

                // 面：先看描边，没命中再看内部。
                // 内部命中时距离记 0，于是它会盖过附近的点和线——这是有意的：
                // 面里头随手点一下就应该选中这个面，而不是被边框旁边的对象抢走。
                // 射线法在屏幕坐标系里判内外，和顶点用的是同一个坐标系，对任何投影都成立。
                GeometryKind.Polygon => IsPointInPolygon(screenPoints, screenX, screenY)
                    ? 0.0
                    : MinDistanceToEdges(screen, screenPoints),

                // 不认识就跳过，和 GeoJsonReader 的宽容策略保持一致
                // （这里不该抛异常：异常留给"整个文件都用不了"的情况）
                _ => null,
            };

            // 取最近的那个：若写成一命中就 return，重叠处会命中"列表里靠前的"而不是"离鼠标最近的"，
            // 表现就是"有时显示的是旁边那个对象"。
            if (distance is { } d && d <= tolerance && d < bestDistance)
            {
                bestDistance = d;
                best = feature;
            }
        }

        return best;
    }

    /// <summary>点到折线所有线段的距离里最小的那个；点数不足 2 时退化成点到点。</summary>
    private static double MinDistanceToEdges(Point p, List<Point> points)
    {
        if (points.Count < 2) return Distance(p, points[points.Count - 1]);

        var min = double.MaxValue;
        for (int i = 0; i < points.Count - 1; i++)
            min = Math.Min(min, DistanceToSegment(p.X, p.Y, points[i], points[i + 1]));

        return min;
    }

    /// <summary>点到线段的距离。线段退化成一点时，就是点到点的距离。</summary>
    private static double DistanceToSegment(double px, double py, Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;

        if (lengthSquared < 1e-12) return Distance(px, py, a.X, a.Y);

        // t 是点在 AB 上的投影参数，夹到 [0,1] 就表示"落在线段内"。
        // 不夹的话算出来的是点到**直线**的距离——线段延长线附近也会被命中，手感就假了。
        var t = ((px - a.X) * dx + (py - a.Y) * dy) / lengthSquared;
        t = Math.Clamp(t, 0, 1);

        return Distance(px, py, a.X + t * dx, a.Y + t * dy);
    }

    private static double Distance(double x1, double y1, double x2, double y2)
        => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));

    private static double Distance(Point a, Point b) => Distance(a.X, a.Y, b.X, b.Y);

    /// <summary>
    /// 判断点是否在多边形内（射线法，不处理内圈/洞的情况）。
    ///
    /// 传进来的顶点必须是**同一个坐标系**里的（本类里是屏幕像素，见 HitTest 的说明）。
    /// 做法：从鼠标向右发一条水平射线，数它穿过多少条边；奇数就在里面。
    ///   为什么用 py &gt; low &amp;&amp; py &lt;= high 这种"左闭右开"：顶点正好落在射线高度上时，
    ///   如果两头都算，相邻两条边会各记一次交点，等于把一次穿越数成两次，判定就反了。
    /// </summary>
    private static bool IsPointInPolygon(IReadOnlyList<Point> polygon, double px, double py)
    {
        int count = polygon.Count;
        if (count < 3) return false;

        var inside = false;
        for (int i = 0; i < count - 1; i++)
        {
            var a = polygon[i];
            var b = polygon[i + 1];

            var low = Math.Min(a.Y, b.Y);
            var high = Math.Max(a.Y, b.Y);
            if (py <= low || py > high) continue;   // 这条边不跨过射线所在的高度

            if (Math.Abs(b.Y - a.Y) < 1e-12) continue;   // 水平边不产生交点（上面那行其实已经挡掉了，留作兜底）

            // 边与射线的交点横坐标：把两个顶点连成直线，解出 y = py 时的 x。
            // 这里写成"从 a 出发按比例走"的形式，比通分后的连乘式少一个数量级的符号坑：
            // 之前用连乘式时把 (x1*y2 - x2*y1) 写成了 (x2*y1 - x1*y2)，符号反了，
            // 于是所有交点都跑到坐标轴的负侧，三角形内部永远判不中（只剩描边能选中）。
            var nodeX = a.X + (b.X - a.X) * (py - a.Y) / (b.Y - a.Y);

            if (nodeX > px) inside = !inside;   // 只数鼠标右侧的交点
        }

        return inside;
    }
}
