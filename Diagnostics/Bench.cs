namespace GeoJsonViewer.Diagnostics;

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GeoJsonViewer.Logic;
using GeoJsonViewer.Models;
using GeoJsonViewer.Rendering;

/// <summary>
/// 压测：GeoJsonViewer.exe --bench [文件路径]
///
/// 输出必须是一张**可直接粘进 README 的表**（第十一节规定了格式）。
///
/// 两列数字的来源必须说清楚，否则这张表就是自欺：
///   · **改之前**：同一份数据，走第一条路径 —— SimpleRenderer 逐对象全画、
///     HitTester 全量扫描。这些是旧工程里的类，本工程一行没改地引用它们，
///     所以这一列不是"抄来的历史数字"，而是**这台机器上现在量到的**。
///   · **改之后**：走窗口自己的路径（BenchOpen / BenchRebuild / BenchHover 转发到
///     真的 OnOpenClick 后半段、RebuildView、UpdateHoverTip），不是测试里另写的复制品。
///
/// 三个规矩（第十一节）：预热后取中位；只测操作、不测启动；数字要能复跑。
/// </summary>
public static class Bench
{
    private const int Warmup = 2;
    private const int Iterations = 9;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Run(string? layerPath)
    {
        var path = layerPath ?? ResolveSample("中华人民共和国.geojson");
        if (!File.Exists(path))
        {
            Console.WriteLine($"找不到文件：{path}");
            return 2;
        }

        var window = new Views.MainWindow
        {
            Left = -32000,
            Top = -32000,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();
        window.UpdateLayout();

        var canvas = FindCanvas(window);
        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        var hoverPoint = new Point(width * 0.60, height * 0.55);

        // ── 改之前：同一份文件，但走"没有索引"的那条路 ──
        var layer = LayerReader.ReadFile(path);
        var settings = Projection.BuildSettings(0, 0, 0, layer.LonLatBounds ?? new GeoRect());
        var projected = layer.AllPoints().Select(p => Projection.Project(p, settings)).ToList();
        var oldBounds = new GeoBounds(projected);
        oldBounds.Reset(width, height);

        var simpleRenderer = new SimpleRenderer();

        // 规矩 1「预热一次再测」在这里比平时更重要：两条路要放在同一台机器、同一个进程里比，
        // 谁先跑谁就吃 JIT。所以先把**两条路都点着**（各自完整跑一遍，结果丢掉），再开始计时。
        simpleRenderer.Render(layer, settings, oldBounds, canvas);
        window.BenchOpen(path);
        window.BenchRebuild();
        PumpUntil(() => !window.BenchRedrawPending, 5000);
        HitTester.HitTest(layer, settings, oldBounds, hoverPoint.X, hoverPoint.Y);
        canvas.Children.Clear();

        // 先按"旧代码放最前面"的老做法量一次，留着当顺序效应的证据
        var earlyOldDraw = Median(9, () => simpleRenderer.Render(layer, settings, oldBounds, canvas));
        canvas.Children.Clear();

        // ── 改之后：让真窗口自己走一遍打开流程 ──
        // 打开 + 首帧量三轮取中位：打开会一次性分配整个图层的图元，第一轮常被 Gen2 GC 拖长几百毫秒，
        // 只测一轮会得到一个只在第一次出现的数字。
        var opens = new List<double>();
        var firsts = new List<double>();
        for (var round = 0; round < 3; round++)
        {
            canvas.Children.Clear();
            Gc();

            var sw = Stopwatch.StartNew();
            window.BenchOpen(path);
            sw.Stop();
            opens.Add(sw.Elapsed.TotalMilliseconds);

            firsts.Add(StopwatchOnce(window.BenchRebuild));
            PumpUntil(() => !window.BenchRedrawPending, 5000);
        }

        var openMs = MedianOf(opens);
        var newFirst = MedianOf(firsts);

        // 每一项都记"中位 ± 半幅"：1× 这一档两列本来就只差几毫秒，
        // 只报一个数会让人把噪声当成结论（上一版就是这么误报的）。
        var newRedrawS = Stats(Iterations, window.BenchRebuild);
        var newHoverS = Stats(Iterations, () => window.BenchHover(hoverPoint));

        // 一帧拆两段：找候选 / 画候选。悬停只跑前一段，所以"悬停有没有变化"看这一行就够。
        window.BenchRebuild();
        var breakdown1x = window.BenchBreakdown;

        var state = window.BenchState;
        var indexMs = state.Layer is null
            ? 0.0
            : Median(3, () => _ = new StrtreeFinder(state.Layer!, state.Settings));

        // 8×：直接改窗口里那份 bounds（同一个对象），再量一次
        var zoom8 = state.Bounds;
        var newRedraw8S = default(Stat);
        var breakdown8x = (Clear: 0.0, Query: 0.0, Draw: 0.0);
        if (zoom8 is not null)
        {
            zoom8.Zoom = 8.0;
            newRedraw8S = Stats(Iterations, window.BenchRebuild);
            window.BenchRebuild();
            breakdown8x = window.BenchBreakdown;
            zoom8.Zoom = 1.0;
            window.BenchRebuild();
        }

        // ── 对照实验：为什么"同一个渲染器、同一份数据"两条路差一倍 ──
        // 把两边各自的图层对象都拿旧渲染器画一次，画到同一个离屏画布上。
        var newLayer = window.BenchState.Layer;
        var off = new Canvas { Width = width, Height = height };
        var expOldLayer = Median(5, () => simpleRenderer.Render(layer, settings, oldBounds, off));
        var expNewLayer = newLayer is null
            ? 0.0
            : Median(5, () => simpleRenderer.Render(newLayer, window.BenchState.Settings, window.BenchState.Bounds!, off));

        // ── 改之前：放到最后量 ──
        // 上一版把旧路径放在最前面量，1× 得到 68 ms；把同样的事放到最后再量是 43 ms。
        // 差的 25 ms 不是算法，是第一次走到这些 WPF 画图代码要付的 JIT 与 GC。
        // ★ 旧程序打开文件时也会先做基准转换，所以"改之前"这一列必须画**转换之后**的图层，
        //   否则是拿"未拍平的 8,531 个部件"去比"拍平后的 4,595 个"，比的是数据不是代码。
        var flatLayer = state.Layer!;
        var flatSettings = state.Settings;
        var flatBounds = new GeoBounds(flatLayer.AllPoints().Select(p => Projection.Project(p, flatSettings)));
        flatBounds.Reset(width, height);

        // ★ "一帧"必须两边都算清空：窗口那边 RebuildView 每次都要先 Clear，
        //   8,531 个子元素清起来不便宜。只算画、不算清，就成了又一次不对称。
        var oldFrameS = Stats(Iterations, () =>
        {
            canvas.Children.Clear();
            simpleRenderer.Render(flatLayer, flatSettings, flatBounds, canvas);
        });

        var oldFirsts = new List<double>();
        for (var round = 0; round < 3; round++)
        {
            canvas.Children.Clear();
            Gc();
            oldFirsts.Add(StopwatchOnce(() => simpleRenderer.Render(flatLayer, flatSettings, flatBounds, canvas)));
        }

        var oldFirst = MedianOf(oldFirsts);
        var oldHoverS = Stats(Iterations, () => HitTester.HitTest(flatLayer, flatSettings, flatBounds, hoverPoint.X, hoverPoint.Y));

        // 8× 那一档：放大之后"该画的少了"，两条路线的差距才看得出来
        flatBounds.Zoom = 8.0;
        var oldDraw8S = Stats(Iterations, () => simpleRenderer.Render(flatLayer, flatSettings, flatBounds, canvas));
        var oldRedraw8 = Median(Iterations, canvas.Children.Clear) + oldDraw8S.Median;
        flatBounds.Zoom = 1.0;

        var oldRedrawS = oldFrameS;

        // ── 表格 ──
        Console.WriteLine("=== 压测 ===");
        Console.WriteLine($"数据: {Path.GetFileName(path)}");
        Console.WriteLine($"对象: {layer.Features.Count:N0}    原始部件: {CountParts(layer):N0}    转换后部件: {(newLayer is null ? 0 : CountParts(newLayer)):N0}    索引: STRtree（粒度=部件）");
        Console.WriteLine();
        Console.WriteLine("             改之前                     改之后");
        Console.WriteLine($"建索引          —                        {Ms(indexMs)}");
        Console.WriteLine($"首帧            {Ms(oldFirst)}                      {Ms(newFirst)}");
        Console.WriteLine($"单次重绘        {Fmt(oldRedrawS)}     {Fmt(newRedrawS)}");
        Console.WriteLine($"单次悬停        {Fmt(oldHoverS)}     {Fmt(newHoverS)}");
        Console.WriteLine();
        Console.WriteLine($"补充：一次重绘 @8×   {Ms(oldRedraw8)}  →  {Fmt(newRedraw8S)}");
        Console.WriteLine($"      打开合计      —  →  {Ms(openMs)}（含读文件、基准转换、fit、建索引；不含上屏）");
        Console.WriteLine($"      画布          {width:0} × {height:0} px；预热 {Warmup} 次、每项 {Iterations} 次（表中为 中位±半幅）");
        Console.WriteLine($"      顺序效应     旧路径先量(冷) {Ms(earlyOldDraw)} → 后量(热) {Ms(oldFrameS.Median)}；表里用的是后者");
        Console.WriteLine($"      对照实验     旧层对象 {Ms(expOldLayer)} / 新层对象 {Ms(expNewLayer)}（同一个离屏画布）；对象 旧 {layer.Features.Count:N0} / 新 {(newLayer?.Features.Count ?? 0):N0}，部件 旧 {CountParts(layer):N0} / 新 {(newLayer is null ? 0 : CountParts(newLayer)):N0}，点数 旧 {CountPoints(layer):N0} / 新 {(newLayer is null ? 0 : CountPoints(newLayer)):N0}（旧层只看第一块 {CountFirstParts(layer):N0}）");
        Console.WriteLine($"      一帧拆开      1×：清空 {Ms(breakdown1x.Clear)} / 找候选 {Ms(breakdown1x.Query)} + 画 {Ms(breakdown1x.Draw)}"
            + $"；旧路径 1×：清空+画 {Ms(oldFrameS.Median)}；8×：找候选 {Ms(breakdown8x.Query)} + 画 {Ms(breakdown8x.Draw)}");
        Console.WriteLine("      口径          「首帧」= 已经热过的打开之后第一张画面（不含首次 JIT 与启动）；"
            + "冷启动会再多几百毫秒，那不是这几步要优化的东西");

        window.Close();
        return 0;
    }

    // ─────────────────────────────── 小工具 ───────────────────────────────

    private static Canvas FindCanvas(Window window)
        => (Canvas)window.GetType()
            .GetField("MapCanvas", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(window)!;

    private static string Ms(double v) => v.ToString("0.0", Inv) + " ms";

    private static double MedianOf(List<double> values){ values.Sort(); return values[values.Count / 2]; }

    private static void Gc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static double StopwatchOnce(Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private readonly record struct Stat(double Median, double Min, double Max);

    private static Stat Stats(int iterations, Action action)
    {
        var samples = new double[iterations];
        for (var i = 0; i < iterations; i++) samples[i] = StopwatchOnce(action);
        Array.Sort(samples);
        return new Stat(samples[iterations / 2], samples[0], samples[iterations - 1]);
    }

    private static string Fmt(Stat s) => $"{Ms(s.Median)}±{((s.Max - s.Min) / 2):0.0}";

    private static double Median(int iterations, Action action)
    {
        for (var i = 0; i < Warmup; i++) action();

        var samples = new double[iterations];
        for (var i = 0; i < iterations; i++) samples[i] = StopwatchOnce(action);

        Array.Sort(samples);
        return samples[samples.Length / 2];
    }

    /// <summary>泵消息，等窗口把挂起的那一帧画完（⑤ 之后"画"发生在 CompositionTarget.Rendering）。</summary>
    private static void PumpUntil(Func<bool> predicate, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(1),
            DispatcherPriority.Background,
            (_, _) =>
            {
                if (predicate() || sw.ElapsedMilliseconds > timeoutMs) frame.Continue = false;
            },
            Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static int CountParts(GeoLayer layer)
    {
        var total = 0;
        foreach (var feature in layer.Features)
        {
            var geometry = feature.Geometry;
            total += geometry.Parts.Count > 0 ? geometry.Parts.Count : 1;
        }
        return total;
    }

    /// <summary>多部件几何里"第一块"的点数之和：如果它等于转换后的点数，说明其余部件被丢掉了。</summary>
    private static int CountFirstParts(GeoLayer layer)
    {
        var total = 0;
        foreach (var feature in layer.Features) total += feature.Geometry.Points.Count;
        return total;
    }

    private static int CountPoints(GeoLayer layer)
    {
        var n = 0;
        foreach (var feature in layer.Features)
        {
            var geometry = feature.Geometry;
            if (geometry.Parts.Count == 0) n += geometry.Points.Count;
            else foreach (var part in geometry.Parts) n += part.Count;
        }

        return n;
    }

    private static string ResolveSample(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "GeoJsonViewer", "Samples", fileName);
            if (File.Exists(candidate)) return candidate;

            candidate = Path.Combine(directory.FullName, "Samples", fileName);
            if (File.Exists(candidate)) return candidate;

            directory = directory.Parent!;
            if (directory is null) break;
        }

        return fileName;
    }
}












