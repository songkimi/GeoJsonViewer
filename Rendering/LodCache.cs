namespace GeoJsonViewer.Rendering;

using GeoJsonViewer.Models;

/// <summary>
/// 预抽稀（LOD）缓存：把"抽稀过的一份坐标"按部件记住，下次同档缩放直接取用。
///
/// ── 为什么需要它 ──────────────────────────────────────────────────────────
///
/// 合并渲染（IndexedRenderer）只免掉了"造 8,531 个控件"那笔账，光栅化那笔还在：
/// 1,290,065 个点，1.5× 档光画就要 2.9 秒。抽稀能把点数砍到 2–10%，但那件事本身
/// 全量跑一次要 0.77–1.04 秒（Douglas–Peucker 在每个点上都要算距离）。
///
/// 结论：**抽稀只能在帧外做**。这份缓存就是"帧外"——同一个部件、同一个容差档，
/// 只算一次，之后每帧只是查表。
///
/// ── 容差怎么定（这就是"抽稀的标准"）─────────────────────────────────────
///
/// 标准是**屏幕 1 像素**：一个点删掉之后，折线偏离原路径不到 1 px，肉眼看不见。
/// 屏幕 1 px 换算成数据单位是 <c>1 / GeoBounds.PixelsPerDataUnit</c>（= BaseScale × Zoom）。
/// 所以放大之后容差自动变小、保留的点变多——不会出现"放大之后形状还是粗的"。
///
/// 为了防止缩放时反复重算，容差按 2 的幂**分桶**（1/2、1、2、4… 像素），
/// 取桶时向上取，宁可多留点也不多删点。
/// </summary>
internal sealed class LodCache
{
    /// <summary>小于这个点数的部件不值得抽稀（DP 的开销比省下的光栅化还多）。</summary>
    private const int MinPoints = 200;

    /// <summary>每个部件最多记住几档抽稀结果（缩放来回切换时不必反复重算）。</summary>
    private const int MaxLevelsPerPart = 3;

    private readonly Dictionary<IReadOnlyList<GeoPoint>, List<Entry>> _entries =
        new(ReferenceEqualityComparer.Instance);

    // ── 临时自检（GJV2_LOD_TRACE=1 时才打印）───────────────────────────────
    private static readonly bool Trace = Environment.GetEnvironmentVariable("GJV2_LOD_TRACE") == "1";
    private static int _frameHits, _frameMisses, _frameIn, _frameOut;

    /// <summary>一帧开始时调用（IndexedRenderer.Render 里），用来统计这一帧 LOD 命中了没有。</summary>
    public static void FrameBegin()
    {
        _frameHits = _frameMisses = _frameIn = _frameOut = 0;
    }

    /// <summary>一帧结束时调用；只在 GJV2_LOD_TRACE=1 时打印。</summary>
    public static void FrameEnd()
    {
        if (!Trace) return;
        Console.WriteLine($"      LOD 帧: 命中 {_frameHits} 未命中 {_frameMisses} 点 {_frameIn:N0} → {_frameOut:N0}");
    }

    private sealed record Entry(double Tolerance, IReadOnlyList<GeoPoint> Points);

