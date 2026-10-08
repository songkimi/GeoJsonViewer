namespace GeoJsonViewer.Diagnostics;

using System.IO;
using System.Windows;
using GeoJsonViewer.Logic;
using GeoJsonViewer.Models;

/// <summary>
/// 无窗口自检：`dotnet run -- --selftest` 或 `GeoJsonViewer.exe --selftest`。
///
/// 为什么要有它：
///   这条流水线（读文件 → 投影 → fit → 映射到屏幕）每一步都可能出错，而"在窗口里目测"
///   是最慢的排错方式——只能看到最终结果，分不清是哪一步坏了。
///   自检把中间结果直接打印出来：
///     · 每个城市的经纬度 → 投影后的平面数值 → 屏幕像素；
///     · 命令输出的顺序就是数据流经的顺序，哪一段不合理一眼能看出来。
/// </summary>
public static class SelfTest
{
    public static int Run(string? samplePath)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== GeoJsonViewer 自检 ===");

        samplePath = ResolveSamplePath(samplePath);
        if (samplePath is null)
        {
            Console.WriteLine("[X] 找不到测试数据 Samples\\beijing-shanghai-guangzhou.geojson");
            return 1;
        }
        Console.WriteLine($"测试数据: {samplePath}");

        // 用固定的虚拟窗口尺寸，保证每次输出可比（真实窗口大小会变，不适合当基准）
        const double viewportW = 1120;
        const double viewportH = 720;

        try
        {
            RunRead(samplePath);
            RunProjectionComparison();
            RunBoundsAndMapping(viewportW, viewportH);
            RunZoomAnchorCheck(viewportW, viewportH);
            RunResizeKeepsScaleCheck(viewportW, viewportH);
            RunHoverHitTest(viewportW, viewportH);
            RunDegenerateInputCheck(viewportW, viewportH);
        }
        catch (NotImplementedException ex)
        {
            Console.WriteLine();
            Console.WriteLine($"[未完成] {ex.Message}");
            Console.WriteLine("这条提示指明了还没实现的功能；补齐后再跑一次自检。");
            return 2;
        }
        catch (Exception ex)
        {
            // 兜底：自检工具本身不该甩一大段堆栈把人吓住，先给一句人话，再给一行技术细节。
            Console.WriteLine();
            Console.WriteLine($"[出错] {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("（自检失败不代表程序坏了，看这条消息定位是哪一段算不通）");
            return 3;
        }

        Console.WriteLine();
        Console.WriteLine("=== 自检结束 ===");
        return 0;
    }

    // ── 第 1 段：把文件读成 GeoLayer─────────────────────────
    private static void RunRead(string path)
    {
        Console.WriteLine();
        Console.WriteLine("[1] 解析 GeoJSON");
        var layer = GeoJsonReader.ReadFile(path);

        Console.WriteLine($"    文件名   : {layer.FileName}");
        Console.WriteLine($"    对象数   : {layer.Features.Count}   （期望 5）");
        Console.WriteLine($"    跳过的   : {layer.SkippedCount}");

        var bounds = layer.LonLatBounds;
        if (bounds is null)
        {
            Console.WriteLine("    经纬度范围: null（图层是空的）");
        }
        else
        {
            var b = bounds.Value;
            Console.WriteLine($"    经纬度范围: 经度 {b.MinLon:0.0000} ~ {b.MaxLon:0.0000}，纬度 {b.MinLat:0.0000} ~ {b.MaxLat:0.0000}");
        }

        foreach (var f in layer.Features)
            Console.WriteLine($"      · {f.DisplayName}  [{f.Geometry.Kind}, {f.Geometry.Points.Count} 点]");
    }

    // ── 第 2 段：两种投影的差别────────────────────────
    private static void RunProjectionComparison()
    {
        Console.WriteLine();
        Console.WriteLine("[2] 投影：同一批经纬度，两种模式的平面数值");
        Console.WriteLine("    预期：纬度越高，墨卡托的 y 比线性的 y 拉得越开（这就是「高纬被压扁」的修正）");

        var samples = new (string Name, GeoPoint P)[]
        {
            ("北京", new GeoPoint(116.4074, 39.9042)),
            ("上海", new GeoPoint(121.4737, 31.2304)),
            ("广州", new GeoPoint(113.2644, 23.1291)),
        };

        Console.WriteLine("    地点      经度        纬度       线性(x, y)                  墨卡托(x, y)");
        foreach (var (name, p) in samples)
        {
            // 两种模式**分别**取数：这样还没实现的那个只会让对应那列显示「未实现」，
            // 不会把已经实现的那列一起拖下水。
            var lin = TryProject(() => Projection.ToLinear(p));
            var mer = TryProject(() => Projection.ToWebMercator(p));
            Console.WriteLine($"    {name,-8}  {p.Lon,9:0.0000}  {p.Lat,9:0.0000}   {FormatProjected(lin)}     {FormatProjected(mer)}");
        }

        // 把"高纬被拉得更开"这个结论直接变成数字，便于核对
        var bj = new GeoPoint(116.4074, 39.9042);
        var gz = new GeoPoint(113.2644, 23.1291);
        try
        {
            double linGap = Math.Abs(Projection.ToLinear(bj).Y - Projection.ToLinear(gz).Y);
            double merGap = Math.Abs(Projection.ToWebMercator(bj).Y - Projection.ToWebMercator(gz).Y);
            Console.WriteLine();
            Console.WriteLine($"    北京↔广州 的 y 距离：线性 {linGap:0.0000}  →  墨卡托 {merGap:0.0000}   （放大 {merGap / linGap:0.000}×）");
            Console.WriteLine("    这个倍数 > 1 说明：线性把北方的南北距离压扁了，墨卡托把它还回来。");
            RunMercatorPoleCheck();
        }
        catch (NotImplementedException)
        {
            Console.WriteLine();
            Console.WriteLine("    （还差一种模式没实现，倍数等两边都能算了再印）");
        }
    }

