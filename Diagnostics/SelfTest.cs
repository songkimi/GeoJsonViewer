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

        // 高斯-克吕格那两段会把不符的项数带出来，最后用它决定退出码（0 通过 / 4 有不符）
        var gaussKrugerFailures = 0;
        var gaussKrugerInverseFailures = 0;
        var gaussKrugerScreenFailures = 0;
        var gaussKrugerZoneFailures = 0;
        var datumTransformFailures = 0;
        var datumLayerFailures = 0;
        var zoneChangeFailures = 0;
        var shapefileFailures = 0;
        var layerReaderFailures = 0;
        var prjFailures = 0;
        var prjSuggestionFailures = 0;

        try
        {
            RunRead(samplePath);
            RunProjectionComparison();
            RunBoundsAndMapping(viewportW, viewportH);
            RunZoomAnchorCheck(viewportW, viewportH);
            RunResizeKeepsScaleCheck(viewportW, viewportH);
            RunHoverHitTest(viewportW, viewportH);
            RunDegenerateInputCheck(viewportW, viewportH);
            gaussKrugerFailures = RunGaussKrugerCheck();
            gaussKrugerInverseFailures = RunGaussKrugerInverseCheck();
            gaussKrugerScreenFailures = RunGaussKrugerScreenCheck(viewportW, viewportH);
            gaussKrugerZoneFailures = RunZoneSelectionCheck();
            datumTransformFailures = RunDatumTransformCheck();
            datumLayerFailures = RunDatumLayerCheck(samplePath);
            zoneChangeFailures = RunZoneChangeCheck();
            shapefileFailures = RunShapefileCheck();
            layerReaderFailures = RunLayerReaderCheck(samplePath);
            prjFailures = RunPrjCheck(samplePath);
            prjSuggestionFailures = RunPrjSuggestionCheck();
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
        if (gaussKrugerFailures > 0 || gaussKrugerInverseFailures > 0
            || gaussKrugerScreenFailures > 0 || gaussKrugerZoneFailures > 0
            || datumTransformFailures > 0 || datumLayerFailures > 0 || zoneChangeFailures > 0
            || shapefileFailures > 0 || layerReaderFailures > 0 || prjFailures > 0 || prjSuggestionFailures > 0)
        {
            Console.WriteLine($"=== 自检结束：高斯-克吕格正算 {gaussKrugerFailures} 项不符、反算 {gaussKrugerInverseFailures} 项不符、" +
                              $"接界面 {gaussKrugerScreenFailures} 项不符、选带 {gaussKrugerZoneFailures} 项不符、" +
                              $"七参数 {datumTransformFailures} 项不符、基准图层 {datumLayerFailures} 项不符、" +
                              $"换带 {zoneChangeFailures} 项不符、shapefile {shapefileFailures} 项不符、" +
                              $"读取分派 {layerReaderFailures} 项不符、prj 声明 {prjFailures} 项不符、" +
                              $"基准建议 {prjSuggestionFailures} 项不符 ===");
            return 4;
        }
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
        var settings = ProjectionSettings.Linear;

        var projected = new List<(double X, double Y)>();
        foreach (var p in layer.AllPoints())
            projected.Add(Projection.Project(p, settings));

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
            var (x, y) = Projection.Project(p, settings);
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

        GeoBounds BoundsFor(ProjectionSettings settings)
        {
            var pts = new List<(double X, double Y)>();
            foreach (var p in layer.AllPoints()) pts.Add(Projection.Project(p, settings));
            var b = new GeoBounds(pts);
            b.Reset(viewportW, viewportH);
            return b;
        }

        var lin = BoundsFor(ProjectionSettings.Linear);
        var mer = BoundsFor(ProjectionSettings.WebMercator);

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
            //   看着像"整体缩小"，其实是漏了投影。这一步很容易漏。
            var (lx, ly) = lin.ToScreen(Projection.Project(p, ProjectionSettings.Linear).X, Projection.Project(p, ProjectionSettings.Linear).Y);
            var (mx, my) = mer.ToScreen(Projection.Project(p, ProjectionSettings.WebMercator).X, Projection.Project(p, ProjectionSettings.WebMercator).Y);
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
            projected.Add(Projection.Project(p, ProjectionSettings.Linear));

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
            projected.Add(Projection.Project(p, ProjectionSettings.Linear));

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

        foreach (var settings in new[] { ProjectionSettings.Linear, ProjectionSettings.WebMercator })
        {
            var modeName = settings.DisplayName;
            Console.WriteLine();
            Console.WriteLine($"    ---- {modeName} ----");

            var projected = new List<(double X, double Y)>();
            foreach (var p in layer.AllPoints())
                projected.Add(Projection.Project(p, settings));

            var bounds = new GeoBounds(projected);
            bounds.SetViewport(viewportW, viewportH);
            bounds.Reset(viewportW, viewportH);

            // ① 命中"北京"：把北京的经纬度走完整的 ToScreen，再拿这个屏幕点去点它
            var beijing = layer.Features.First(f => f.DisplayName == "北京");
            var bp = beijing.Geometry.Points[0];
            var (bpx, bpy) = Projection.Project(bp, settings);
            var (bsx, bsy) = bounds.ToScreen(bpx, bpy);
            var hitAtPoint = HitTester.HitTest(layer, settings, bounds, bsx, bsy);
            var pointOk = hitAtPoint?.DisplayName == "北京";
            if (!pointOk) failures.Add($"{modeName}：点在「北京」上没命中北京（拿到 {hitAtPoint?.DisplayName ?? "null"}）");
            Console.WriteLine($"    鼠标点在「北京」上 → {(hitAtPoint?.DisplayName ?? "null"),-10} {(pointOk ? "✓" : "✗ ← 错了")}");

            // ② 命中"北京—上海"这条线的中段：验证用的是点到线段的距离，不是点到端点
            var line = layer.Features.First(f => f.DisplayName == "北京—上海");
            var mid = new GeoPoint(
                (line.Geometry.Points[0].Lon + line.Geometry.Points[1].Lon) / 2.0,
                (line.Geometry.Points[0].Lat + line.Geometry.Points[1].Lat) / 2.0);
            var (lmx, lmy) = Projection.Project(mid, settings);
            var (lsx, lsy) = bounds.ToScreen(lmx, lmy);
            var hitOnLine = HitTester.HitTest(layer, settings, bounds, lsx, lsy);
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
            var (cmx, cmy) = Projection.Project(interior, settings);
            var (csx, csy) = bounds.ToScreen(cmx, cmy);
            var hitInPolygon = HitTester.HitTest(layer, settings, bounds, csx, csy);
            var polygonOk = hitInPolygon?.DisplayName == tri.DisplayName;
            if (!polygonOk) failures.Add($"{modeName}：点在三角形内部没命中面（拿到 {hitInPolygon?.DisplayName ?? "null"}）");
            Console.WriteLine($"    鼠标落在三角形内部 → {(hitInPolygon?.DisplayName ?? "null"),-10} {(polygonOk ? "✓" : "✗ ← 错了")}");

            // ④ 离所有对象都远 → 必须返回 null（容差默认 8 像素）。
            //    取视图右下角往里 10 像素：广东那一带的最南点也在 y≈700，所以这个角落离所有对象都超过 8px。
            var hitFar = HitTester.HitTest(layer, settings, bounds, viewportW - 10, viewportH - 10);
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

    // ── 第 8 段：高斯-克吕格正算────────────────────
    // 期望值不是我手算出来的：先用 Kruger 级数（Karney 2011 的横轴墨卡托精确解）独立算一遍，
    // 再与 6 次级数逐点对比，两套算法相差 < 0.1 毫米才把数字写进来。
    // 这样这里的 ✓ 才真的意味着"算对了"，而不是"和我抄的一样"。
    private static int RunGaussKrugerCheck()
    {
        const double tolerance = 0.01;   // 米。真错了的量级是 500000（忘东偏）或几百米（带号/椭球错），0.01 米既严又不苛刻
        var failures = 0;
        var ellipsoid = Ellipsoid.CGCS2000;

        void Check(string label, double expected, double actual)
        {
            var ok = Math.Abs(expected - actual) <= tolerance;
            if (!ok) failures++;
            var mark = ok ? "✓" : "✗";
            Console.WriteLine($"    {mark} {label,-26} 期望 {expected,15:0.0000}   实得 {actual,15:0.0000}   差 {actual - expected,+12:0.0000}");
        }

        void CheckForward(string label, GeoPoint p, double centralMeridian, double expectedX, double expectedY)
        {
            var (x, y) = GaussKruger.Forward(p, ellipsoid, centralMeridian);
            Check($"{label} x", expectedX, x);
            Check($"{label} y", expectedY, y);
        }

        Console.WriteLine();
        Console.WriteLine("[8] 高斯-克吕格正算（CGCS2000 椭球：a = 6378137，1/f = 298.257222101）");
        Console.WriteLine("    参考值由 Kruger 级数独立算出；容差 0.01 米");

        Console.WriteLine("  -- 子午线弧长（赤道 → 纬度，米）--");
        Check("X(0°)", 0.0, GaussKruger.MeridianArcLength(0.0, ellipsoid));
        Check("X(26.6477°)", 2948595.187752, GaussKruger.MeridianArcLength(26.6477, ellipsoid));
        Check("X(30°)", 3320113.397845, GaussKruger.MeridianArcLength(30.0, ellipsoid));
        Check("X(39.9087°)", 4419391.648361, GaussKruger.MeridianArcLength(39.9087, ellipsoid));

        Console.WriteLine("  -- 分带：经度 → 本带中央经线 --");
        Check("贵阳 6 度带 (106.6302)", 105.0, GaussKruger.CentralMeridian(106.6302, 6));
        Check("贵阳 3 度带 (106.6302)", 108.0, GaussKruger.CentralMeridian(106.6302, 3));
        Check("天安门 6 度带 (116.3975)", 117.0, GaussKruger.CentralMeridian(116.3975, 6));
        Check("天安门 3 度带 (116.3975)", 117.0, GaussKruger.CentralMeridian(116.3975, 3));
        Check("上海 6 度带 (121.4737)", 123.0, GaussKruger.CentralMeridian(121.4737, 6));
        Check("上海 3 度带 (121.4737)", 120.0, GaussKruger.CentralMeridian(121.4737, 3));

        Console.WriteLine("  -- 正算：x 向北、y 向东，含 500 公里东偏 --");
        CheckForward("贵阳 CM105", new GeoPoint(106.6302, 26.6477), 105.0, 2949631.0503, 662319.6801);
        CheckForward("贵阳 CM108", new GeoPoint(106.6302, 26.6477), 108.0, 2949326.4980, 363611.7106);
        CheckForward("天安门 CM117", new GeoPoint(116.3975, 39.9087), 117.0, 4419565.4353, 448481.5746);
        CheckForward("上海 CM123", new GeoPoint(121.4737, 31.2304), 123.0, 3457523.5390, 354575.4792);
        CheckForward("上海 CM120", new GeoPoint(121.4737, 31.2304), 120.0, 3457455.4886, 640412.3150);
        CheckForward("广州 CM114", new GeoPoint(113.2644, 23.1291), 114.0, 2559024.7393, 424654.8930);
        CheckForward("中央经线上 (105°, 30°)", new GeoPoint(105.0, 30.0), 105.0, 3320113.3978, 500000.0);
        CheckForward("赤道上 (105.5°, 0°)", new GeoPoint(105.5, 0.0), 105.0, 0.0, 555660.4566);

        Console.WriteLine();
        if (failures == 0)
        {
            Console.WriteLine("    全部通过。");
        }
        else
        {
            Console.WriteLine($"    有 {failures} 项不符。先看「差」那一列的量级：");
            Console.WriteLine("      差约 500000      → 忘了加东偏（或加成了两次）");
            Console.WriteLine("      差几百米 ~ 几公里 → 中央经线/带号不对，或椭球参数用错");
            Console.WriteLine("      差几毫米以下      → 系数抄错或幂次写错（cos³B 写成 cos3B 就是这个量级）");
        }

        return failures;
    }

    // ── 第 9 段：高斯-克吕格反算────────────────────
    // 反算的靶子分两类，容差故意不同：
    //   ① 往返：拿正算的结果反过来解，用的是全精度数值，容差 1e-9 度（≈0.1 毫米）；
    //   ② 反推 Kruger 独立算出的 (x, y)：那些数字写进第 [8] 段时只留了 4 位小数（0.1 毫米），
    //      所以容差放宽到 1e-7 度（≈1 厘米）——真错了的量级是米甚至几公里，照样抓得住。
    private static int RunGaussKrugerInverseCheck()
    {
        const double roundTripTolerance = 1e-9;   // 度（带内；1e-9 度约 0.1 毫米）
        const double outOfZoneTolerance = 1e-6;  // 度（越出 3 度带；约 10 厘米）
        const double referenceTolerance = 1e-7;   // 度
        var failures = 0;
        var ellipsoid = Ellipsoid.CGCS2000;

        void Report(string label, double maxDiff, double tolerance)
        {
            var ok = maxDiff <= tolerance;
            if (!ok) failures++;
            Console.WriteLine($"    {(ok ? "✓" : "✗")} {label,-30} 最大偏差 {maxDiff,12:0.000000000000} 度   （允许 {tolerance:0.0e+0}）");
        }

        void CheckRoundTrip(string label, GeoPoint p, double centralMeridian, double tolerance)
        {
            var (x, y) = GaussKruger.Forward(p, ellipsoid, centralMeridian);
            var back = GaussKruger.Inverse(x, y, ellipsoid, centralMeridian);
            Report($"{label} 往返", Math.Max(Math.Abs(back.Lon - p.Lon), Math.Abs(back.Lat - p.Lat)), tolerance);
        }

        void CheckReference(string label, double x, double y, double centralMeridian, double expectedLon, double expectedLat)
        {
            var p = GaussKruger.Inverse(x, y, ellipsoid, centralMeridian);
            Report($"{label} 反推", Math.Max(Math.Abs(p.Lon - expectedLon), Math.Abs(p.Lat - expectedLat)), referenceTolerance);
        }

        Console.WriteLine();
        Console.WriteLine("[9] 高斯-克吕格反算（平面坐标 → 经纬度）");

        Console.WriteLine("  -- 往返：正算 → 反算，应回到原点（带内 |l| ≤ 2°，容差 1e-9 度）--");
        CheckRoundTrip("贵阳 (106.6302, 26.6477) CM105", new GeoPoint(106.6302, 26.6477), 105.0, roundTripTolerance);
        CheckRoundTrip("天安门 (116.3975, 39.9087) CM117", new GeoPoint(116.3975, 39.9087), 117.0, roundTripTolerance);
        CheckRoundTrip("上海 (121.4737, 31.2304) CM120", new GeoPoint(121.4737, 31.2304), 120.0, roundTripTolerance);
        CheckRoundTrip("广州 (113.2644, 23.1291) CM114", new GeoPoint(113.2644, 23.1291), 114.0, roundTripTolerance);
        CheckRoundTrip("赤道上 (105.5, 0) CM105", new GeoPoint(105.5, 0.0), 105.0, roundTripTolerance);
        CheckRoundTrip("中央经线上 (117, 30) CM117", new GeoPoint(117.0, 30.0), 117.0, roundTripTolerance);

        Console.WriteLine("  -- 往返：越出 3 度带（|l| ≥ 3°）—— 级数截断使然，容差放宽到 1e-6 度（约 10 厘米）--");
        CheckRoundTrip("带边界 (108.5, 25) CM105", new GeoPoint(108.5, 25.0), 105.0, outOfZoneTolerance);
        CheckRoundTrip("反侧大经差 (102, 10) CM105", new GeoPoint(102.0, 10.0), 105.0, outOfZoneTolerance);
        CheckRoundTrip("高纬大经差 (101.5, 45) CM105", new GeoPoint(101.5, 45.0), 105.0, outOfZoneTolerance);
        CheckRoundTrip("6 度带边缘 (111, 50) CM105", new GeoPoint(111.0, 50.0), 105.0, outOfZoneTolerance);
        Console.WriteLine("    （3 度带的 l 最大只有 1.5°，实际使用永远落在上一组。这一组只是为了看见「越出带外的代价」：");
        Console.WriteLine("      正算/反算级数都只到 l⁵ 项，l 越大、被截掉的 l⁷ 及更高阶项越不可忽略，");
        Console.WriteLine("      往返误差便从 1e-11 度量级涨到 1e-7 度量级 —— 这正是「必须分带」的量化理由。）");

        Console.WriteLine("  -- 反推 Kruger 独立算出的 (x, y)（那些数只留了 4 位小数，容差 1e-7 度）--");
        CheckReference("贵阳 CM105", 2949631.0503, 662319.6801, 105.0, 106.6302, 26.6477);
        CheckReference("贵阳 CM108", 2949326.4980, 363611.7106, 108.0, 106.6302, 26.6477);
        CheckReference("天安门 CM117", 4419565.4353, 448481.5746, 117.0, 116.3975, 39.9087);
        CheckReference("上海 CM123", 3457523.5390, 354575.4792, 123.0, 121.4737, 31.2304);
        CheckReference("上海 CM120", 3457455.4886, 640412.3150, 120.0, 121.4737, 31.2304);
        CheckReference("广州 CM114", 2559024.7393, 424654.8930, 114.0, 113.2644, 23.1291);
        CheckReference("中央经线上 CM105", 3320113.3978, 500000.0, 105.0, 105.0, 30.0);
        CheckReference("赤道上 CM105", 0.0, 555660.4566, 105.0, 105.5, 0.0);

        Console.WriteLine();
        if (failures == 0)
        {
            Console.WriteLine("    结论：带内往返到毫米以下，越出 3 度带后误差按截断阶数增长；也能反推独立算出的平面坐标。");
        }
        else
        {
            Console.WriteLine($"    有 {failures} 项不符。常见原因：");
            Console.WriteLine("      · 纬度迭代没收敛（初值或导数写错）→ 偏差四处均匀、量级在米以上");
            Console.WriteLine("      · 忘减 FalseEasting → 经度整体偏几度");
            Console.WriteLine("      · B、l 忘换回度，或 GeoPoint(经度, 纬度) 两个参数写反 → 经纬度互换");
        }

        return failures;
    }

    // ── 第 10 段：高斯-克吕格接进屏幕流水线（换轴 / 自动选带 / 反算读数）──
    /// <summary>
    /// 这一段验的不是"公式算得对"（那是 [8][9] 的事），而是"高斯投影真的能用"：
    ///   ① 换轴：屏幕的 (水平, 垂直) 必须等于测绘的 (东, 北)，不能把 (北, 东) 直接塞进来；
    ///   ② 自动选带：拿数据经度范围的中点算中央经线；
    ///   ③ 反算读数：点 → 投影 → 屏幕 → 反算回经纬度，这正是状态栏光标读数走的那条路。
    /// 判据都选成"换个错误实现就明显不对"的形式，而不是"和某个常数相等"。
    /// </summary>
    private static int RunGaussKrugerScreenCheck(double viewportW, double viewportH)
    {
        Console.WriteLine();
        Console.WriteLine("[10] 高斯-克吕格接进屏幕流水线（换轴 / 自动选带 / 反算读数）");

        var layer = GeoJsonReader.ReadFile(ResolveSamplePath(null)!);
        var failures = new List<string>();

        void Report(string label, bool ok, string detail)
        {
            if (!ok) failures.Add($"{label}：{detail}");
            Console.WriteLine($"    {label,-24} {(ok ? "✓" : "✗ ← 错了")}   {detail}");
        }

        // ① 自动选带：拿数据经度范围的中点去问 CentralMeridian
        var lonLat = layer.LonLatBounds ?? throw new GeoJsonException("测试数据里没有任何点，无法选带");
        var midLon = (lonLat.MinLon + lonLat.MaxLon) / 2.0;
        var cm6 = GaussKruger.CentralMeridian(midLon, 6);
        var cm3 = GaussKruger.CentralMeridian(midLon, 3);
        Console.WriteLine($"    数据经度 {lonLat.MinLon:0.####} ~ {lonLat.MaxLon:0.####}（跨 {lonLat.MaxLon - lonLat.MinLon:0.###} 度），中点 {midLon:0.####}");
        Console.WriteLine($"      6° 带 → 中央经线 {cm6:0.####}°；3° 带 → 中央经线 {cm3:0.####}°");
        Console.WriteLine("      注意：这份测试数据从北京跨到上海，宽度超过一个 6° 带，边角本来就会变形——");
        Console.WriteLine("      分带只保证「带内」精度，跨带的图要么分幅、要么接受边缘误差。");
        Report("自动选带落在带内", Math.Abs(cm6 - midLon) <= 3.0 && Math.Abs(cm3 - midLon) <= 1.5,
            $"选中经线离数据中点 {Math.Abs(cm6 - midLon):0.###}° / {Math.Abs(cm3 - midLon):0.###}°");

        var gk = ProjectionSettings.GaussKruger(Ellipsoid.CGCS2000, 6, cm6);

        // ② 换轴：屏幕的水平方向应当吃"东"，垂直方向应当吃"北"
        var guiyang = new GeoPoint(106.6302, 26.6477);
        var (northing, easting) = GaussKruger.Forward(guiyang, gk.Ellipsoid, gk.CentralMeridianDegrees);
        var (gx, gy) = Projection.Project(guiyang, gk);
        Report("换轴：水平吃「东」", Math.Abs(gx - easting) < 1e-6, $"Project.X = {gx:0.0000}，东 = {easting:0.0000}");
        Report("换轴：垂直吃「北」", Math.Abs(gy - northing) < 1e-6, $"Project.Y = {gy:0.0000}，北 = {northing:0.0000}");

        // ③ 不转置的独立判据：两个点经度相同、只差 1° 纬度，且都在中央经线上。
        //    竖向差应该是一个纬度的弧长（≈110.9 公里），横向差应该恰好是 0。
        //    要是忘了换轴，这 110.9 公里会跑到横向上去 —— 这个判据不需要任何参考值就能判死。
        var (ax, ay) = Projection.Project(new GeoPoint(cm6, 26.6477), gk);
        var (bx, by) = Projection.Project(new GeoPoint(cm6, 27.6477), gk);
        var dx = Math.Abs(bx - ax);
        var dy = Math.Abs(by - ay);
        Report("南北 1° 落在垂直方向", dx < 1.0 && dy > 100000, $"横向差 {dx:0.0000} 米，竖向差 {dy:0.0} 米（期望 0 / ≈110900）");

        // ④ 整条屏幕流水线：投影 → fit → 屏幕 → 反算回经纬度（状态栏光标读数就是这么算的）
        var projected = new List<(double X, double Y)>();
        foreach (var p in layer.AllPoints()) projected.Add(Projection.Project(p, gk));
        var bounds = new GeoBounds(projected);
        bounds.SetViewport(viewportW, viewportH);
        bounds.Reset(viewportW, viewportH);
        Console.WriteLine($"    高斯模式 fit 之后 BaseScale = {bounds.BaseScale:E3} 像素/米（数据单位从「度」变成了「米」，比例尺跟着变很正常）");

        var allInView = true;
        var worstReadout = 0.0;
        var worstName = "";
        Console.WriteLine("      地点      屏幕坐标              反算回经纬度              误差（度）");
        foreach (var f in layer.Features)
        {
            if (f.Geometry.Kind != GeometryKind.Point) continue;
            var p = f.Geometry.Points[0];
            var (x, y) = Projection.Project(p, gk);
            var (sx, sy) = bounds.ToScreen(x, y);
            if (sx < 0 || sx > viewportW || sy < 0 || sy > viewportH) allInView = false;
            var (rx, ry) = bounds.ToData(sx, sy);
            // 这正是 UpdateCoordinateReadout 走的路：ToData 给的是 (水平, 垂直)，反算要 (北, 东)
            var back = GaussKruger.Inverse(ry, rx, gk.Ellipsoid, gk.CentralMeridianDegrees);
            var err = Math.Max(Math.Abs(back.Lon - p.Lon), Math.Abs(back.Lat - p.Lat));
            if (err > worstReadout) { worstReadout = err; worstName = f.DisplayName; }
            Console.WriteLine($"      {f.DisplayName,-8} ({sx,7:0.0}, {sy,7:0.0})   ({back.Lon:0.######}, {back.Lat:0.######})   {err:E1}");
        }

        Report("高斯模式下 fit 全部可见", allInView, allInView ? "所有点都在窗口内" : "有点跑到窗口外了");
        Report("反算读数往返", worstReadout <= 1e-6, $"最差「{worstName}」{worstReadout:E1} 度（允许 1e-6 ≈ 0.1 米）");

        if (failures.Count == 0)
        {
            Console.WriteLine("    结论：换轴、选带、反算读数三条路都通，高斯-克吕格可以接进界面了。");
        }
        else
        {
            Console.WriteLine($"    有 {failures.Count} 项不符。常见原因：");
            Console.WriteLine("      · 横向/竖向反了（忘了换轴）→ 第 ② ③ 项报错，图会「转置」");
            Console.WriteLine("      · 反算时实参顺序写成 (东, 北) → 往返误差直接到几十度");
            Console.WriteLine("      · 忘了把 settings 里的椭球和中央经线传下去 → 用默认值算出的坐标整体偏几百米");
        }

        return failures.Count;
    }

    // ── 第 11 段：自动选带的适用性判断 ─────────────────────────
    /// <summary>
    /// 分带是"规则"，选完带还要回答一个问题：这份数据装得下吗？
    /// 这段用几个手挑的例子把这个判断钉死，其中包括一份**真的装不下**的数据
    /// （就是 [10] 段用的那份北京—上海，跨 8.2 度，比一个 6 度带还宽）。
    /// </summary>
    private static int RunZoneSelectionCheck()
    {
        Console.WriteLine();
        Console.WriteLine("[11] 自动选带：选出来的带装得下这份数据吗");

        var failures = new List<string>();

        void Report(string label, bool actual, bool expected, string detail)
        {
            var ok = actual == expected;
            if (!ok) failures.Add($"{label}：期望 {(expected ? "装得下" : "装不下")}，实得 {(actual ? "装得下" : "装不下")}");
            Console.WriteLine($"    {label,-34} {(ok ? "✓" : "✗ ← 错了")}   {detail}");
        }

        // ① 单点：只要它离中央经线不超过半个带宽
        Report("单点 贵阳(106.6302) / CM105 / 6°",
            GaussKruger.FitsInZone(106.6302, 106.6302, 105, 6), true,
            "经差 1.6302° ≤ 3°");

        // ② 一个小范围：两端都在带内
        Report("贵阳市域(106.0~107.5) / CM106.5 / 3°",
            GaussKruger.FitsInZone(106.0, 107.5, 106.5, 3), true,
            "最大经差 1.0° ≤ 1.5°");

        // ③ 3 度带的极限：上海离 CM120 是 1.4737°，刚好还在带内
        Report("单点 上海(121.4737) / CM120 / 3°",
            GaussKruger.FitsInZone(121.4737, 121.4737, 120, 3), true,
            "经差 1.4737° ≤ 1.5°（已经贴着带边了）");

        // ④ 真的装不下：测试数据从北京跨到上海，一个 6 度带罩不住
        Report("北京—上海(113.2644~121.4737) / CM117 / 6°",
            GaussKruger.FitsInZone(113.2644, 121.4737, 117, 6), false,
            "最大经差 4.4737° > 3°，装不下");

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("    结论：适用性判断正确。装不下的时候，程序要做的不是拒绝这份数据，");
            Console.WriteLine("      而是**照样画、但把代价说清楚**（状态栏里注明这一带罩不住、边角会有变形）——");
            Console.WriteLine("      用户手上有这份数据，比一个「不支持」的弹窗有用得多。");
        }
        else
        {
            Console.WriteLine($"    有 {failures.Count} 项不符。检查：比的是两端与中央经线的经差，判据是 带宽/2；");
            Console.WriteLine("      6 度带是 ±3°、3 度带是 ±1.5°；别忘了取绝对值（西边那一端是负数）。");
        }

        return failures.Count;
    }

    /// <summary>
    /// 第 12 段：七参数基准转换（大地坐标 ↔ 三维直角坐标、布尔沙-沃尔夫变换）。
    ///
    /// 为什么这段能做到"不靠外部数据也能验"：
    ///   ① 正算有三个能用公式直接推出来的锚点（赤道上 X = a、北极上 Z = 短半轴 b、赤道抬高 1000 米 X 就多 1000 米）；
    ///   ② 反算有往返；
    ///   ③ 七参数的七个参数可以拆成"只平移""只尺度""只绕 Z 转"三个单项，每一项都能手算。
    /// 剩下的整条链，再跟另一套独立实现算出的参考值比一次。
    /// 最后两条顺便把量级讲清楚：1 角秒 ≈ 30.9 米、1 ppm ≈ 6.4 米，以及 WGS84 与 CGCS2000 的椭球几乎是一回事。
    /// </summary>
    private static int RunDatumTransformCheck()
    {
        Console.WriteLine();
        Console.WriteLine("[12] 七参数基准转换：大地坐标 ↔ 三维直角坐标 ↔ 布尔沙-沃尔夫");

        var failures = new List<string>();

        void ReportValue(string label, double expected, double actual, double tolerance, string unit)
        {
            var diff = Math.Abs(actual - expected);
            var ok = diff <= tolerance;
            if (!ok) failures.Add($"{label}：期望 {expected:0.######}，实得 {actual:0.######}（差 {diff:E3} {unit}）");
            Console.WriteLine($"    {label,-26} {(ok ? "✓" : "✗ ← 错了")}  期望 {expected,16:0.######}  实得 {actual,16:0.######}  差 {diff,10:E3} {unit}");
        }

        void ReportMax(string label, double maxDiff, double tolerance, string unit, string detail)
        {
            var ok = maxDiff <= tolerance;
            if (!ok) failures.Add($"{label}：最大偏差 {maxDiff:E3} {unit}，允许 {tolerance:E1} {unit}");
            Console.WriteLine($"    {label,-26} {(ok ? "✓" : "✗ ← 错了")}  最大偏差 {maxDiff,10:E3} {unit}  （允许 {tolerance:E1} {unit}）  {detail}");
        }

        void ReportTrue(string label, bool ok, string detail)
        {
            if (!ok) failures.Add(label);
            Console.WriteLine($"    {label,-26} {(ok ? "✓" : "✗ ← 错了")}  {detail}");
        }

        var cgcs = Ellipsoid.CGCS2000;

        // ① 正算：赤道、北极、赤道抬高。期望值全部由公式直接写死，不需要任何外部参考值。
        Console.WriteLine("  -- 正算的锚点：闭式就能写出期望值，不依赖任何外部参考 --");
        var equator = DatumTransform.BlhToXyz(new GeoPoint(0, 0), 0, cgcs);
        ReportValue("赤道 X = a", cgcs.A, equator.X, 1e-6, "米");
        ReportValue("赤道 Y", 0, equator.Y, 1e-6, "米");
        ReportValue("赤道 Z", 0, equator.Z, 1e-6, "米");
        var pole = DatumTransform.BlhToXyz(new GeoPoint(0, 90), 0, cgcs);
        ReportValue("北极 X", 0, pole.X, 1e-6, "米");
        ReportValue("北极 Z = 短半轴 b", cgcs.B, pole.Z, 1e-6, "米");
        var raised = DatumTransform.BlhToXyz(new GeoPoint(0, 0), 1000, cgcs);
        ReportValue("赤道 + 1000 米 X", cgcs.A + 1000, raised.X, 1e-6, "米");

        // ② 反算：往返。经纬度和大地高都要能回来。
        Console.WriteLine("  -- 反算：往返应回到原点 --");
        var roundTrips = new (string Label, GeoPoint Point, double H)[]
        {
            ("天安门", new GeoPoint(116.3975, 39.9087), 50),
            ("高纬", new GeoPoint(135.0, 60.0), -20),
            ("南半球", new GeoPoint(-60.0, -35.0), 3000),
        };
        foreach (var (label, point, h) in roundTrips)
        {
            var xyz = DatumTransform.BlhToXyz(point, h, cgcs);
            var (back, _) = DatumTransform.XyzToBlh(xyz, cgcs);
            var diff = Math.Max(Math.Abs(back.Lon - point.Lon), Math.Abs(back.Lat - point.Lat));
            ReportMax($"{label} 往返（经纬度）", diff, 1e-9, "度", "1e-9 度 ≈ 0.1 毫米");
        }
        var (_, backHeight) = DatumTransform.XyzToBlh(
            DatumTransform.BlhToXyz(new GeoPoint(116.3975, 39.9087), 50, cgcs), cgcs);
        ReportMax("天安门 往返（大地高）", Math.Abs(backHeight - 50), 1e-4, "米", "大地高也要能回来");

        // ②-b 极点：p = 0 时 H = p/cosB − N 这条式子会变成 0/0 的近似，必须单独走一条路
        //     （纬度直接由 Z 的符号定，大地高用 |Z| − b）。
        //     这一条补上的是自检原先没有覆盖到的那一行（极点附近 H 的公式退化）。
        //     外面包一层 try：边界输入往往就是"会抛异常"的那种输入，
        //       如果不兜住，一条坏输入会把后面所有检查都掐断，反而看不出全貌。
        try
        {
            var poleXyz = new GeoXyz(0, 0, cgcs.B + 120.0);
            var (poleBack, poleHeight) = DatumTransform.XyzToBlh(poleXyz, cgcs);
            ReportValue("极点 纬度", 90.0, poleBack.Lat, 1e-9, "度");
            ReportValue("极点 大地高", 120.0, poleHeight, 1e-4, "米");
        }
        catch (Exception ex)
        {
            ReportTrue("极点：边界输入不该抛异常", false, $"抛了 {ex.GetType().Name}：{ex.Message}");
        }

        // ③ 正算与独立实现的参考值比（参考值由另一套实现算出，不是本程序自己算的）。
        Console.WriteLine("  -- 与独立算出的参考值比 --");
        var tiananmen = DatumTransform.BlhToXyz(new GeoPoint(116.3975, 39.9087), 50, cgcs);
        ReportValue("天安门 X", -2178190.067145, tiananmen.X, 1e-4, "米");
        ReportValue("天安门 Y", 4388416.213766, tiananmen.Y, 1e-4, "米");
        ReportValue("天安门 Z", 4070246.776925, tiananmen.Z, 1e-4, "米");

        // ④ 七参数：拆成三个单项，每一项都能手算。单位换算错了这里一定当场露馅。
        Console.WriteLine("  -- 七参数：拆成三个单项，每一项都能手算 --");
        var source = DatumTransform.BlhToXyz(new GeoPoint(116.3975, 39.9087), 50, Ellipsoid.Beijing54);
        var moveOnly = DatumTransform.ApplySevenParameters(source, new DatumParameters("只平移", -110, -41, -24, 0, 0, 0, 0));
        ReportValue("只平移 X_t", source.X - 110, moveOnly.X, 1e-9, "米");
        ReportValue("只平移 Y_t", source.Y - 41, moveOnly.Y, 1e-9, "米");
        ReportValue("只平移 Z_t", source.Z - 24, moveOnly.Z, 1e-9, "米");

        var scaleOnly = DatumTransform.ApplySevenParameters(source, new DatumParameters("只尺度", 0, 0, 0, 0, 0, 0, 3.5));
        var scale = 1.0 + 3.5e-6;
        ReportValue("只尺度 X_t", scale * source.X, scaleOnly.X, 1e-6, "米");
        ReportValue("只尺度 Y_t", scale * source.Y, scaleOnly.Y, 1e-6, "米");

        var rotateOnly = DatumTransform.ApplySevenParameters(source, new DatumParameters("只绕 Z 转 2 角秒", 0, 0, 0, 0, 0, 2.0, 0));
        var rz = 2.0 * Math.PI / (180.0 * 3600.0);
        ReportValue("只绕 Z：X_t = X − rz·Y", source.X - rz * source.Y, rotateOnly.X, 1e-6, "米");
        ReportValue("只绕 Z：Y_t = Y + rz·X", source.Y + rz * source.X, rotateOnly.Y, 1e-6, "米");
        ReportValue("只绕 Z：Z_t 不变", source.Z, rotateOnly.Z, 1e-6, "米");

        // ⑤ 整条链 + 两个边界：参数全 0 必须恒等；只换椭球不给参数则一定会挪。
        Console.WriteLine("  -- 整条链：北京54 → CGCS2000（示例七参数），外加两个边界 --");
        var (target, targetHeight) = DatumTransform.Transform(
            new GeoPoint(116.3975, 39.9087), 50, Ellipsoid.Beijing54, cgcs, DatumParameters.Beijing54ToCgcs2000Example);
        ReportValue("转换后 经度", 116.3998229575, target.Lon, 1e-8, "度");
        ReportValue("转换后 纬度", 39.9087123871, target.Lat, 1e-8, "度");
        ReportValue("转换后 大地高", 175.435628, targetHeight, 1e-3, "米");
        var eastShift = (target.Lon - 116.3975) * 111320 * Math.Cos(39.9087 * Math.PI / 180);
        Console.WriteLine($"      换了基准之后，同一点往东挪了约 {eastShift:0.0} 米、大地高从 50 米变成 {targetHeight:0.0} 米 —— 都不是 bug");

        var (identityPoint, identityHeight) = DatumTransform.Transform(
            new GeoPoint(116.3975, 39.9087), 50, cgcs, cgcs, DatumParameters.Identity);
        ReportMax("参数全 0 时恒等（经纬度）", Math.Max(Math.Abs(identityPoint.Lon - 116.3975), Math.Abs(identityPoint.Lat - 39.9087)), 1e-12, "度", "恒等变换必须一点不动");
        ReportMax("参数全 0 时恒等（大地高）", Math.Abs(identityHeight - 50), 1e-6, "米", "整条链本身必须是透明的");

        // 顺手把"只换椭球"的账算清楚：同一个经纬度挂在两套椭球上，三维上差约 109 米，
        // 但这 109 米是**径向**的（大地高 50 米 → 159 米），经纬度才挪 2.7 米。
        // 要让位置在地面上挪上百米，靠的是两套基准的地心差与轴向差，也就是七参数。
        var (onlyEllipsoid, onlyEllipsoidHeight) = DatumTransform.Transform(
            new GeoPoint(116.3975, 39.9087), 50, Ellipsoid.Beijing54, cgcs, DatumParameters.Identity);
        var ellipsoidShift = Math.Max(
            Math.Abs(onlyEllipsoid.Lon - 116.3975) * 111320 * Math.Cos(39.9087 * Math.PI / 180),
            Math.Abs(onlyEllipsoid.Lat - 39.9087) * 111320);
        ReportMax("只换椭球 → 平面位移", ellipsoidShift, 5.0, "米", "平面上只有几米");
        ReportMax("只换椭球 → 大地高变化", Math.Abs(onlyEllipsoidHeight - 159.1109), 1e-3, "米", "50 米 → 159 米：椭球差几乎全在高度上");
        ReportTrue("要挪上百米必须给七参数", Math.Abs(eastShift) > 100, $"示例七参数把同一点向东挪了 {eastShift:0.0} 米");

        // ⑥ 量级：把参数换算成米，才知道它们到底多大。
        Console.WriteLine("  -- 量级：把参数换算成米，才知道它们到底多大 --");
        ReportValue("1 ppm 在赤道 ≈", 6.3781, cgcs.A * 1e-6, 1e-3, "米");
        ReportValue("1 角秒 在赤道 ≈", 30.9221, cgcs.A * Math.PI / (180.0 * 3600.0), 1e-3, "米");
        ReportValue("CGCS2000 与 WGS84 短半轴之差 ≈", 1.0482e-4, Math.Abs(cgcs.B - Ellipsoid.WGS84.B), 1e-7, "米");
        Console.WriteLine("      所以：角秒→弧度、ppm→无量纲，漏一次就差 30 米或 6 米量级；漏两次就是几万公里。");

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("    结论：正算锚点、反算往返、七参数三项分解与整条链全部对上。");
            Console.WriteLine("      最后三条量级顺便说明了两件事：① 旋转参数写成角秒、尺度写成 ppm，进公式前必须换单位；");
            Console.WriteLine("      ② CGCS2000 与 WGS84 的短半轴只差 0.1 毫米 —— 这两套椭球几乎是一回事，");
            Console.WriteLine("      国内拿 WGS84 的经纬度当 CGCS2000 用，差的是厘米级的框架与板块运动，不是椭球形状。");
            Console.WriteLine("      而北京54 / 西安80 是另一个故事：只换椭球能动的几乎只有大地高（50 米 → 159 米），");
            Console.WriteLine("      要让位置在平面上挪上百米，必须给出七参数。");
        }
        else
        {
            Console.WriteLine($"    有 {failures.Count} 项不符。按这个顺序查（每一层都对答案，错误会锁在一层里）：");
            Console.WriteLine("      ① 正算锚点就错 → 看 N 的公式，以及 Z 项里的 N·(1 − e²)；");
            Console.WriteLine("      ② 锚点对、往返错 → 看迭代的收敛判据，以及极点附近的处理；");
            Console.WriteLine("      ③ 往返对、单项错 → 看角秒→弧度、ppm→无量纲的换算，以及 (1+m) 乘在哪个位置；");
            Console.WriteLine("      ④ 单项对、整条链错 → 看 Transform 里进 XYZ 用哪个椭球、出 BLH 用哪个椭球。");
        }

        return failures.Count;
    }

    /// <summary>
    /// [13] 基准转换到图层。与 [12] 的分工：[12] 管「一个点算得对不对」，
    /// 这里管「一整层对象穿过转换之后，结构、属性、方向、退化输入还对不对」。
    /// </summary>
    private static int RunDatumLayerCheck(string samplePath)
    {
        Console.WriteLine();
        Console.WriteLine("[13] 基准转换到图层：TransformLayer（源基准 → CGCS2000）");

        var failures = new List<string>();

        void ReportValue(string label, double expected, double actual, double tolerance, string unit)
        {
            var diff = Math.Abs(actual - expected);
            var ok = diff <= tolerance;
            if (!ok) failures.Add($"{label}：期望 {expected:0.######}，实得 {actual:0.######}（差 {diff:E3} {unit}）");
            Console.WriteLine($"    {label,-30} {(ok ? "✓" : "✗ ← 错了")}  期望 {expected,16:0.######}  实得 {actual,16:0.######}  差 {diff,10:E3} {unit}");
        }

        void ReportTrue(string label, bool ok, string detail)
        {
            if (!ok) failures.Add(label);
            Console.WriteLine($"    {label,-30} {(ok ? "✓" : "✗ ← 错了")}  {detail}");
        }

        var raw = GeoJsonReader.ReadFile(samplePath);
        var cgcs = DatumCatalog.Cgcs2000;

        // ① 结构必须保住：转换的是坐标，不是数据本身。
        Console.WriteLine("  -- 走过一趟转换之后，除了坐标，别的都该原样还在 --");
        var moved = DatumTransform.TransformLayer(raw, DatumCatalog.Beijing54, cgcs, 0);
        var firstRaw = raw.Features[0];
        var firstMoved = moved.Features[0];
        ReportTrue("对象数不变", moved.Features.Count == raw.Features.Count, $"{raw.Features.Count} → {moved.Features.Count}");
        ReportTrue("跳过数不变", moved.SkippedCount == raw.SkippedCount, $"跳过了 {moved.SkippedCount} 个");
        ReportTrue("文件名不变", moved.FileName == raw.FileName, $"「{moved.FileName}」");
        ReportTrue("名字不变", firstMoved.DisplayName == firstRaw.DisplayName, $"「{firstMoved.DisplayName}」");
        ReportTrue("属性不变", (firstMoved.GetProperty("name") ?? "") == (firstRaw.GetProperty("name") ?? ""),
            $"name = 「{firstMoved.GetProperty("name")}」");
        ReportTrue("点的个数不变（含面的闭合环）",
            moved.Features.Zip(raw.Features).All(pair => pair.First.Geometry.Points.Count == pair.Second.Geometry.Points.Count),
            "线的每个顶点、面的首尾闭合点都要跟着走");

        // ② 逐点一致性：图层里的点必须等于单独调 Transform 的结果（Transform 已由 [12] 的独立参考值验证过）。
        Console.WriteLine("  -- 逐点一致性：图层里的每个点 = 单独调 Transform 的结果 --");
        var single = DatumTransform.Transform(firstRaw.Geometry.Points[0], 0,
            DatumCatalog.Beijing54.Ellipsoid, cgcs.Ellipsoid, DatumCatalog.Beijing54.ToCgcs2000);
        ReportValue("第 1 点 经度", single.Point.Lon, firstMoved.Geometry.Points[0].Lon, 1e-12, "度");
        ReportValue("第 1 点 纬度", single.Point.Lat, firstMoved.Geometry.Points[0].Lat, 1e-12, "度");

        // ③ 量级：北京54 → CGCS2000 该把点挪上百米（只换椭球只有几米，见 [12] 的 2.667 米）。
        var shiftEast = (firstMoved.Geometry.Points[0].Lon - firstRaw.Geometry.Points[0].Lon) * 111320 * Math.Cos(firstRaw.Geometry.Points[0].Lat * Math.PI / 180);
        var shiftNorth = (firstMoved.Geometry.Points[0].Lat - firstRaw.Geometry.Points[0].Lat) * 111320;
        var shiftTotal = Math.Sqrt(shiftEast * shiftEast + shiftNorth * shiftNorth);
        ReportTrue("北京54 → CGCS2000 挪了上百米", shiftTotal is > 100 and < 400,
            $"东 {shiftEast:0.0} 米、北 {shiftNorth:0.0} 米（合计 {shiftTotal:0.0} 米）");

        // 非幂等演示（界面依赖这一点）：TransformLayer 是纯函数，但**不幂等** ——
        //   在"已经转换过"的图层上再按同一个源基准转一次，等于把偏移量又叠加一遍。
        //   所以界面必须永远从原始图层出发；写成 _layer = TransformLayer(_layer, …) 的话，
        //   下拉框来回切几次，数据就会越走越远（而且还看起来"很正常"）。
        var twice = DatumTransform.TransformLayer(moved, DatumCatalog.Beijing54, cgcs, 0);
        var secondShift = (twice.Features[0].Geometry.Points[0].Lon - firstMoved.Geometry.Points[0].Lon)
                          * 111320 * Math.Cos(firstRaw.Geometry.Points[0].Lat * Math.PI / 180);
        ReportTrue("在结果上再转一次 = 偏移量叠加", Math.Abs(secondShift - shiftEast) < 0.05,
            $"第二次又挪了 {secondShift:0.0} 米（第一次 {shiftEast:0.0} 米）→ 界面必须从原始图层出发");

        // 界面序列模拟：界面里"切源基准"=「重读下拉框 → 重新算一份显示图层」。
        //   这里把两种写法的 5 个来回都走一遍：
        //     正确（界面实际写法，输入永远是 _rawLayer）→ 来回切多少次都回到原样，误差恒为 0；
        //     错误（把上一次的结果当输入）→ 每切一次北京54 就多挪一份，5 个来回累积近 1 公里。
        //   为什么要算这个数：它给出"如果界面接错了，肉眼在 20 倍放大下该看到多大偏差"。
        GeoLayer? uiLayer = raw;       // 界面里的 _layer 的替身
        GeoLayer? wrongLayer = raw;    // 反例的替身
        for (var round = 0; round < 5; round++)
        {
            uiLayer = DatumTransform.TransformLayer(raw, DatumCatalog.Beijing54, cgcs, 0);
            uiLayer = DatumTransform.TransformLayer(raw, cgcs, cgcs, 0);

            wrongLayer = DatumTransform.TransformLayer(wrongLayer, DatumCatalog.Beijing54, cgcs, 0);
            wrongLayer = DatumTransform.TransformLayer(wrongLayer, cgcs, cgcs, 0);
        }

        ReportValue("界面序列：切 5 个来回后回到原样", 0, Math.Max(
            Math.Abs(uiLayer.Features[0].Geometry.Points[0].Lon - firstRaw.Geometry.Points[0].Lon),
            Math.Abs(uiLayer.Features[0].Geometry.Points[0].Lat - firstRaw.Geometry.Points[0].Lat)), 0, "度");

        var accumulatedEast = (wrongLayer.Features[0].Geometry.Points[0].Lon - firstRaw.Geometry.Points[0].Lon)
                              * 111320 * Math.Cos(firstRaw.Geometry.Points[0].Lat * Math.PI / 180);
        // 5 个来回 = 5 次「切到北京54」，每次都叠加一份，所以累积应当接近 5 × 第一次的位移。
        // 末尾换算成像素只是给个体感刻度：本项目最大 20 倍放大时 1 像素 ≈ 0.00126 度经差
        // （≈ 108 米），所以 1 公里在最大放大下也就几个像素 —— 这就是"界面接错了也难看出来"的原因。
        const double degreesPerPixelAtMaxZoom = 0.00126;
        var accumulatedDegrees = (wrongLayer.Features[0].Geometry.Points[0].Lon - firstRaw.Geometry.Points[0].Lon);
        ReportTrue("对照：接错的写法 5 个来回累积近 1 公里", Math.Abs(accumulatedEast - 5 * shiftEast) < 1.0,
            $"累积东移 {accumulatedEast:0.0} 米 / {accumulatedDegrees:0.00000} 度 ≈ "
            + $"{Math.Abs(accumulatedDegrees) / degreesPerPixelAtMaxZoom:0.0} 像素（20 倍放大）");

        // ④ 恒等与近恒等：同一个基准一点不动；WGS84 → CGCS2000 只差一丁点（两套椭球短半轴差 0.1 毫米）。
        Console.WriteLine("  -- 四个方向：恒等 / 正向 / 反向 / 经 CGCS2000 中转 --");
        var same = DatumTransform.TransformLayer(raw, cgcs, cgcs, 0);
        ReportValue("同一基准 → 一点不动", 0, Math.Max(
            Math.Abs(same.Features[0].Geometry.Points[0].Lon - firstRaw.Geometry.Points[0].Lon),
            Math.Abs(same.Features[0].Geometry.Points[0].Lat - firstRaw.Geometry.Points[0].Lat)), 1e-12, "度");

        var wgs = DatumTransform.TransformLayer(raw, DatumCatalog.Wgs84, cgcs, 0);
        // 容差 1e-8 度（约 1 毫米）：WGS84 与 CGCS2000 的短半轴差 0.1 毫米，换椭球本身就会挪一点点经纬度。
        ReportValue("WGS84 → CGCS2000 几乎不动", 0, Math.Max(
            Math.Abs(wgs.Features[0].Geometry.Points[0].Lon - firstRaw.Geometry.Points[0].Lon),
            Math.Abs(wgs.Features[0].Geometry.Points[0].Lat - firstRaw.Geometry.Points[0].Lat)), 1e-8, "度");

        var back = DatumTransform.TransformLayer(moved, cgcs, DatumCatalog.Beijing54, 0);
        // 容差 1e-6 度（约 10 厘米）：反向是"七个参数取负"的**近似**，再叠上两套椭球来回换，
        // 毫米级的残差是数学本身的，不是实现错了。（要它严格回到原位，只有逆矩阵求解一条路。）
        ReportValue("CGCS2000 → 北京54 回到原点", 0, Math.Max(
            Math.Abs(back.Features[0].Geometry.Points[0].Lon - firstRaw.Geometry.Points[0].Lon),
            Math.Abs(back.Features[0].Geometry.Points[0].Lat - firstRaw.Geometry.Points[0].Lat)), 1e-6, "度");

        var xian = DatumTransform.TransformLayer(raw, DatumCatalog.Beijing54, DatumCatalog.Xian80, 0);
        var (midPoint, midHeight) = DatumTransform.Transform(firstRaw.Geometry.Points[0], 0,
            DatumCatalog.Beijing54.Ellipsoid, cgcs.Ellipsoid, DatumCatalog.Beijing54.ToCgcs2000);
        var xianParams = DatumCatalog.Xian80.ToCgcs2000;
        var (manualPoint, _) = DatumTransform.Transform(midPoint, midHeight,
            cgcs.Ellipsoid, DatumCatalog.Xian80.Ellipsoid,
            new DatumParameters("西安80 → CGCS2000（反向）",
                -xianParams.Dx, -xianParams.Dy, -xianParams.Dz,
                -xianParams.Rx, -xianParams.Ry, -xianParams.Rz, -xianParams.ScalePpm));
        // 这一条原来只比经度：于是"2.6 米的纬度错误"躲在后面没被发现，红灯却是因为 0.4 毫米的经度差亮的。
        //   经纬度对同一处错误的敏感度差得很远，只挑一个比等于赌运气，两轴各比一次才叫判据。
        // 容差 1e-9 度（≈0.1 毫米）还隐含一个**约定**：中转时第一腿算出的中间大地高要带进第二腿
        //   （第二腿继续用 0 会差约 1.7 毫米）；这个约定同时写在 TransformLayer 的注释里。
        ReportValue("北京54 → 西安80 中转：经度", manualPoint.Lon, xian.Features[0].Geometry.Points[0].Lon, 1e-9, "度");
        ReportValue("北京54 → 西安80 中转：纬度", manualPoint.Lat, xian.Features[0].Geometry.Points[0].Lat, 1e-9, "度");

        // ⑤ 退化输入：空图层不崩、也不该凭空造出对象。
        var empty = new GeoLayer { FileName = "(空图层)", Features = Array.Empty<GeoFeature>() };
        var emptyMoved = DatumTransform.TransformLayer(empty, DatumCatalog.Beijing54, cgcs, 0);
        ReportTrue("空图层：0 个对象、不抛异常",
            emptyMoved.Features.Count == 0 && emptyMoved.FileName == "(空图层)", "退化输入走的是同一条路");

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("    结论：一层的对象穿过基准转换之后，坐标动了、名字/属性/点数/跳过数一个没少；");
            Console.WriteLine("      而且四个方向（恒等 / 正向 / 反向 / 经 CGCS2000 中转）都对得上。");
        }
        else
        {
            Console.WriteLine($"    有 {failures.Count} 项不符。按这个顺序查：");
            Console.WriteLine("      ① 结构类不符（对象数/属性/点数）→ 组装新 GeoLayer 时漏了字段，或者把点列表共享出去了；");
            Console.WriteLine("      ② 逐点一致性不符 → 遍历时用错了椭球/参数，或者只算了一半的点；");
            Console.WriteLine("      ③ 反向往返不符 → 看「source 是 CGCS2000」那条分支有没有把七个参数取负；");
            Console.WriteLine("      ④ 中转不符 → 两个都不是 CGCS2000 时，必须先换到 CGCS2000 再换到目标（不能跳过中间）。");
        }

        return failures.Count;
    }

    /// <summary>换带（ChangeZone）与带号前缀（StripZonePrefix）。</summary>
    private static int RunZoneChangeCheck()
    {
        Console.WriteLine();
        Console.WriteLine("[14] 换带：把坐标从一条中央经线搬到另一条");
        var failures = new List<string>();

        void ReportValue(string label, double expected, double actual, double tolerance, string unit)
        {
            var diff = Math.Abs(expected - actual);
            var ok = diff <= tolerance;
            if (!ok) failures.Add(label);
            Console.WriteLine($"    {(ok ? "✓" : "✗")} {label}：期望 {expected:0.#######} {unit}，实得 {actual:0.#######} {unit}，差 {diff:E2}（允许 {tolerance:E2}）");
        }

        void ReportTrue(string label, bool ok, string detail)
        {
            if (!ok) failures.Add(label);
            Console.WriteLine($"    {(ok ? "✓" : "✗")} {label} —— {detail}");
        }

        var ellipsoid = Ellipsoid.CGCS2000;
        // 贵阳附近的一个点：在 105° 带里 l = +1.63°（带内），在 111° 带里 l = −4.37°（出带）。
        var guiyang = new GeoPoint { Lon = 106.6302, Lat = 26.6477 };
        var (x105, y105) = GaussKruger.Forward(guiyang, ellipsoid, 105.0);

        // ① 定义：换带 = 反算（源带）+ 正算（目标带）；和手工两步必须逐位一致。
        var viaManual = GaussKruger.Inverse(x105, y105, ellipsoid, 105.0);
        var (mx, my) = GaussKruger.Forward(viaManual, ellipsoid, 111.0);
        var (cx, cy) = GaussKruger.ChangeZone(x105, y105, ellipsoid, 105.0, 111.0);
        ReportValue("换带 = 手工「反算 + 正算」：x", mx, cx, 1e-9, "米");
        ReportValue("换带 = 手工「反算 + 正算」：y", my, cy, 1e-9, "米");

        // ② 换过去再换回来 = 回到原点。这里两头都跨了带（−4.37°），精度由级数截断决定，所以容差比带内宽松。
        var (bx, by) = GaussKruger.ChangeZone(cx, cy, ellipsoid, 111.0, 105.0);
        ReportValue("105° 带 → 111° 带 → 换回 105° 带：x", x105, bx, 1e-2, "米");
        ReportValue("……：y", y105, by, 1e-2, "米");

        // ③ 量级自证：相邻带的同名点差几百公里 —— 这就是「跨带必须换带」的量化理由。
        var dy = Math.Abs(cy - y105);
        ReportTrue("相邻带同一点 y 差 500~700 公里", dy > 500_000 && dy < 700_000,
            $"实得 {dy / 1000:0.0} 公里（6° 带、纬度 26.6°）");

        // ④ 不同的路，同一个终点：先把同一个物理点写成 3° 带（108°）的坐标，再换到 6° 带（111°），
        //    结果必须和「105° 带 → 111° 带」一致 —— 两条路都经过同一个经纬度。
        var (x108, y108) = GaussKruger.Forward(guiyang, ellipsoid, 108.0);
        var (via3, vy3) = GaussKruger.ChangeZone(x108, y108, ellipsoid, 108.0, 111.0);
        // 容差 1e-4 米（0.1 毫米）：两条路各做一次带内反算，差异只来自级数截断的尾项。
        ReportValue("3° 带（108°）→ 6° 带（111°）与另一条路一致：x", cx, via3, 1e-4, "米");
        ReportValue("……：y", cy, vy3, 1e-4, "米");

        // ⑤ 带号前缀：20500000 是「20 带 + 东偏 500 km」的国家标准写法。
        ReportValue("剥掉带号前缀：20500000（20 带）→", 500_000.0,
            GaussKruger.StripZonePrefix(20_500_000.0, 20), 1e-6, "米");
        ReportTrue("带号前缀不剥时的错误量级", Math.Abs(20_500_000.0 - GaussKruger.FalseEasting) > 10_000_000,
            $"{Math.Abs(20_500_000.0 - GaussKruger.FalseEasting) / 1000:0} 公里（≈2000 公里）");

        // ⑥ 补上去盖住"自检碰不到的那一行"：ChangeZone 里「y ≥ 1e6 就自动剥前缀」的分支。
        //    不喂一个带前缀的 y 进来，那个 if 永远走不到 —— 上面 ①②④ 用的 y 都只有几十万。
        //    18 带（中央经线 105°）的国家标准写法：y = 18×1e6 + 662319.6800742（同一个贵阳点）。
        //    （第一版这条我写错了：x 取 105° 带、y 却用了 111° 带的东偏，两个带混在一起，
        //      等于喂进去一个离中央经线约 10° 的点 —— 红灯亮得对，是判据本身错了。）
        var prefixedY = 18_000_000.0 + y105;
        var (prefixedX, prefixedYResult) = GaussKruger.ChangeZone(x105, prefixedY, ellipsoid, 105.0, 111.0);
        //    容差 1e-8 米（10 纳米）而不是 1e-9：带号前缀把数值抬到 1.86×10⁷，
        //    双精度在那个量级的最小刻度约 4×10⁻⁹ 米，剥回来必然损失几个纳米 ——
        //    这是"带号前缀"这种**表示法**的代价，不是实现错误。（实测残差 1.46 纳米。）
        ReportValue("带前缀的 y（18 带写法）与不带前缀结果一致：x", cx, prefixedX, 1e-8, "米");
        ReportValue("……：y", cy, prefixedYResult, 1e-8, "米");

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("    结论：换带 = 反算 + 正算，经纬度是唯一的中间语言；相邻带的同名点差约 600 公里，");
            Console.WriteLine("      所以「换带」是把同一个点换一把尺子量，不是把点搬走；带号前缀另算，差的是 2000 公里。");
        }
        return failures.Count;
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

    /// <summary>
    /// [15] shapefile：读现成数据（Natural Earth 110m 国家），对的是裸读 .shp 字节算出来的数字。
    ///
    /// 为什么用这个文件当判据：177 条记录 / 10,654 个顶点 / 文件头 bbox 这三样，
    /// 是先按 shapefile 规范用 struct 直接读字节得到的（不经过任何 GIS 库），
    /// 所以它们同时钉住"记录数、顶点数、范围"——哪一处读错都会露馅。
    /// 数据不在（TestData 没下载）就打印跳过，不让自检因为"没数据"变红。
    /// </summary>
    private static int RunShapefileCheck()
    {
        Console.WriteLine();
        Console.WriteLine("[15] shapefile：读 .shp/.dbf（对的是裸读字节算出来的记录数/顶点数/范围）");

        var failures = new List<string>();

        void ReportValue(string label, double expected, double actual, double tolerance, string unit)
        {
            var diff = Math.Abs(expected - actual);
            var ok = diff <= tolerance;
            if (!ok) failures.Add(label);
            Console.WriteLine($"    {(ok ? "✓" : "✗")} {label}：期望 {expected:0.#######} {unit}，实得 {actual:0.#######} {unit}，差 {diff:E2}（允许 {tolerance:E2}）");
        }

        void ReportTrue(string label, bool ok, string detail)
        {
            if (!ok) failures.Add(label);
            Console.WriteLine($"    {(ok ? "✓" : "✗")} {label} —— {detail}");
        }

        var root = ResolveTestDataRoot();
        if (root is null)
        {
            Console.WriteLine("    - 没找到 TestData 目录（真实数据不随仓库上传），跳过这一段");
            return 0;
        }

        var countriesPath = Path.Combine(root, "naturalearth", "ne_110m_admin_0_countries", "ne_110m_admin_0_countries.shp");
        if (!File.Exists(countriesPath))
        {
            Console.WriteLine("    - 没找到 Natural Earth 110m 国家数据，跳过这一段");
            return 0;
        }

        var layer = ShapefileReader.ReadFile(countriesPath);
        ReportTrue("文件名保真", layer.FileName == "ne_110m_admin_0_countries.shp", $"「{layer.FileName}」");
        ReportTrue("记录数 177", layer.Features.Count == 177, $"{layer.Features.Count} 条（.shx 反推 177 条）");
        ReportTrue("一条没丢", layer.SkippedCount == 0, $"跳过 {layer.SkippedCount} 条");

        // 顶点总数：多部件要是被漏掉、或者被数了两次，这个数字立刻不对。
        // 10,642 = 只数外环。全环是 10,654，差的 12 个顶点在洞里 —— 洞暂时不画（见 ToGeometry 的说明）。
        var vertexCount = layer.AllPoints().Count();
        ReportTrue("顶点总数 10,642（只算外环；多部件既不丢也不重复数）", vertexCount == 10642, $"{vertexCount} 个");

        if (layer.LonLatBounds is { } bounds)
        {
            ReportValue("西界 MinLon", -180.0, bounds.MinLon, 1e-9, "度");
            ReportValue("南界 MinLat", -90.0, bounds.MinLat, 1e-9, "度");
            ReportValue("东界 MaxLon", 180.00000000000006, bounds.MaxLon, 1e-9, "度");
            ReportValue("北界 MaxLat", 83.64513000000001, bounds.MaxLat, 1e-9, "度");
        }
        else
        {
            ReportTrue("经纬度范围非空", false, "一层 177 个国家不可能没有范围");
        }

        // 属性：字段名可能是 NAME / name，GetProperty 做了忽略大小写的兜底，两种都该取到 Fiji。
        var first = layer.Features[0];
        ReportTrue("首条 name = Fiji", first.GetProperty("name") == "Fiji", $"name = 「{first.GetProperty("name")}」");

        // 首条在 shapefile 里是 MultiPolygon（斐济：3 块），所以 Parts 必须保住 3 块、合计 22 个顶点。
        var parts = first.Geometry.Parts;
        ReportTrue("首条 3 个部件", parts.Count == 3, $"{parts.Count} 个部件");
        ReportTrue("首条合计 22 个顶点", parts.Sum(p => p.Count) == 22, $"{parts.Sum(p => p.Count)} 个顶点");

        // 策略真的传进库里了吗：10m 省级数据里有 1 条不合规多边形，
        // 五种策略留下的东西各不相同 —— 这是"选项有没有接上"最硬的证据。
        var provincesPath = Path.Combine(root, "naturalearth", "ne_10m_admin_1_states_provinces", "ne_10m_admin_1_states_provinces.shp");
        if (File.Exists(provincesPath))
        {
            Console.WriteLine("    （下面要读 4 次 35 MB 的数据，这一段慢是正常的）");

            var strictMessage = "(没抛异常)";
            try
            {
                ShapefileReader.ReadFile(provincesPath, new ShapefileReadOptions(ShapefileGeometryPolicy.Strict));
            }
            catch (Exception ex)
            {
                strictMessage = $"{ex.GetType().Name}: {ex.Message}";
            }

            ReportTrue("Strict：不合规几何直接抛异常", strictMessage.StartsWith("ShapefileException", StringComparison.Ordinal), strictMessage);

            var ignore = ShapefileReader.ReadFile(provincesPath, new ShapefileReadOptions(ShapefileGeometryPolicy.IgnoreInvalid));
            var skip = ShapefileReader.ReadFile(provincesPath, new ShapefileReadOptions(ShapefileGeometryPolicy.SkipInvalid));
            var quick = ShapefileReader.ReadFile(provincesPath, new ShapefileReadOptions(ShapefileGeometryPolicy.QuickFixInvalid));

            ReportTrue("IgnoreInvalid：4,596 条全留（只删掉坏环）",
                ignore.Features.Count == 4596 && ignore.SkippedCount == 0,
                $"{ignore.Features.Count} 条、跳过 {ignore.SkippedCount} 条");
            ReportTrue("SkipInvalid：整条丢掉，只剩 4,595 条",
                skip.Features.Count == 4595 && skip.SkippedCount == 1,
                $"{skip.Features.Count} 条、跳过 {skip.SkippedCount} 条");
            ReportTrue("QuickFix：库给 4,596 条，其中 1 条被修成空几何 → 也得丢掉（4,595 条）",
                quick.Features.Count == 4595 && quick.SkippedCount == 1,
                $"{quick.Features.Count} 条、跳过 {quick.SkippedCount} 条");
        }
        else
        {
            Console.WriteLine("    - 没找到 10m 省级数据，跳过策略对比（这一项要读 35 MB，本来就慢）");
        }

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("    结论：记录数、顶点数、经纬度范围、属性键值、多部件边界五样都对上了；");
            Console.WriteLine("      而且「几何不合规怎么办」这个选项确实传到了库里面（两种策略的记录数不一样）。");
        }
        else
        {
            Console.WriteLine($"    有 {failures.Count} 项不符。按这个顺序查：");
            Console.WriteLine("      ① 记录数/顶点数不对 → 是不是 Multi* 只取了第一块，或者把多个部件串成了一条？");
            Console.WriteLine("         （顶点数若是 10,654，说明连洞（InteriorRing）也塞进部件里了 —— 洞会被画成实心面）");
            Console.WriteLine("      ② 范围不对 → 顶点的 (X, Y) 有没有写反？多部件的点有没有参与范围计算？");
            Console.WriteLine("      ③ 属性取不到 → 字段名大小写、或值的字符串化失败了；");
            Console.WriteLine("      ④ 策略对比不对 → ToLibraryMode 的五项映射有没有错位（比如 Skip 和 Ignore 对调），");
            Console.WriteLine("         或者忘了丢掉库修出来的空几何（QuickFix 那条会变成空几何）。");
        }

        return failures.Count;
    }

    /// <summary>
    /// [17] .prj 声明：WKT → 「经纬度还是平面 / 哪套基准 / 哪个椭球」。
    ///
    /// 为什么值得单测：坐标数字本身永远不会告诉你它是 WGS84 还是 CGCS2000，
    /// 而这个判断错了，后面的投影、七参数、换带全都建立在错的起点上。
    /// 这里用一段真实文件的 .prj + 几段手写 WKT，把四种基准和两种坐标系都走一遍。
    /// </summary>
    private static int RunPrjCheck(string samplePath)
    {
        Console.WriteLine();
        Console.WriteLine("[17] .prj 声明：WKT → 坐标系类型 / 基准 / 椭球");

        var failures = 0;

        void Report(string label, bool ok, string detail)
        {
            Console.WriteLine($"    {(ok ? "✓" : "✗")} {label} —— {detail}");
            if (!ok) failures++;
        }

        static bool Near(double? actual, double expected, double tolerance)
            => actual is { } v && Math.Abs(v - expected) < tolerance;

        // ① 磁盘上真实的 .prj（Natural Earth 全是 WGS84 经纬度）
        var dataRoot = ResolveTestDataRoot();
        var shpPath = dataRoot is null
            ? null
            : Path.Combine(dataRoot, "naturalearth", "ne_110m_admin_0_countries", "ne_110m_admin_0_countries.shp");
        if (shpPath is not null && File.Exists(shpPath))
        {
            var info = PrjReader.TryRead(shpPath);
            if (info is null)
            {
                Report("真实 .prj 能读出来", false, "TryRead 返回了 null");
            }
            else
            {
                Report("真实 .prj：是地理坐标系", !info.IsProjected, info.IsProjected ? "被认成投影坐标系了" : "GEOGCS");
                Report("真实 .prj：基准名 D_WGS_1984",
                       string.Equals(info.DatumName, "D_WGS_1984", StringComparison.OrdinalIgnoreCase),
                       $"「{info.DatumName}」");
                Report("真实 .prj：长半轴 6378137", Near(info.SemiMajorAxis, 6378137.0, 1e-6), $"{info.SemiMajorAxis}");
                Report("真实 .prj：扁率倒数 298.257223563", Near(info.InverseFlattening, 298.257223563, 1e-9),
                       $"{info.InverseFlattening}");

                var match = PrjReader.MatchDatum(info);
                Report("真实 .prj：认出 WGS84，且是按名字认的",
                       match is { FromName: true } m && m.Datum.Name == DatumCatalog.Wgs84.Name,
                       match is null ? "没认出来" : $"{match.Value.Datum.Name}（FromName = {match.Value.FromName}）");
            }
        }
        else
        {
            Console.WriteLine("    - 没找到 110m 国家数据，跳过“真实 .prj”这一组（TestData 不进版本库）");
        }

        // ② 没有 .prj 的文件：不该抛异常，只该说“不知道”
        var missing = PrjReader.TryRead(samplePath);
        Report("没有 .prj 的路径 → null", missing is null,
               missing is null ? "返回 null（不知道，但也没炸）" : "返回了非 null");

        // ③④⑤⑥ 四种基准 + 一种投影坐标系，用标准 WKT 走一遍
        const string wgs84 = "GEOGCS[\"GCS_WGS_1984\",DATUM[\"D_WGS_1984\",SPHEROID[\"WGS_1984\",6378137.0,298.257223563]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]]";
        const string cgcs2000 = "GEOGCS[\"GCS_China_Geodetic_Coordinate_System_2000\",DATUM[\"D_China_2000\",SPHEROID[\"CGCS2000\",6378137.0,298.257222101]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]]";
        const string beijing54 = "GEOGCS[\"GCS_Beijing_1954\",DATUM[\"D_Beijing_1954\",SPHEROID[\"Krasovsky_1940\",6378245.0,298.3]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]]";
        const string xian80 = "GEOGCS[\"GCS_Xian_1980\",DATUM[\"D_Xian_1980\",SPHEROID[\"Xian_1980\",6378140.0,298.257]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]]";
        const string cgcs2000Gk = "PROJCS[\"CGCS2000_3_Degree_GK_Zone_36\",GEOGCS[\"GCS_China_Geodetic_Coordinate_System_2000\",DATUM[\"D_China_2000\",SPHEROID[\"CGCS2000\",6378137.0,298.257222101]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]],PROJECTION[\"Gauss_Kruger\"],PARAMETER[\"False_Easting\",36500000.0],PARAMETER[\"False_Northing\",0.0],PARAMETER[\"Central_Meridian\",108.0],PARAMETER[\"Scale_Factor\",1.0],PARAMETER[\"Latitude_Of_Origin\",0.0],UNIT[\"Meter\",1.0]]";

        void CheckDatum(string label, string wkt, Datum expected)
        {
            var info = PrjReader.Parse(wkt);
            var match = info is null ? null : PrjReader.MatchDatum(info);
            Report(label,
                   match is { FromName: true } m && m.Datum.Name == expected.Name,
                   info is null
                       ? "Parse 返回 null"
                       : match is null
                           ? $"没认出来（基准名「{info.DatumName}」、椭球「{info.SpheroidName}」）"
                           : $"{match.Value.Datum.Name}（FromName = {match.Value.FromName}）");
        }

        CheckDatum("WGS84 经纬度 → WGS84", wgs84, DatumCatalog.Wgs84);
        CheckDatum("CGCS2000 经纬度 → CGCS2000", cgcs2000, DatumCatalog.Cgcs2000);
        CheckDatum("北京54 经纬度 → 北京54", beijing54, DatumCatalog.Beijing54);
        CheckDatum("西安80 经纬度 → 西安80", xian80, DatumCatalog.Xian80);

        var gk = PrjReader.Parse(cgcs2000Gk);
        Report("CGCS2000 高斯投影：内层 GEOGCS 不算数，整体是投影坐标系",
               gk is { IsProjected: true }, gk is null ? "Parse 返回 null" : $"IsProjected = {gk.IsProjected}");
        Report("CGCS2000 高斯投影：椭球仍能取到 6378137 / 298.257222101",
               gk is not null && Near(gk.SemiMajorAxis, 6378137.0, 1e-6) && Near(gk.InverseFlattening, 298.257222101, 1e-9),
               gk is null ? "Parse 返回 null" : $"a = {gk.SemiMajorAxis}、1/f = {gk.InverseFlattening}");

        // ⑦ 名字认不出（基准名和椭球名都不含任何线索）、只能靠椭球数字猜：必须如实说“这是猜的”
        const string unknownDatum = "GEOGCS[\"GCS_Unknown\",DATUM[\"D_Something_Else\",SPHEROID[\"Ellipsoid_1\",6378137.0,298.257223563]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]]";
        var unknownInfo = PrjReader.Parse(unknownDatum);
        var unknownMatch = unknownInfo is null ? null : PrjReader.MatchDatum(unknownInfo);
        Report("名字认不出 → 退回椭球匹配，并标记为“猜的”",
               unknownMatch is { FromName: false } um && um.Datum.Name == DatumCatalog.Wgs84.Name,
               unknownMatch is null ? "没认出来" : $"{unknownMatch.Value.Datum.Name}（FromName = {unknownMatch.Value.FromName}）");

        // ⑧ 排版恶劣：只到 ProjNET 能容忍的那几种 —— 换行、多空格、制表符、数字不带小数点。
        //    实测（TestData\probe\MessyProbe）：这四种它全接受，唯独"关键字小写"不认。
        const string messyLayout = "GEOGCS [ \"GCS_WGS_1984\" ,\r\n\tDATUM [ \"D_WGS_1984\" ,\r\n\t\tSPHEROID [ \"WGS_1984\" , 6378137 , 298.257223563 ] ] ,\r\n\tPRIMEM [ \"Greenwich\" , 0 ] ,\r\n\tUNIT [ \"Degree\" , 0.0174532925199433 ] ]";
        var messyInfo = PrjReader.Parse(messyLayout);
        Report("排版恶劣（换行/多空格/制表符/整数写法）也认得出",
               messyInfo is not null && !messyInfo.IsProjected
               && Near(messyInfo.SemiMajorAxis, 6378137.0, 1e-6)
               && Near(messyInfo.InverseFlattening, 298.257223563, 1e-9),
               messyInfo is null ? "Parse 返回 null" : $"a = {messyInfo.SemiMajorAxis}、1/f = {messyInfo.InverseFlattening}");

        // ⑧b 小写关键字：ProjNET 大小写敏感，明确要求返回 null。
        //     这不是 bug，是把库的边界"钉"下来，免得以后有人把它当 bug 去"修"。
        const string lowercaseKeywords = "geogcs[\"GCS_WGS_1984\",DATUM[\"D_WGS_1984\",SPHEROID[\"WGS_1984\",6378137.0,298.257223563]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]]";
        var lowercaseInfo = PrjReader.Parse(lowercaseKeywords);
        Report("小写关键字 → 返回 null（ProjNET 大小写敏感；真实 .prj 由软件生成，一律大写）",
               lowercaseInfo is null,
               lowercaseInfo is null ? "null" : "居然认出来了 —— 是不是加了归一化？");

        // ⑨ 根本不是 WKT：返回 null，不抛异常
        var garbage = PrjReader.Parse("这不是 WKT，只是一句中文");
        Report("不是 WKT → 返回 null 而不是抛异常", garbage is null, garbage is null ? "null" : "返回了非 null");
        var empty = PrjReader.Parse("   ");
        Report("空字符串 → 返回 null", empty is null, empty is null ? "null" : "返回了非 null");

        Console.WriteLine();
        if (failures == 0)
        {
            Console.WriteLine("    结论：真实 .prj 与四套基准的 WKT 都能认出来，投影坐标系没被内层 GEOGCS 骗到，");
            Console.WriteLine("      名字缺失时老实退回椭球匹配并标记“猜的”，坏输入不抛异常。");
        }
        else
        {
            Console.WriteLine($"    有 {failures} 项不符。按这个顺序查：");
            Console.WriteLine("      ① 是不是被内层 GEOGCS 骗了（投影 WKT 里也有 GEOGCS，要比较两者谁在前）；");
            Console.WriteLine("      ② 引号串取错了（DATUM 后面第一个引号串才是名字，别取到 PROJCS 的名字）；");
            Console.WriteLine("      ③ 数字解析没用 InvariantCulture（逗号是分隔符，区域设置一变小数点就变逗号）；");
            Console.WriteLine("      ④ 小写关键字 ProjNET 不认（它大小写敏感）—— 小写归入“认不出”，不算错。");
        }

        return failures;
    }

    /// <summary>
    /// [18] .prj → 「源基准」建议：什么时候替用户选，什么时候只提示。
    /// </summary>
    private static int RunPrjSuggestionCheck()
    {
        Console.WriteLine();
        Console.WriteLine("[18] .prj → 源基准建议（认得出才改，认不出只提示）");
        var failures = 0;

        void Report(string label, bool ok, string detail)
        {
            Console.WriteLine($"      {(ok ? "✓" : "✗")} {label} —— {detail}");
            if (!ok) failures++;
        }

        const string wgs84 = "GEOGCS[\"GCS_WGS_1984\",DATUM[\"D_WGS_1984\",SPHEROID[\"WGS_1984\",6378137.0,298.257223563]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]]";
        const string beijing54WithToWgs84 = "GEOGCS[\"GCS_Beijing_1954\",DATUM[\"D_Beijing_1954\",SPHEROID[\"Krasovsky_1940\",6378245.0,298.3],TOWGS84[-110.0,-41.0,-24.0,1.5,-1.2,2.0,3.5]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]]";
        const string projected = "PROJCS[\"CGCS2000_3_Degree_GK_Zone_36\",GEOGCS[\"GCS_China_Geodetic_Coordinate_System_2000\",DATUM[\"D_China_2000\",SPHEROID[\"CGCS2000\",6378137.0,298.257222101]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]],PROJECTION[\"Gauss_Kruger\"],PARAMETER[\"False_Easting\",36500000.0],PARAMETER[\"False_Northing\",0.0],PARAMETER[\"Central_Meridian\",108.0],PARAMETER[\"Scale_Factor\",1.0],PARAMETER[\"Latitude_Of_Origin\",0.0],UNIT[\"Meter\",1.0]]";
        // 椭球故意取一个四套基准都对不上的值：否则 [17] ⑦ 那条"退回椭球匹配"会让这里也认成 WGS84。
        const string unknownDatum = "GEOGCS[\"GCS_Unknown\",DATUM[\"D_Something_Else\",SPHEROID[\"Odd_Ellipsoid\",6378000.0,299.0]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]]";

        var wgsInfo = PrjReader.Parse(wgs84);
        var wgsSuggestion = wgsInfo is null ? null : PrjSuggestion.For(wgsInfo);
        Report("WGS84 声明 → 建议源基准 WGS84、没有警告",
               wgsSuggestion is { Datum: not null, Warning: null } ws && ws.Datum.Name == DatumCatalog.Wgs84.Name,
               wgsSuggestion is null ? "For 返回 null" : $"{wgsSuggestion.Datum?.Name ?? "null"} / 警告 = {wgsSuggestion.Warning ?? "null"}");

        var bjInfo = PrjReader.Parse(beijing54WithToWgs84);
        var bjSuggestion = bjInfo is null ? null : PrjSuggestion.For(bjInfo);
        Report("带 TOWGS84 → HasToWgs84 = true、建议北京54",
               bjSuggestion is { Datum: not null, HasToWgs84: true } bs && bs.Datum.Name == DatumCatalog.Beijing54.Name,
               bjSuggestion is null ? "For 返回 null" : $"HasToWgs84 = {bjSuggestion.HasToWgs84}、{bjSuggestion.Datum?.Name ?? "null"}");

        var gkInfo = PrjReader.Parse(projected);
        var gkSuggestion = gkInfo is null ? null : PrjSuggestion.For(gkInfo);
        var gkWarning = gkSuggestion?.Warning;
        Report("投影坐标系 → 不给建议，但要有一句提示",
               gkSuggestion is { Datum: null } && !string.IsNullOrWhiteSpace(gkWarning),
               gkSuggestion is null ? "For 返回 null" : $"{gkSuggestion.Datum?.Name ?? "null"} / {gkWarning ?? "null"}");

        var unknownInfo = PrjReader.Parse(unknownDatum);
        var unknownSuggestion = unknownInfo is null ? null : PrjSuggestion.For(unknownInfo);
        var unknownWarning = unknownSuggestion?.Warning;
        Report("认不出基准 → 不给建议，但要有一句提示",
               unknownSuggestion is { Datum: null } && !string.IsNullOrWhiteSpace(unknownWarning),
               unknownSuggestion is null ? "For 返回 null" : $"{unknownSuggestion.Datum?.Name ?? "null"} / {unknownWarning ?? "null"}");

        var noneSuggestion = PrjSuggestion.For(null);
        var noneWarning = noneSuggestion.Warning;
        Report("没有 .prj → 不给建议，但要有一句提示",
               noneSuggestion is { Datum: null } && !string.IsNullOrWhiteSpace(noneWarning),
               $"{noneSuggestion.Datum?.Name ?? "null"} / {noneWarning ?? "null"}");

        // 三句提示是给操作员看的：三种「不改」的原因必须能区分开，否则他只知道「没识别出基准」，
        // 分不清是数据本来就是米、还是这套基准没见过、还是压根没有声明。
        var distinctWarnings = new[] { gkWarning, unknownWarning, noneWarning }
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Distinct(StringComparer.Ordinal)
            .Count();
        Report("三种「不改」的原因 → 三句提示互不相同",
               distinctWarnings == 3,
               distinctWarnings == 3 ? "三句均不同" : $"只有 {distinctWarnings} 种不同的提示");

        // 名字认不出、但椭球参数正好等于某一套基准 —— 这时只能"猜"，猜对了也必须说明是猜的。
        var guessedInfo = PrjReader.Parse("GEOGCS[\"GCS_Ambiguous\",DATUM[\"D_Ambiguous\",SPHEROID[\"Ellipsoid_1\",6378137.0,298.257223563]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]]");
        var guessedSuggestion = guessedInfo is null ? null : PrjSuggestion.For(guessedInfo);
        Report("只对上椭球参数 → 给建议，但 FromName = false 且要说明是猜的",
               guessedSuggestion is { Datum: not null, FromName: false, HasToWgs84: false } guess
               && guess.Datum.Name == DatumCatalog.Wgs84.Name
               && !string.IsNullOrWhiteSpace(guess.Warning),
               guessedSuggestion is null
                   ? "For 返回 null"
                   : $"{guessedSuggestion.Datum?.Name ?? "null"} / FromName = {guessedSuggestion.FromName} / HasToWgs84 = {guessedSuggestion.HasToWgs84} / {guessedSuggestion.Warning ?? "null"}");

        // HasToWgs84 是"原样透传"，不是"我认得出来就写 true" —— 投影坐标系也可能带 TOWGS84。
        // （TOWGS84 必须写在 DATUM 里面；写在 GEOGCS 外面就成了非法 WKT，Parse 会按契约返回 null。）
        const string projectedWithToWgs84 = "PROJCS[\"CGCS2000_3_Degree_GK_Zone_36\",GEOGCS[\"GCS_China_Geodetic_Coordinate_System_2000\",DATUM[\"D_China_2000\",SPHEROID[\"CGCS2000\",6378137.0,298.257222101],TOWGS84[-110.0,-41.0,-24.0,1.5,-1.2,2.0,3.5]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]],PROJECTION[\"Gauss_Kruger\"],PARAMETER[\"False_Easting\",36500000.0],PARAMETER[\"False_Northing\",0.0],PARAMETER[\"Central_Meridian\",108.0],PARAMETER[\"Scale_Factor\",1.0],PARAMETER[\"Latitude_Of_Origin\",0.0],UNIT[\"Meter\",1.0]]";
        var ptwInfo = PrjReader.Parse(projectedWithToWgs84);
        var ptwSuggestion = ptwInfo is null ? null : PrjSuggestion.For(ptwInfo);
        Report("投影坐标系 + TOWGS84 → HasToWgs84 原样透传 = true",
               ptwSuggestion is { Datum: null, HasToWgs84: true },
               ptwSuggestion is null
                   ? "For 返回 null"
                   : $"{ptwSuggestion.Datum?.Name ?? "null"} / HasToWgs84 = {ptwSuggestion.HasToWgs84}");

        Console.WriteLine();
        if (failures == 0)
        {
            Console.WriteLine("    结论：.prj 认得出就选基准，是投影/认不出/没有声明就只提示 —— 不擅自改用户的设置。");
        }
        else
        {
            Console.WriteLine($"    有 {failures} 项不符。按这个顺序查：");
            Console.WriteLine("      ① 三种「不改」的分支是不是都给了非空的 Warning（空串不算）；");
            Console.WriteLine("      ② IsProjected 的判断是不是在 MatchDatum 之前（投影坐标系的坐标是米，认基准没意义）；");
            Console.WriteLine("      ③ 三句提示是不是写成了同一句 —— 操作员要靠它区分「数据是米 / 基准没见过 / 压根没声明」。");
        }

        return failures;
    }

    /// <summary>
    ///
    /// 这一段存在的理由：界面里"按后缀走哪条路"的分支自检碰不到，
    /// 而那正是最容易出现「切了没反应」的地方。把分派搬进 Logic 之后，它就能被自检直接点名。
    /// </summary>
    private static int RunLayerReaderCheck(string samplePath)
    {
        Console.WriteLine();
        Console.WriteLine("[16] 打开文件：按扩展名分派到 GeoJSON / shapefile 两条读路径");

        var failures = 0;

        void Report(string label, bool ok, string detail)
        {
            Console.WriteLine($"    {(ok ? "✓" : "✗")} {label} —— {detail}");
            if (!ok) failures++;
        }

        // ① .geojson：走 GeoJsonReader 那条老路
        var geo = LayerReader.ReadFile(samplePath);
        Report("GeoJSON 走 GeoJSON 那条路", geo.Features.Count == 5, $"{geo.Features.Count} 条对象");
        Report("文件名保真", geo.FileName == Path.GetFileName(samplePath), $"「{geo.FileName}」");

        // ② .shp：走 ShapefileReader
        var dataRoot = ResolveTestDataRoot();
        var shpPath = dataRoot is null
            ? null
            : Path.Combine(dataRoot, "naturalearth", "ne_110m_admin_0_countries", "ne_110m_admin_0_countries.shp");
        if (shpPath is not null && File.Exists(shpPath))
        {
            var shp = LayerReader.ReadFile(shpPath);
            Report("shapefile 走 shapefile 那条路", shp.Features.Count == 177,
                   $"{shp.Features.Count} 条对象、跳过 {shp.SkippedCount} 条");
        }
        else
        {
            Console.WriteLine("    - 没找到 110m 国家数据，跳过 shapefile 分派（TestData 不进版本库）");
        }

        // ③ 调用方给的读选项，有没有真的传到库里。
        //    这一条专门盯“参数半路被丢掉”：函数签名收下了 shapefileOptions，但调库时没带上，
        //    默认策略会把数据读出来、一切看起来正常，只有这里能看出来。
        var strictPath = dataRoot is null
            ? null
            : Path.Combine(dataRoot, "naturalearth", "ne_10m_admin_1_states_provinces", "ne_10m_admin_1_states_provinces.shp");
        if (strictPath is not null && File.Exists(strictPath))
        {
            var strictThrew = false;
            var strictMessage = "";
            try
            {
                LayerReader.ReadFile(strictPath, new ShapefileReadOptions(ShapefileGeometryPolicy.Strict));
            }
            catch (Exception ex)
            {
                strictThrew = ex.GetType().Name == "ShapefileException";
                strictMessage = strictThrew ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            }

            Report("读选项透传到库里（Strict 应当抛异常）", strictThrew,
                   strictThrew ? $"ShapefileException: {strictMessage}" : "没抛异常 —— shapefileOptions 是不是半路被丢了？");
        }
        else
        {
            Console.WriteLine("    - 没找到 10m 省级数据，跳过「读选项透传」这一项");
        }

        // ③④ 扩展名的大小写、以及“根本不认识”的情况，都在输出目录（bin 下）里造出来。
        // 不放 Path.GetTempPath()：部分沙箱/受控环境不允许往系统临时目录写，bin 下一定可写、也一定被忽略。
        var tempDir = Path.Combine(AppContext.BaseDirectory, ".selftest-temp-" + Path.GetFileNameWithoutExtension(Path.GetRandomFileName()));
        Directory.CreateDirectory(tempDir);
        try
        {
            var upperPath = Path.Combine(tempDir, "upper.GEOJSON");
            File.Copy(samplePath, upperPath, overwrite: true);
            var upper = LayerReader.ReadFile(upperPath);
            Report("大写扩展名 .GEOJSON 也认", upper.Features.Count == 5, $"{upper.Features.Count} 条对象");

            var txtPath = Path.Combine(tempDir, "notes.txt");
            File.WriteAllText(txtPath, "这里不是地图");
            var threw = false;
            var message = "";
            try
            {
                LayerReader.ReadFile(txtPath);
            }
            catch (GeoJsonException ex)
            {
                threw = true;
                message = ex.Message;
            }

            Report("不认识的扩展名 → 抛 GeoJsonException",
                   threw && message.Contains(".shp", StringComparison.OrdinalIgnoreCase),
                   threw ? $"「{message}」" : "没抛异常，而是继续往下读了");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* 临时目录删不掉不影响结论 */ }
        }

        Console.WriteLine();
        if (failures == 0)
        {
            Console.WriteLine("    结论：同一个入口按扩展名走对了路，文件名保真；大小写与不认识的类型也处理了。");
            Console.WriteLine("      （剩下的是对话框本身：标题、过滤器、默认目录 —— 那部分只能手点。）");
        }
        else
        {
            Console.WriteLine($"    有 {failures} 项不符。按这个顺序查：");
            Console.WriteLine("      ① 扩展名比较前有没有转小写（.SHP / .GeoJSON 都得认）；");
            Console.WriteLine("      ② 分派的两边有没有接对：.shp → ShapefileReader，.geojson/.json → GeoJsonReader；");
            Console.WriteLine("      ③ 不认识的扩展名要抛 GeoJsonException，并在消息里列出支持的类型；");
            Console.WriteLine("      ④ 读不到记录 → shapefileOptions 是不是半路丢了。");
        }

        return failures;
    }

    /// <summary>
    /// 找 TestData 目录（真实数据放在仓库根目录的 TestData 下，且不进版本库）。
    /// 和 ResolveSamplePath 一样从输出目录往上找，这样"从哪个工作目录启动"都能找到。
    /// </summary>
    private static string? ResolveTestDataRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "TestData");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return null;
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