    /// <summary>
    /// 返回"可以拿去画"的一份坐标。点数少于 <see cref="MinPoints"/> 的部件原样返回。
    ///
    /// <paramref name="toleranceDataUnits"/> 是容差，单位是**数据单位**（经纬度）；
    /// 调用方用 <c>1 / bounds.PixelsPerDataUnit</c> 把"1 屏幕像素"换算过来。
    ///
    /// ── 为什么后面那个判断必须是 <c>==</c>，不能是 <c>&gt;=</c> ─────────────────
    ///
    /// 容差**大 = 粗**（点更少）。若缓存里存的是"更粗"的那份（Tolerance &gt; bucket），
    /// 直接拿来用就等于：这一档明明要 1 px 的精细度，却画了 2 px / 4 px 的粗版本。
    /// 最要命的是**放大**：放大后 bucket 变小，<c>&gt;=</c> 依然成立 —— 于是永远复用
    /// 那份最粗的，放大到 20× 海岸线也还是直的，细节永远回不来。
    ///
    /// 所以这里只认"同一档"。换档就重算一次（每个部件每档只算一次），
    /// 把结果按档存在小列表里，最多留 <see cref="MaxLevelsPerPart"/> 档。
    /// </summary>
    public IReadOnlyList<GeoPoint> Simplify(IReadOnlyList<GeoPoint> part, double toleranceDataUnits)
    {
        if (part.Count < MinPoints) return part;

        // 分桶：2 的幂，向上取（宁可多留点）。
        var bucket = toleranceDataUnits <= 0 ? 1.0 : Math.Pow(2, Math.Ceiling(Math.Log2(toleranceDataUnits)));

        if (_entries.TryGetValue(part, out var levels))
        {
            foreach (var level in levels)
            {
                if (level.Tolerance != bucket) continue;

                _frameHits++;
                _frameIn += part.Count;
                _frameOut += level.Points.Count;
                return level.Points;
            }
        }
        else
        {
            levels = new List<Entry>(2);
            _entries[part] = levels;
        }

        _frameMisses++;
        _frameIn += part.Count;

        var simplified = DouglasPeucker(part, bucket);
        _frameOut += simplified.Count;

        // 超过上限就丢掉"离当前这档最远"的一份，保证内存不随缩放次数无限长。
        if (levels.Count >= MaxLevelsPerPart)
        {
            var worst = 0;
            for (var i = 1; i < levels.Count; i++)
                if (Math.Abs(levels[i].Tolerance - bucket) > Math.Abs(levels[worst].Tolerance - bucket))
                    worst = i;
            levels.RemoveAt(worst);
        }

        levels.Add(new Entry(bucket, simplified));
        return simplified;
    }

    /// <summary>
    /// Douglas–Peucker：先连首末点，找离这条直线最远的点；
    /// 比容差远就保留它、把整段劈成两半递归；不比容差远就把中间的点全丢掉。
    ///
    /// 复杂度：平均 O(n log n)，最坏 O(n²)（数据是一条来回折的锯齿才会踩到最坏）。
    /// </summary>
    private static List<GeoPoint> DouglasPeucker(IReadOnlyList<GeoPoint> points, double tolerance)
    {
        var keep = new bool[points.Count];
        keep[0] = true;
        keep[^1] = true;

        var toleranceSquared = tolerance * tolerance;
        var stack = new Stack<(int First, int Last)>();
        stack.Push((0, points.Count - 1));

        while (stack.Count > 0)
        {
            var (first, last) = stack.Pop();
            if (last - first < 2) continue;

            var maxDistanceSquared = 0.0;
            var maxIndex = -1;

            var a = points[first];
            var b = points[last];

            for (var i = first + 1; i < last; i++)
            {
                var d = DistanceSquaredToSegment(points[i], a, b);
                if (d <= maxDistanceSquared) continue;

                maxDistanceSquared = d;
                maxIndex = i;
            }

            // 注意判断的是"平方"：省掉 100 万次开方，这是这段里最划算的一行优化。
            if (maxDistanceSquared > toleranceSquared)
            {
                keep[maxIndex] = true;
                stack.Push((first, maxIndex));
                stack.Push((maxIndex, last));
            }
        }

        var result = new List<GeoPoint>(points.Count / 4);
        for (var i = 0; i < points.Count; i++)
            if (keep[i]) result.Add(points[i]);

        return result;
    }

    /// <summary>点到线段的距离平方（用经纬度当平面坐标——在单个部件的尺度上这足够准）。</summary>
    private static double DistanceSquaredToSegment(GeoPoint p, GeoPoint a, GeoPoint b)
    {
        var dx = b.Lon - a.Lon;
        var dy = b.Lat - a.Lat;

        if (dx == 0 && dy == 0) return Squared(p.Lon - a.Lon, p.Lat - a.Lat);

        var t = ((p.Lon - a.Lon) * dx + (p.Lat - a.Lat) * dy) / (dx * dx + dy * dy);
        t = Math.Clamp(t, 0, 1);

        return Squared(p.Lon - (a.Lon + t * dx), p.Lat - (a.Lat + t * dy));
    }

    private static double Squared(double x, double y) => x * x + y * y;
}