    /// <summary>
    /// 墨卡托的**极点边界**：纬度 ±90 必须先夹到 ±85.05112877980659，
    /// 投影结果才会正好落在 ±180 —— 也就是让整张投影后的地图成为正方形
    /// （±180 经度 ↔ ±85.05 纬度），在线地图的瓦片都是方的就是这个原因。
    ///
    /// 为什么值得单独断言：Clamp 与 tan 的先后顺序写反时，程序**不会报错**，
    /// 只是 y 变成 179.41（错 0.59）；而正常的测试数据全在中低纬度，
    /// 那个错值根本不会被碰到。这里是唯一能自动抓住它的地方。
    /// </summary>
    private static void RunMercatorPoleCheck()
    {
        const double kMax = Projection.MaxMercatorLatitude;
        const double halfWorld = 180.0;

        var north = Projection.ToWebMercator(new GeoPoint(0.0, 90.0));
        var south = Projection.ToWebMercator(new GeoPoint(0.0, -90.0));
        var edge = Projection.ToWebMercator(new GeoPoint(0.0, kMax));

        bool northOk = Math.Abs(north.Y - halfWorld) < 1e-9;
        bool southOk = Math.Abs(south.Y + halfWorld) < 1e-9;
        bool edgeOk = Math.Abs(edge.Y - halfWorld) < 1e-9;

        Console.WriteLine();
        Console.WriteLine($"    纬度 90 被夹到 ±{kMax} 之后：y = {north.Y:0.00000000}   期望 +{halfWorld:0.00000000}  {(northOk ? "✓" : "✗")}");
        Console.WriteLine($"    纬度 -90 同理                ：y = {south.Y:0.00000000}   期望 -{halfWorld:0.00000000}  {(southOk ? "✓" : "✗")}");
        Console.WriteLine($"    正好取在纬度上限 {kMax} 上  ：y = {edge.Y:0.00000000}   期望 +{halfWorld:0.00000000}  {(edgeOk ? "✓" : "✗")}");

        if (northOk && southOk && edgeOk)
        {
            Console.WriteLine("    结论：夹的是原纬度、tan 用的是夹后的值，顺序没写反；投影后的世界是 ±180 × ±180 的正方形。");
        }
        else
        {
            Console.WriteLine("    结论：极点边界不对。典型错法是先把纬度喂给 tan、再夹结果 —— 那时 tan 已经爆成无穷了，");
            Console.WriteLine("          夹一个无穷大的值没用，y 会停在 179.41 而不是 180。");
        }
    }

    /// <summary>单独取一次投影值；没实现就返回 null，不打断整段自检。</summary>
    private static (double X, double Y)? TryProject(Func<(double X, double Y)> project)
    {
        try { return project(); }
        catch (NotImplementedException) { return null; }
    }

    private static string FormatProjected((double X, double Y)? v) =>
        v is null ? "（未实现）              " : $"({v.Value.X,9:0.0000}, {v.Value.Y,9:0.0000})";

    // ── 第 3 段：fit 与屏幕映射─────────────────────────────────
    private static void RunBoundsAndMapping(double viewportW, double viewportH)
    {
        Console.WriteLine();
        Console.WriteLine("[3] fit 与屏幕映射（虚拟窗口 1120 × 720，四周留 20px）");

        var layer = GeoJsonReader.ReadFile(ResolveSamplePath(null)!);
        var mode = ProjectionMode.Linear;

        var projected = new List<(double X, double Y)>();
        foreach (var p in layer.AllPoints())
            projected.Add(Projection.Project(p, mode));

        var bounds = new GeoBounds(projected);
        bounds.SetViewport(viewportW, viewportH);
        bounds.Reset(viewportW, viewportH);

        Console.WriteLine($"    BaseScale : {bounds.BaseScale:0.0000} 像素/度");
        Console.WriteLine($"    Zoom      : {bounds.Zoom:0.00}×");
        Console.WriteLine();

        var allInView = true;
        foreach (var f in layer.Features)
        {
            if (f.Geometry.Kind != GeometryKind.Point) continue;
            var p = f.Geometry.Points[0];
            var (x, y) = Projection.Project(p, mode);
            var (sx, sy) = bounds.ToScreen(x, y);
            var inside = sx >= 0 && sx <= viewportW && sy >= 0 && sy <= viewportH;
            if (!inside) allInView = false;
            Console.WriteLine($"    {f.DisplayName,-8} 屏幕坐标 ({sx,8:0.0}, {sy,8:0.0})   在窗口内: {(inside ? "是" : "否 ← 错了")}");
        }

        Console.WriteLine();
        Console.WriteLine(allInView
            ? "    结论：所有点都在窗口内 —— fit 成功。以下两点只能人工核对："
            : "    结论：有点跑到窗口外了 —— fit 阶段还没做对。");
        Console.WriteLine("      · 广州在左下、上海在右下、北京在右上（线性模式下北京纬度最高，应该最靠上）");
        Console.WriteLine("      · 屏幕上三点的相对位置要和在线地图一致：不偏、不镜像、不旋转。");

        // 互逆性自查：ToData(ToScreen(p)) 必须还原回 p
        Console.WriteLine();
        var maxError = 0.0;
        foreach (var (x, y) in projected)
        {
            var (sx, sy) = bounds.ToScreen(x, y);
            var (rx, ry) = bounds.ToData(sx, sy);
            maxError = Math.Max(maxError, Math.Max(Math.Abs(rx - x), Math.Abs(ry - y)));
        }
        Console.WriteLine($"    ToData(ToScreen(p)) 还原误差最大 {maxError:E3}（应该接近 0，比如 < 1e-9）");

        RunScreenComparison(viewportW, viewportH);
    }

    /// <summary>
    /// 同一批城市在两种投影下的**屏幕坐标对照**，用来回答那个很自然的问题：
    /// "切到墨卡托后，为什么有的城市看着挪得多、有的几乎不动？"
    ///
    /// 关键是要分清两件事：
    ///   · 城市的**相对关系**（谁离谁多远）——这是投影决定的，必须变；
    ///   · 整张图被 fit 重新居中带来的**整体位移**——这是"重新适配窗口"决定的，每个点都可能有。
    /// 只看某一个点的位移，会把这两件事混在一起，于是得出"上海往中心跑了"这种结论。
    /// 能反映形状的是**两两之间的像素距离**。
    /// </summary>
    private static void RunScreenComparison(double viewportW, double viewportH)
    {
        var layer = GeoJsonReader.ReadFile(ResolveSamplePath(null)!);

        GeoBounds BoundsFor(ProjectionMode mode)
        {
            var pts = new List<(double X, double Y)>();
            foreach (var p in layer.AllPoints()) pts.Add(Projection.Project(p, mode));
            var b = new GeoBounds(pts);
            b.Reset(viewportW, viewportH);
            return b;
        }

        var lin = BoundsFor(ProjectionMode.Linear);
        var mer = BoundsFor(ProjectionMode.WebMercator);

        var names = new[] { "北京", "上海", "广州" };
        var coords = new Dictionary<string, (double Lon, double Lat)>
        {
            ["北京"] = (116.4074, 39.9042),
            ["上海"] = (121.4737, 31.2304),
            ["广州"] = (113.2644, 23.1291),
        };

        Console.WriteLine();
        Console.WriteLine($"    屏幕坐标对照（同一批点、各自 fit 到 {viewportW:0} × {viewportH:0}）");
        Console.WriteLine("      地点      线性屏幕           墨卡托屏幕          y 位移");
        var screen = new Dictionary<string, ((double X, double Y) Lin, (double X, double Y) Mer)>();
        foreach (var n in names)
        {
            var (lon, lat) = coords[n];
            var p = new GeoPoint(lon, lat);
            // 必须各自先过 Projection.Project 再喂 ToScreen。
            //   ToScreen 吃的是**平面数值**，不是经纬度；对线性模式它恰好相等（恒等映射），
            //   所以漏掉这一步时只有墨卡托那列会错：把原经纬度直接喂进墨卡托的 bounds，
            //   "广州"会跑到画布外面（y=722 > 720），三对距离全都变成同一个 0.846×，
            //   看着像"整体缩小"，其实是漏了投影。这个坑很容易踩。
            var (lx, ly) = lin.ToScreen(Projection.Project(p, ProjectionMode.Linear).X, Projection.Project(p, ProjectionMode.Linear).Y);
            var (mx, my) = mer.ToScreen(Projection.Project(p, ProjectionMode.WebMercator).X, Projection.Project(p, ProjectionMode.WebMercator).Y);
            screen[n] = ((lx, ly), (mx, my));
            Console.WriteLine($"      {n,-6}  ({lx,7:0.0}, {ly,7:0.0})   ({mx,7:0.0}, {my,7:0.0})   {my - ly,+7:0.0} 像素");
        }

        Console.WriteLine();
        Console.WriteLine("    两两之间的像素距离及其**比值**：");
        var pairD = new Dictionary<string, (double Lin, double Mer)>();
        foreach (var (a, b) in new[] { ("北京", "广州"), ("北京", "上海"), ("上海", "广州") })
        {
            double linD = Math.Abs(screen[a].Lin.Y - screen[b].Lin.Y);
            double merD = Math.Abs(screen[a].Mer.Y - screen[b].Mer.Y);
            pairD[$"{a}↔{b}"] = (linD, merD);
            Console.WriteLine($"      {a}↔{b} 的竖向像素距离：线性 {linD,7:0.0}  →  墨卡托 {merD,7:0.0}");
        }

        // 为什么看比值、不看绝对值：
        //   两种模式各自 fit 到同一个窗口，等于各自被**重新归一化**了一遍
        //   （墨卡托的总跨度更大，所以它的 BaseScale 更小）。
        //   于是某两个城市之间的像素距离变大还是变小，取决于它占整张图的比例；
        //   只有"北边那一对 ÷ 南边那一对"这种**比值**才不受归一化影响，才真正反映形状。
        double linRatio = pairD["北京↔上海"].Lin / pairD["上海↔广州"].Lin;
        double merRatio = pairD["北京↔上海"].Mer / pairD["上海↔广州"].Mer;
        Console.WriteLine();
        Console.WriteLine($"    北边那一对 ÷ 南边那一对：线性 {linRatio:0.000}  →  墨卡托 {merRatio:0.000}");
        Console.WriteLine($"      这个比值从 {linRatio:0.000} 涨到 {merRatio:0.000}，就是「墨卡托把高纬度的南北距离拉开」的量化体现——");
        Console.WriteLine("      注意它不表现为'所有距离一起变大'：两种模式各自 fit 到同一窗口，整体被重新归一化过，");
        Console.WriteLine("      所以绝对值可增可减，只有**比值**（也就是形状）不受影响。");
        Console.WriteLine("      单个点「看着挪了十几像素」主要是整张图重新居中造成的，不是它自己被拉伸。");
    }

    // ── 第 4 段：缩放锚点不漂────────────────────────
    private static void RunZoomAnchorCheck(double viewportW, double viewportH)
    {
        Console.WriteLine();
        Console.WriteLine("[4] 缩放：以鼠标为锚点，锚点的像素位置应保持不变");

        var layer = GeoJsonReader.ReadFile(ResolveSamplePath(null)!);
        var projected = new List<(double X, double Y)>();
        foreach (var p in layer.AllPoints())
            projected.Add(Projection.Project(p, ProjectionMode.Linear));

        var bounds = new GeoBounds(projected);
        bounds.SetViewport(viewportW, viewportH);
        bounds.Reset(viewportW, viewportH);

        double anchorX = viewportW / 2.0 + 50.0;   // 故意选一个远离视图中心的锚点：
        double anchorY = viewportH / 2.0 - 30.0;   // 锚点在中心的话，就算实现错了也看不出来

        var anchorData = bounds.ToData(anchorX, anchorY);
        double worstScreenDrift = 0.0;
        double worstDataDrift = 0.0;

        // 连续放大 30 格：轮数的积本该是 1.15^30 ≈ 66.2×，远超 20× 上限，
        // 所以只要夹逻辑生效，结果就该正好停在 20.0000×（不生效就一路涨到 66.2×）。
        for (var i = 0; i < 30; i++)
        {
            bounds.ZoomAt(anchorX, anchorY, 1.15);
            worstScreenDrift = Math.Max(worstScreenDrift, AnchorDrift(bounds, anchorX, anchorY));
            worstDataDrift = Math.Max(worstDataDrift, DataDrift(bounds, anchorX, anchorY, anchorData));
        }
        var zoomAfterIn = bounds.Zoom;
        var inExpect = ClampZoom(1.0 * Math.Pow(1.15, 30));

        // 先 Reset 再连续缩小 30 格：理论的积是 1/1.15^30 ≈ 0.0151×，低于 0.2× 下限，
        // 所以应该正好停在下限 0.2× —— 这才是真正在验下限夹。
        bounds.Reset(viewportW, viewportH);
        for (var i = 0; i < 30; i++)
        {
            bounds.ZoomAt(anchorX, anchorY, 1.0 / 1.15);
            worstScreenDrift = Math.Max(worstScreenDrift, AnchorDrift(bounds, anchorX, anchorY));
            worstDataDrift = Math.Max(worstDataDrift, DataDrift(bounds, anchorX, anchorY, anchorData));
        }
        var zoomAfterOut = bounds.Zoom;
        var outExpect = ClampZoom(1.0 / Math.Pow(1.15, 30));

        // Reset → 放大 30 格（确定停在上限）→ 往回滚 4 格。
        // 期望值不是随口写的，就是"过顶后的 Zoom × 1.15^-4"：11.4351×。
        bounds.Reset(viewportW, viewportH);
        for (var i = 0; i < 30; i++) bounds.ZoomAt(anchorX, anchorY, 1.15);
        for (var i = 0; i < 4; i++) bounds.ZoomAt(anchorX, anchorY, 1.0 / 1.15);
        var backFromMax = bounds.Zoom;
        var backExpect = ClampZoom(ClampZoom(1.0 * Math.Pow(1.15, 30)) / Math.Pow(1.15, 4));

        Console.WriteLine($"    放大 30 格：Zoom = {zoomAfterIn:0.0000}×  期望 {inExpect:0.0000}×（轮数连乘是 {Math.Pow(1.15, 30):0.0}×，被上限截住）");
        Console.WriteLine($"    缩小 30 格：Zoom = {zoomAfterOut:0.0000}×  期望 {outExpect:0.0000}×（轮数连乘是 {1.0 / Math.Pow(1.15, 30):0.0000}×，被下限截住）");
        Console.WriteLine($"    顶到上限后往回滚 4 格：Zoom = {backFromMax:0.0000}×  期望 {backExpect:0.0000}×（若停在 {GeoBounds.MaxZoom:0.0}× 就是过顶漂移）");
        Console.WriteLine($"    锚点屏幕位置最大漂移 {worstScreenDrift:E3} 像素（应 < 1e-9）");
        Console.WriteLine($"    锚点数据坐标最大漂移 {worstDataDrift:E3} 度（应 < 1e-9）");

        var maxOk = Math.Abs(zoomAfterIn - inExpect) < 1e-9;
        var minOk = Math.Abs(zoomAfterOut - outExpect) < 1e-9;
        var backOk = Math.Abs(backFromMax - backExpect) < 1e-6;
        var anchorOk = worstScreenDrift < 1e-9;

        if (maxOk && minOk && backOk && anchorOk)
        {
            Console.WriteLine();
            Console.WriteLine("    结论：锚点不漂、上下限都夹住了、过顶后还能正确回调。");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("    结论：有问题 ——");
            if (!maxOk) Console.WriteLine("      · 放大没停在上限：Zoom 的 setter 里有 Clamp，但 ZoomAt 里可能没走它。");
            if (!minOk) Console.WriteLine("      · 缩小没停在下限：同上。");
            if (!backOk) Console.WriteLine("      · 顶到上限后往回滚，Zoom 没按预期下降 —— 典型的「过顶漂移」。");
            if (!anchorOk) Console.WriteLine("      · 锚点漂了：ZoomAt 里反算 pan 的式子和 ToScreen 对不上。");
        }
    }

    // ── [4-B] 拉窗口：比例要冻住，但"从哪儿裁"必须有个明确的定点 ────────────
    /// <summary>
    /// 这一段回答两个问题，它们是不同的事，混在一起容易看错：
    ///
    ///   ① 比例变了吗？—— 拉窗口时最容易犯的错，是把"窗口多大"和"1 度等于多少像素"绑在一起。
    ///      一旦绑上，用户滚到的位置就被悄悄改掉（窗口缩 40%，屏幕上的东西跟着缩 40%）。
    ///      它**不会报任何错**，Zoom 的数值也没变，只有肉眼能看出来，所以必须由自检盯住。
    ///
    ///   ② 从哪儿裁？—— 画布少掉的那几十像素总要裁，得说清从哪里裁。
    ///      GeoBounds 的约定是**左上角钉住**（panDelta = (旧宽-新宽)/2 的直接结果）：
    ///      旧帧左上角 (0,0) 底下压着的那块地方，拉完窗口还得压在 (0,0)。
    ///      所以这里只认一个不变量：**定点底下压着的那块地方（ToData）前后相等**。
    ///      顺带把"画布中心底下压着的地方"也打印出来对照——它是应该跑的，而且跑的量
    ///      正好是"半个尺寸差"，正好能反过来印证 panDelta 那个式子。
    /// </summary>
    private static void RunResizeKeepsScaleCheck(double viewportW, double viewportH)
    {
        Console.WriteLine();
        Console.WriteLine("[4-B] 拉窗口：比例冻住 + 定点不动");

        var layer = GeoJsonReader.ReadFile(ResolveSamplePath(null)!);
        var projected = new List<(double X, double Y)>();
        foreach (var p in layer.AllPoints())
            projected.Add(Projection.Project(p, ProjectionMode.Linear));

        var bounds = new GeoBounds(projected);
        bounds.SetViewport(viewportW, viewportH);
        bounds.Reset(viewportW, viewportH);

        // 模拟窗口被拖小 200×140（和真实拖拽走的是同一条路径：SetViewport，不是 Reset）
        var newW = viewportW - 200;
        var newH = viewportH - 140;

        var failures = new List<string>();

        // 每次重来都 Reset 一次，保证面对的是同一个起始状态
        bounds.Reset(viewportW, viewportH);

        // 左上角 (0,0) 是这套实现的定点（panDelta = (旧宽-新宽)/2 推出来的）。
        // 检查：这个屏幕位置底下压着的那块地方，拉完窗口必须一模一样。
        var (ax, ay) = (0.0, 0.0);

        var scaleBefore = bounds.BaseScale;
        var (dataX, dataY) = bounds.ToData(ax, ay);          // 左上角底下压着的是哪块地方
        var (midDataX, midDataY) = bounds.ToData(viewportW / 2.0, viewportH / 2.0);   // 画布正中那块（供对照）
        var (bx, by) = bounds.ToScreen(116.4074, 39.9042);   // 北京，用来算"两点之间有多少像素"
        var (ox, oy) = bounds.ToScreen(113.2644, 23.1291);   // 广州
        var dSpanBefore = DistanceBetweenPixels(bx, by, ox, oy);

        // 模拟窗口被拖小 200×140（和真实拖拽走的是同一条路径：SetViewport，不是 Reset）
        bounds.SetViewport(newW, newH);

        var (dataX2, dataY2) = bounds.ToData(ax, ay);        // 左上角底下现在压着的是哪块地方
        var (midDataX2, midDataY2) = bounds.ToData(newW / 2.0, newH / 2.0);   // 新的画布正中底下那块
        var (bx2, by2) = bounds.ToScreen(116.4074, 39.9042);
        var (ox2, oy2) = bounds.ToScreen(113.2644, 23.1291);
        var dSpanAfter = DistanceBetweenPixels(bx2, by2, ox2, oy2);

        var scaleFrozen = Math.Abs(bounds.BaseScale - scaleBefore) < 1e-12;
        var spanKept = Math.Abs(dSpanAfter - dSpanBefore) < 1e-9;
        var cornerDataKept = Math.Abs(dataX2 - dataX) < 1e-9 && Math.Abs(dataY2 - dataY) < 1e-9;
        var sizeRecorded = Math.Abs(bounds.ViewportWidth - newW) < 1e-12 && Math.Abs(bounds.ViewportHeight - newH) < 1e-12;

        // 画布正中底下那块地方"应该跑多少"：屏幕参照点从 (W/2,H/2) 换成 (W'/2,H'/2)，
        // 画布小了 200×140，故正中应往左上挪 100×70，而每像素代表 1/k 度（k = Zoom * BaseScale）。
        var k = bounds.Zoom * bounds.BaseScale;
        var expectedMidShiftX = (newW / 2.0 - viewportW / 2.0) / k;   // 负值：数据坐标变小 → 屏幕往左
        var expectedMidShiftY = -(newH / 2.0 - viewportH / 2.0) / k;  // y 轴朝下、数据纬度朝上，故反号
        var midShiftX = midDataX2 - midDataX;
        var midShiftY = midDataY2 - midDataY;
        var midShiftMatches = Math.Abs(midShiftX - expectedMidShiftX) < 1e-9
                           && Math.Abs(midShiftY - expectedMidShiftY) < 1e-9;

        Console.WriteLine();
        Console.WriteLine($"    ---- 画布 {viewportW:0}×{viewportH:0} → {newW:0}×{newH:0} ----");
        Console.WriteLine($"      定点（左上角 ({ax:0}, {ay:0})）底下压着 ({dataX:0.0000}, {dataY:0.0000}) → 拉窗口后压着 " +
                          $"({dataX2:0.0000}, {dataY2:0.0000})  {(cornerDataKept ? "✓ 还是那块地方" : "✗ 换了地方")}");
        Console.WriteLine($"      画布正中底下那块 ({midDataX:0.0000}, {midDataY:0.0000}) → ({midDataX2:0.0000}, {midDataY2:0.0000})" +
                          $"　位移 ({midShiftX:+0.0000;-0.0000}, {midShiftY:+0.0000;-0.0000})" +
                          $"，按公式应等于 ({expectedMidShiftX:+0.0000;-0.0000}, {expectedMidShiftY:+0.0000;-0.0000}) " +
                          $"{(midShiftMatches ? "✓" : "✗")}");
        Console.WriteLine($"      BaseScale {scaleBefore:0.000000} → {bounds.BaseScale:0.000000}  " +
                          $"{(scaleFrozen ? "✓" : "✗")}　「北京↔广州」{dSpanBefore:0.00} → {dSpanAfter:0.00} 像素 {(spanKept ? "✓" : "✗")}");

        if (!cornerDataKept) failures.Add($"定点底下压着的地方变了 ({dataX:0.0000},{dataY:0.0000}) → ({dataX2:0.0000},{dataY2:0.0000})");
        if (!scaleFrozen) failures.Add("BaseScale 变了（说明 SetViewport 里还在算 fit）");
        if (!spanKept) failures.Add("两点之间的像素距离变了（比例被改了）");
        if (!midShiftMatches) failures.Add($"画布正中的位移不符合 panDelta 公式（实测 ({midShiftX:0.0000},{midShiftY:0.0000})）");
        if (!sizeRecorded) failures.Add("画布尺寸没记进去");

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("    结论：BaseScale 冻住了、屏幕比例没变、左上角定点底下压着的地方也没变；");
            Console.WriteLine("      画布正中底下那块则按 (旧宽-新宽)/2 / k 往左上挪——这正是 panDelta 那个式子的来历。");
            Console.WriteLine("      这里的不变量是 ToData，不是「屏幕坐标前后相等」——后者在画布换尺寸时必然变。");
            Console.WriteLine("      （原来这里还试过 Center / TopLeft / BottomRight 三种锚点，但那个参数从没被用过，");
            Console.WriteLine("        三种枚举值算出来是同一个数，等于同一个实现跑三遍，已删。）");
        }
        else
        {
            Console.WriteLine("    结论：有问题 ——");
            foreach (var f in failures) Console.WriteLine($"      · {f}");
        }
    }

    private static double DistanceBetweenPixels(double x1, double y1, double x2, double y2)
        => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));

    // ── 第 5 段：悬停命中────────────────────────────
    /// <summary>
    /// 为什么这一段非有不可：
    ///   命中测试错了，窗口里的表现只有一种——「悬停没反应」。可它至少有四个不同的坏法：
    ///     ① 坐标没投影（GsonReader→Projection 那一步漏了）：线性模式看着正常，墨卡托全错位；
    ///     ② 多边形内外判定的公式符号反了：面只能靠描边命中，内部点不中；
    ///     ③ 只比到端点的距离、没比到线段的距离：长线的中段点不中；
    ///     ④ 容差拿地理单位当像素用：缩放一变手感就变。
    ///   肉眼分不出是哪一种，这一段用"已知答案的几个鼠标位置"把它们逐个钉死。
    ///   两种投影都测，是因为①只在墨卡托下暴露——这也是本项目最阴的一类 bug。
    /// </summary>
    private static void RunHoverHitTest(double viewportW, double viewportH)
    {
        Console.WriteLine();
        Console.WriteLine("[5] 悬停命中：已知答案的鼠标位置应命中对应对象，空旷处返回 null");

        var layer = GeoJsonReader.ReadFile(ResolveSamplePath(null)!);
        var failures = new List<string>();

        foreach (var mode in new[] { ProjectionMode.Linear, ProjectionMode.WebMercator })
        {
            var modeName = mode == ProjectionMode.Linear ? "线性" : "Web 墨卡托";
            Console.WriteLine();
            Console.WriteLine($"    ---- {modeName} ----");

            var projected = new List<(double X, double Y)>();
            foreach (var p in layer.AllPoints())
                projected.Add(Projection.Project(p, mode));

            var bounds = new GeoBounds(projected);
            bounds.SetViewport(viewportW, viewportH);
            bounds.Reset(viewportW, viewportH);

            // ① 命中"北京"：把北京的经纬度走完整的 ToScreen，再拿这个屏幕点去点它
            var beijing = layer.Features.First(f => f.DisplayName == "北京");
            var bp = beijing.Geometry.Points[0];
            var (bpx, bpy) = Projection.Project(bp, mode);
            var (bsx, bsy) = bounds.ToScreen(bpx, bpy);
            var hitAtPoint = HitTester.HitTest(layer, mode, bounds, bsx, bsy);
            var pointOk = hitAtPoint?.DisplayName == "北京";
            if (!pointOk) failures.Add($"{modeName}：点在「北京」上没命中北京（拿到 {hitAtPoint?.DisplayName ?? "null"}）");
            Console.WriteLine($"    鼠标点在「北京」上 → {(hitAtPoint?.DisplayName ?? "null"),-10} {(pointOk ? "✓" : "✗ ← 错了")}");

            // ② 命中"北京—上海"这条线的中段：验证用的是点到线段的距离，不是点到端点
            var line = layer.Features.First(f => f.DisplayName == "北京—上海");
            var mid = new GeoPoint(
                (line.Geometry.Points[0].Lon + line.Geometry.Points[1].Lon) / 2.0,
                (line.Geometry.Points[0].Lat + line.Geometry.Points[1].Lat) / 2.0);
            var (lmx, lmy) = Projection.Project(mid, mode);
            var (lsx, lsy) = bounds.ToScreen(lmx, lmy);
            var hitOnLine = HitTester.HitTest(layer, mode, bounds, lsx, lsy);
            var lineOk = hitOnLine?.DisplayName == "北京—上海";
            if (!lineOk) failures.Add($"{modeName}：点在线段中段没命中这条线（拿到 {hitOnLine?.DisplayName ?? "null"}）");
            Console.WriteLine($"    鼠标落在线段中段   → {(hitOnLine?.DisplayName ?? "null"),-10} {(lineOk ? "✓" : "✗ ← 错了")}");

            // ③ 命中三角形的**内部**（不是描边）：这是"射线法"唯一的检查点。
            //    两个已修过的 bug 都会在这里暴露：坐标没投影（墨卡托下）、交点公式符号反了（两种模式都）。
            //    选点要小心：三角形的重心 (116.6, 39.87) 离北京的屏幕距离只有约 1.3 像素，
            //    "最近优先"生效时它会命中北京——那是**正确**行为，不是 bug。
            //    所以这里用手挑一个"明确在三角形内部、且离三个点和那条线都比较远"的位置：
            //    (116.8, 39.8) 到最近的边约 17.7 像素，到北京约 15.6 像素。
            var tri = layer.Features.First(f => f.Geometry.Kind == GeometryKind.Polygon);
            var interior = new GeoPoint(116.8, 39.8);
            var (cmx, cmy) = Projection.Project(interior, mode);
            var (csx, csy) = bounds.ToScreen(cmx, cmy);
            var hitInPolygon = HitTester.HitTest(layer, mode, bounds, csx, csy);
            var polygonOk = hitInPolygon?.DisplayName == tri.DisplayName;
            if (!polygonOk) failures.Add($"{modeName}：点在三角形内部没命中面（拿到 {hitInPolygon?.DisplayName ?? "null"}）");
            Console.WriteLine($"    鼠标落在三角形内部 → {(hitInPolygon?.DisplayName ?? "null"),-10} {(polygonOk ? "✓" : "✗ ← 错了")}");

            // ④ 离所有对象都远 → 必须返回 null（容差默认 8 像素）。
            //    取视图右下角往里 10 像素：广东那一带的最南点也在 y≈700，所以这个角落离所有对象都超过 8px。
            var hitFar = HitTester.HitTest(layer, mode, bounds, viewportW - 10, viewportH - 10);
            var farOk = hitFar is null;
            if (!farOk) failures.Add($"{modeName}：空旷处（右下角）不该命中，却拿到了 {hitFar!.DisplayName}");
            Console.WriteLine($"    鼠标在右下角空旷处 → {(hitFar?.DisplayName ?? "null"),-10} {(farOk ? "✓" : "✗ ← 错了（容差过大或判定条件错了）")}");
        }

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("    结论：点/线/面在两种投影下都命中正确，空旷处正确返回 null。");
            Console.WriteLine("      （剩下的只有「提示框的位置和外观」，那部分只能靠眼睛看。）");
        }
        else
        {
            Console.WriteLine("    结论：有问题 ——");
            foreach (var f in failures) Console.WriteLine($"      · {f}");
            Console.WriteLine("      排查顺序：① 别忘了 Projection.Project（ToScreen 吃的是平面数值，不是经纬度）；");
            Console.WriteLine("                ② 多边形内外判定是不是拿「平面坐标」和「经纬度顶点」在比较；");
            Console.WriteLine("                ③ 交点横坐标的公式符号；④ tolerance 的单位是不是像素。");
        }
    }

    // ── 第 7 段：退化输入─────────────────────────────────────────
    /// <summary>
    /// 边角用例：空 FeatureCollection、跳过完一个不剩、全是 NaN、只有一个点。
    ///
    /// 为什么值得单独一段：这几个输入**全都能让程序崩**，而且崩法一样——
    /// 「拿 0 个点去 Min/Max」抛 InvalidOperationException: 序列不包含任何元素。
    /// 「不崩溃、显示一行中文提示」这条要求就是靠它守住的。
    ///
    /// 为什么断言写在这里而不是只靠手测：手测要求先**想到**去测这几个文件。
    /// 自检每次都会跑，所以这类"漏了一个空判断"的错误以后再也回不来。
    /// </summary>
    private static void RunDegenerateInputCheck(double viewportW, double viewportH)
    {
        Console.WriteLine();
        Console.WriteLine("[7] 退化输入：空数据 / 全 NaN / 只有一个点");

        var failures = new List<string>();

        // ① 空序列：empty.geojson 走的就是这条路。
        try
        {
            var empty = new GeoBounds(Array.Empty<(double X, double Y)>());
            empty.Reset(viewportW, viewportH);
            var (sx, sy) = empty.ToScreen(0, 0);
            bool ok = double.IsFinite(empty.BaseScale) && double.IsFinite(sx) && double.IsFinite(sy)
                      && Math.Abs(sx - viewportW / 2) < 1e-6 && Math.Abs(sy - viewportH / 2) < 1e-6;
            if (!ok) failures.Add($"空序列：BaseScale/屏幕坐标不对（BaseScale={empty.BaseScale}, ToScreen=({sx:0.0000},{sy:0.0000})）");
            Console.WriteLine($"    空序列构造 GeoBounds → 不再抛异常 {(ok ? "✓" : "✗ ← 错了")}   BaseScale = {empty.BaseScale:0.###e+0}（有限数），ToScreen(0,0) = ({sx:0.0}, {sy:0.0})");
        }
        catch (Exception ex)
        {
            failures.Add($"空序列仍然抛异常：{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"    空序列构造 GeoBounds → ✗ 抛异常：{ex.GetType().Name}: {ex.Message}");
        }

        // ② 全部是 NaN：与空序列同源（过滤完一个有效点都不剩）。
        try
        {
            var nan = new GeoBounds(new (double, double)[] { (double.NaN, double.NaN) });
            nan.Reset(viewportW, viewportH);
            bool ok = double.IsFinite(nan.BaseScale);
            if (!ok) failures.Add($"全 NaN：BaseScale 不是有限数（{nan.BaseScale}）");
            Console.WriteLine($"    全是 NaN 的序列       → {(ok ? "✓" : "✗ ← 错了")}   BaseScale = {nan.BaseScale:0.###e+0}");
        }
        catch (Exception ex)
        {
            failures.Add($"全 NaN 仍然抛异常：{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"    全是 NaN 的序列       → ✗ 抛异常：{ex.GetType().Name}: {ex.Message}");
        }

        // ③ 只有一个点：one-point.geojson。检查的是"落正中 + 是小圆点（半径有限）"。
        //    图元尺寸只由 Zoom 决定（SimpleRenderer 的 SizeForZoom(5.5, bounds.Zoom, 3.0, 26.0)），
        //    从不乘 BaseScale；所以这里只要 BaseScale 有限、位置不跑偏，单点就不会撑满屏幕。
        try
        {
            var one = new GeoBounds(new (double, double)[] { (116.4074, 39.9042) });
            one.Reset(viewportW, viewportH);
            var (sx, sy) = one.ToScreen(116.4074, 39.9042);
            bool ok = double.IsFinite(one.BaseScale)
                      && Math.Abs(sx - viewportW / 2) < 1e-6 && Math.Abs(sy - viewportH / 2) < 1e-6;
            if (!ok) failures.Add($"单个点：没有落在视图正中（ToScreen=({sx:0.0000},{sy:0.0000})）");
            Console.WriteLine($"    只有一个点            → {(ok ? "✓" : "✗ ← 错了")}   点落在视图正中 ({sx:0.0}, {sy:0.0})，BaseScale = {one.BaseScale:0.###e+0}（被 SpanFloor 兜住）");
        }
        catch (Exception ex)
        {
            failures.Add($"单个点抛异常：{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"    只有一个点            → ✗ 抛异常：{ex.GetType().Name}: {ex.Message}");
        }

        // ④ 空 FeatureCollection 的**解析**结果必须是"零个对象"，不是异常。
        //    这两件事要分清楚：空文件 = 解析成功但没东西可画（界面显示一句中文提示）；
        //    坏 JSON = 解析失败（抛 GeoJsonException）。在解析层抛异常就把前者说成了后者。
        try
        {
            var layer = GeoJsonReader.ReadString("{\"type\":\"FeatureCollection\",\"features\":[]}", "empty.geojson");
            bool ok = layer.Features.Count == 0;
            if (!ok) failures.Add($"空 FeatureCollection 应该解析成 0 个对象，实际 {layer.Features.Count}");
            Console.WriteLine($"    空 FeatureCollection  → {(ok ? "✓" : "✗ ← 错了")}   解析成 {layer.Features.Count} 个对象（不是抛异常，界面会显示「这个文件里没有可显示的对象」）");
        }
        catch (Exception ex)
        {
            failures.Add($"空 FeatureCollection 不该抛异常：{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"    空 FeatureCollection  → ✗ 抛了异常：{ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("    结论：空数据不会崩、空文件被正确识别为「没有可显示的对象」。");
            Console.WriteLine("      （剩下的靠手测：界面上那句中文提示、以及提示框出现在画布中央而不是别处。）");
        }
        else
        {
            Console.WriteLine("    结论：有问题 ——");
            foreach (var f in failures) Console.WriteLine($"      · {f}");
            Console.WriteLine("      排查顺序：① GeoBounds.SetDataBounds 是不是先检查「一个有效点都没有」再 Min/Max；");
            Console.WriteLine("                ② 是不是错在解析层抛了异常（那会把「空文件」说成「坏文件」）。");
        }
    }

    /// <summary>复现 GeoBounds.Zoom 属性里的夹，用来算"期望值"。</summary>
    private static double ClampZoom(double zoom) => Math.Clamp(zoom, GeoBounds.MinZoom, GeoBounds.MaxZoom);
    /// <summary>锚点当前应该落在哪个屏幕位置 —— 它偏离锚点多少像素。</summary>
    private static double AnchorDrift(GeoBounds bounds, double anchorX, double anchorY)
    {
        var (dx, dy) = bounds.ToData(anchorX, anchorY);
        var (sx, sy) = bounds.ToScreen(dx, dy);
        return Math.Max(Math.Abs(sx - anchorX), Math.Abs(sy - anchorY));
    }

    /// <summary>锚点对应的数据坐标是否还停在最初那个点上（漂移量，单位：度）。</summary>
    private static double DataDrift(GeoBounds bounds, double anchorX, double anchorY, (double X, double Y) expected)
    {
        var (dx, dy) = bounds.ToData(anchorX, anchorY);
        return Math.Max(Math.Abs(dx - expected.X), Math.Abs(dy - expected.Y));
    }

    private static string? ResolveSamplePath(string? given)
    {
        if (!string.IsNullOrWhiteSpace(given) && File.Exists(given)) return Path.GetFullPath(given);

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Samples", "beijing-shanghai-guangzhou.geojson"),
            Path.Combine(Environment.CurrentDirectory, "Samples", "beijing-shanghai-guangzhou.geojson"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        return null;
    }
}
