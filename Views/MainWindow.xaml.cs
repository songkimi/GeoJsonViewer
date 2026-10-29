namespace GeoJsonViewer.Views;

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GeoJsonViewer.Logic;
using GeoJsonViewer.Models;
using GeoJsonViewer.Rendering;
using Microsoft.Win32;


/// <summary>
/// 主窗口：把 Models / Logic / Rendering 串起来，并处理鼠标键盘。
///
/// 分层约定：这个类**不写任何数学**。
///   投影公式在 Projection，映射和缩放平移在 GeoBounds，命中判定在 HitTester，
///   画图在 SimpleRenderer。这里只做"取事件 → 判断状态 → 调对应的方法 → 更新界面文字"。
/// </summary>
public partial class MainWindow : Window
{
    private GeoLayer? _layer;

    /// <summary>
    /// 文件里读出来的**原始**图层（坐标还是源基准那一套）。
    /// 为什么单独留一份：基准转换不幂等 —— 永远从这一份出发，下拉框来回切才能回到原样。
    /// </summary>
    private GeoLayer? _rawLayer;

    /// <summary>
    /// true = 正在**程序性地**改「源基准」下拉框，别把这次 SelectionChanged 当成用户操作。
    /// </summary>
    private bool _suppressSourceDatumChanged;

    /// <summary>.prj 给出的那句话（没有 .prj、是投影坐标系、认不出基准……）；null = 没话要说。</summary>
    private string? _prjWarning;

    private GeoBounds? _bounds;
    private ProjectionSettings _settings = ProjectionSettings.Linear;

    /// <summary>
    /// "候选来源"：给定一块平面坐标矩形，回答"这里可能有哪些部件"。
    /// </summary>
    private IFeatureFinder? _finder;
    /// <summary>
    /// 候选组件的数量
    /// </summary>
    private int _lastCandidates;

    /// <summary>上一帧拆开之后的两段耗时（毫秒），只被 --bench 读取。</summary>
    private double _lastBreakdownClear;
    private double _lastBreakdownQuery;
    private double _lastBreakdownDraw;

    /// <summary>
    /// 这一层一共有多少个**部件**（状态栏的分母）。
    ///
    /// 为什么不用 `Features.Count`：对象数和部件数不是一回事。ne_10m 是 4,595 个对象 /
    /// 8,531 个部件，分子是部件、分母却写对象数时，整幅视野会显示成 `4595/4595` ——
    /// 看起来像索引没干活，其实是两个数在报不同的东西。
    /// </summary>
    private int _lastTotalParts;

    // ─────────────────── ① 图元复用：只平移时不动顶点 ───────────────────
    //
    // 老路子每帧 `Children.Clear()` + 重造 8,531 个 UIElement：ne_10m 一帧 854 ms，
    // 其中清空就占 462 ms。可平移的时候，顶点本来就是对的（ToScreen 只是多了个 pan），
    // 真正该变的只有"整张纸往哪挪"——所以给一个容器加 TranslateTransform，一个顶点都不碰。
    //
    // 能这么干的前提：**上一帧画的是全部部件**（_lastCulled == false）。
    // 8× 下画的是裁剪后的几十个，这时候平移会把"没画的对象"带进画面，必须重算。
    private readonly Canvas _content = new();

    /// <summary>③ 位图缓存：只有"下一帧可能只平移"时才挂（见 RebuildView 末尾）。</summary>
    private BitmapCache? _bitmapCache;

    /// <summary>最后一次**真正画图**时的 pan。平移帧的位移一律相对它算，避免误差逐帧累积。</summary>
    private double _drawPanX;
    private double _drawPanY;
    private bool _hasFrame;
    private double _lastPanX;
    private double _lastPanY;
    private double _lastZoom = double.NaN;
    private double _lastViewW;
    private double _lastViewH;
    private GeoLayer? _lastLayer;
    private ProjectionSettings _lastSettings;
    private bool _lastCulled;
    private bool _lastFrameTranslated;

    /// <summary>上一帧是不是"拖动中的粗版"（面只填色）。松手后必须补一帧细的。</summary>
    private bool _lastFrameCoarse;

    private const double CoarseTolerancePx = 4.0;

    /// <summary>松手后"空闲补细"的定时器（见 ScheduleFineUpgrade）。</summary>
    private DispatcherTimer? _fineTimer;

    /// <summary>下一次重画是不是"先给粗的"（滚轮缩放时置位，细版落地时清掉）。</summary>
    private bool _coarsePending;

    // ── 拖动余量（2026-10-26）────────────────────────────────────────────────
    //
    // 1× 全幅时整层都在视野里，平移只要动一下 RenderTransform（①）。可一旦放大，
    // 裁剪后的内容只覆盖视口，"往边上推"就会推出白边。做法是**多画一圈余量**：
    // 拖动的那几帧按"视口 + DragMarginPx"去查候选、给 _content 撑大同样的尺寸，
    // 于是平移在 ±余量之内都有东西可看；超出余量时才真正重画一次。
    //
    // 余量的取舍（2026-10-26 定）：它只花两样东西 —— 一次性的重画时间 与 位图缓存的内存。
    // 时间这边几乎不涨（1.5× 时 6,700 个部件已经在视野里，再要大一圈只多几百个）；
    // 涨的是内存，所以余量按**一屏**取：内容 = 3 屏 × 3 屏。
    //
    // 为什么是"一屏"：鼠标不可能拖出屏幕，所以**一次**拖动最多移动一屏。
    // 也就是说铺一屏余量，一次拖动里永远不用重画；要重画只会发生在"松开、再按下、
    // 继续拖"跨过一次屏宽之后。铺得比一屏再大，收益是 0（拖不到那么远）。

    /// <summary>上一帧画图时用的余量（平移距离从它算起）。x/y 分开，按视口尺寸取。</summary>
    private double _drawnMarginX;
    private double _drawnMarginY;

    /// <summary>
    /// 帧调度合并的"脏标记"：已经排上了一帧就不要再排。
    /// </summary>
    private bool _redrawPending;

    private Point _dragStart;          // 上一次鼠标位置（按下时定起点，移动时不断刷新）
    private bool _dragging;

    /// <summary>
    /// 悬停命中测试的节流器。
    /// 为什么需要它：MouseMove 一秒能来上百次，每次都遍历全部对象做距离计算，
    /// 数据一多就会卡成幻灯片。用 40ms ≈ 25fps 的定时器把命中测试
    /// 和渲染解耦，鼠标动得再快，判定的频率也是固定的。
    /// </summary>
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };

    /// <summary>最近一次要判定的鼠标位置，由 MouseMove 写入、由定时器读取。</summary>
    private Point _lastMousePosition;

    public MainWindow()
    {
        InitializeComponent();
        _hoverTimer.Tick += OnHoverTimerTick;

        _bitmapCache = new BitmapCache(VisualTreeHelper.GetDpi(this).DpiScaleX);
        if (Environment.GetEnvironmentVariable("GJV2_NOCACHE") != "1") _content.CacheMode = _bitmapCache;

        _content.IsHitTestVisible = false;
        MapCanvas.Children.Add(_content);

        // 定时器一开机就转起来：它是"空转"的（每 40ms 看一眼鼠标在哪，没东西就立刻返回），
        // 代价可以忽略，但省掉了"什么时候该 Start"这种容易漏掉的时序问题。
        // 注意：只有 OnCanvasMouseLeave 里 Stop 过它，所以这里不 Start 的话，
        // 鼠标一离开画布就再也不会恢复——悬停提示会永久失效。
        _hoverTimer.Start();
    }

    // ─────────────────────────────── 打开文件 ───────────────────────────────

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开地图数据（GeoJSON / shapefile）",
            Filter = LayerReader.BuildDialogFilter(),
            InitialDirectory = FindInitialDirectory(),
        };

        if (dialog.ShowDialog(this) != true) return;

        OpenFile(dialog.FileName);
    }

    /// <summary>
    /// 打开一个文件的完整流程（对话框之外的全部动作）。
    /// </summary>
    private void OpenFile(string filePath)
    {
        try
        {
            _rawLayer = LayerReader.ReadFile(filePath);
            // 顺序要紧：.prj 说明的是"文件里的坐标是哪套基准"，所以必须先选定源基准，再转换。
            ApplyPrjSuggestion(filePath);
            _layer = RebuildTransformedLayer();
        }
        catch (GeoJsonException ex)
        {
            // 解析类错误（含"文件不存在"）：这不是崩溃，是要显示给用户的一行中文话。
            _layer = null;
            _rawLayer = null;
            _bounds = null;
            _prjWarning = null;
            _hasFrame = false;
            _content.Children.Clear();
            _content.RenderTransform = Transform.Identity;
            HoverTip.Visibility = Visibility.Collapsed;
            ShowMessage(ex.Message);
            StatusText.Text = "打开失败";
            return;
        }
        catch (Exception ex)
        {
            // 兜底：任何没预料到的异常也变成一行提示，绝不让窗口崩掉。
            _rawLayer = null;
            _prjWarning = null;
            ShowMessage($"打开文件时出了意外：{ex.Message}");
            StatusText.Text = "打开失败";
            return;
        }

        if (_layer!.Features.Count == 0)
        {
            ShowMessage("这个文件里没有可显示的对象");
        }
        else
        {
            HideMessage();
        }
        _settings = Projection.BuildSettings(ProjectionBox.SelectedIndex, EllipsoidBox.SelectedIndex, ZoneWidthBox.SelectedIndex, _layer?.LonLatBounds ?? new GeoRect());
        // 换文件 = 重新 fit：先按新数据定范围，再按当前画布尺寸算出比例。
        // 不用另外调 SetViewport：Reset 的入参就是画布尺寸，它会一并记下并重算 BaseScale。
        _bounds = new GeoBounds(ProjectAllPoints());
        _bounds.Reset(MapCanvas.ActualWidth, MapCanvas.ActualHeight);

        // 图层换了 = 候选来源也要换。这一行必须和 _layer 在同一段代码里更新：
        // 少了它，画面会拿着**上一个文件**的对象去画（打开第二个文件时特别明显：一片空白或形状对不上）。
        _finder = new StrtreeFinder(_layer!, _settings);
        _lastTotalParts = CountParts(_layer!);

        RequestRedraw();
    }

    /// <summary>文件对话框默认目录：优先 Samples（测试数据就在那儿），否则用程序所在目录。</summary>
    private static string FindInitialDirectory()
    {
        var samples = Path.Combine(AppContext.BaseDirectory, "Samples");
        return Directory.Exists(samples) ? samples : AppContext.BaseDirectory;
    }


    internal void BenchOpen(string path) => OpenFile(path);

    /// <summary>
    /// --bench 用的"强制全量重画"：清掉"上一帧可复用"的标记，量到的永远是重算的那条路。
    /// 否则第二次调用会被 ① 优化成"只平移 0 像素"，量出 0 ms 看着像奇迹。
    /// </summary>
    internal void BenchRebuild()
    {
        _hasFrame = false;
        RebuildView();
    }

    internal void BenchBeginDrag()
    {
        _dragging = true;
        BenchRebuild();
    }

    internal void BenchEndDrag()
    {
        EndDrag();
    }

    internal void BenchHover(System.Windows.Point position) => UpdateHoverTip(position);

    /// <summary>给 _bench\drag-check 用：模拟一次滚轮（factor 倍，锚点在屏幕坐标）。</summary>
    internal void BenchZoom(double factor, double x, double y)
    {
        if (_bounds is null || !_bounds.HasViewport) return;
        var target = Math.Clamp(_bounds.Zoom * factor, GeoBounds.MinZoom, GeoBounds.MaxZoom);
        _bounds.ZoomAt(x, y, target / _bounds.Zoom);
        _coarsePending = true;
        RequestRedraw();
    }

    internal bool BenchRedrawPending => _redrawPending;

    internal (double Clear, double Query, double Draw) BenchBreakdown => (_lastBreakdownClear, _lastBreakdownQuery, _lastBreakdownDraw);

    /// <summary>
    /// 全量重画的次数（只给探针看）。
    /// </summary>
    internal int BenchRebuildCount { get; private set; }

    internal int BenchFrameCount { get; private set; }

    internal (GeoLayer? Layer, ProjectionSettings Settings, GeoBounds? Bounds) BenchState
        => (_layer, _settings, _bounds);

    /// <summary>
    /// 打开 .shp 时读旁边的 .prj：能认出来就把「源基准」替用户选好，认不出/是投影/没有声明就只留一句话。
    /// </summary>
    private void ApplyPrjSuggestion(string filePath)
    {
        _prjWarning = null;
        if (!string.Equals(Path.GetExtension(filePath), ".shp", StringComparison.OrdinalIgnoreCase)) return;

        var suggestion = PrjSuggestion.For(PrjReader.TryRead(filePath));
        _prjWarning = suggestion.Warning;
        if (suggestion.Datum is null) return;

        var index = Array.IndexOf(DatumCatalog.All, suggestion.Datum);
        if (index < 0 || SourceDatumBox.SelectedIndex == index) return;

        _suppressSourceDatumChanged = true;
        try
        {
            SourceDatumBox.SelectedIndex = index;
        }
        finally
        {
            _suppressSourceDatumChanged = false;
        }
    }

    // ───────────────────────── 基准（基准 ≠ 椭球） ─────────────────────────

    /// <summary>
    /// 按工具栏「源基准」把 _rawLayer 转换到 CGCS2000，返回**显示用**的那个图层。
    /// </summary>
    private GeoLayer? RebuildTransformedLayer()
    {
        if (_rawLayer is null) return null; 
        var source = DatumCatalog.All[SourceDatumBox.SelectedIndex]; 
        return DatumTransform.TransformLayer(_rawLayer, source, DatumCatalog.Cgcs2000, 0.0);
    }
        

    /// <summary>换源基准：坐标整体挪了几百米，所以要重新转换 → 重新 fit → 重画。</summary>
    private void OnSourceDatumChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // XAML 是一个节点一个节点解析的：这个下拉框在窗口构造时也会触发一次 —— 那时还没有文件。
        if (_rawLayer is null) return;

        // 程序性改选（ApplyPrjSuggestion 里）不需要在这里再做一遍转换 —— 调用方会按正确顺序接着做。
        if (_suppressSourceDatumChanged) return;
        _layer = RebuildTransformedLayer();
        _bounds = new GeoBounds(ProjectAllPoints());
        _bounds.Reset(MapCanvas.ActualWidth, MapCanvas.ActualHeight);
        // 换基准会换掉整个 _layer（重新构造出来的新对象），候选来源必须跟着换：
        // 旧 _finder 里握着的是**上一份**图层，它的点阵还是旧基准的坐标。
        if (_layer is not null) _finder = new StrtreeFinder(_layer, _settings);
        if (_layer is not null) _lastTotalParts = CountParts(_layer);

        RequestRedraw();
        
    }

    // ─────────────────────────────── 投影模式 ───────────────────────────────

    private void OnProjectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // XAML 是一个节点一个节点解析的：解析到"投影"这个下拉框并读到 SelectedIndex 时，
        // 它后面那两个下拉框（椭球、带宽）**还没有被创建**，读它们就是 null。
        // 这不是"理论上可能"，是每次启动都会发生的事 —— 少了这一句就是启动即崩。
        if (EllipsoidBox is null || ZoneWidthBox is null) return;

        _settings = Projection.BuildSettings(ProjectionBox.SelectedIndex, EllipsoidBox.SelectedIndex, ZoneWidthBox.SelectedIndex, _layer?.LonLatBounds ?? new GeoRect());


        if (_layer is null) return;

        // 为什么切模式要重新 fit：两种投影下的"数据范围"不一样（墨卡托把高纬拉长了），
        // 沿用旧的 baseScale，切过去形状就是歪的。切投影 = 换了一把尺子，必须重新量。
        _bounds = new GeoBounds(ProjectAllPoints());
        _bounds.Reset(MapCanvas.ActualWidth, MapCanvas.ActualHeight);
        //_finder需要重建，因为树里存的是投影后的框，换投影 = 换坐标系
        _finder = new StrtreeFinder(_layer!, _settings);
        _lastTotalParts = CountParts(_layer!);
        RequestRedraw();
    }

    /// <summary>把图层里所有点按当前设置投影一遍。fit 和渲染都以它为准。</summary>
    private IEnumerable<(double X, double Y)> ProjectAllPoints()
        => _layer!.AllPoints().Select(p => Projection.Project(p, _settings));

    // ─────────────────────────────── 缩放与平移 ───────────────────────────────

    /// <summary>
    /// 滚轮缩放，以鼠标位置为锚点（鼠标底下的数据点不动）。
    /// 每格滚轮乘 1.5（2026-10-26 从 1.15 改：1.5 是"半档"，两格正好 2.25×，
    /// 和 LOD 的 2 的幂分桶对得上，拖动缓存的余量也能按档来算）；
    /// 倍数夹在 GeoBounds.MinZoom ~ MaxZoom 之间；
    /// 夹过的目标值要先算回本步的有效因子，否则到了上限继续滚，画面会"漂"。
    /// </summary>
    private void OnCanvasMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_bounds is null || !_bounds.HasViewport) return;

        var pos = e.GetPosition(MapCanvas);

        var steps = e.Delta / 120.0;
        var factor = Math.Pow(1.5, steps);
        var target = Math.Clamp(_bounds.Zoom * factor, GeoBounds.MinZoom, GeoBounds.MaxZoom);
        var effective = target / _bounds.Zoom;
        _bounds.ZoomAt(pos.X, pos.Y, effective);

        // 缩放要重算全部顶点的屏幕坐标，细版 1.4 s。先给一帧粗的（~0.2 s），
        // 空闲 250 ms 再补细 —— 和拖动用的是同一条"粗→细"链路。
        _coarsePending = true;
        RequestRedraw();
    }

    private void OnCanvasMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 为什么要手动抢焦点：不抢的话滚轮事件可能落在别的控件上（比如刚点过的下拉框），
        // 表现就是"滚了没反应"。这一行能省掉一个特别难查的 bug。
        MapCanvas.Focus();
        Keyboard.Focus(MapCanvas);

        _dragStart = e.GetPosition(MapCanvas);
        _dragging = true;
        MapCanvas.CaptureMouse();   // 抓住鼠标：往窗口外拖动也不会丢事件
        MapCanvas.Cursor = Cursors.SizeAll;

        // 按下的这一帧要把余量画出来（放大档才需要，1× 全幅本来什么都画了），
        // 否则第一次移动就没东西可平移。
        if (_lastCulled) RequestRedraw();
    }

    private void OnCanvasMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndDrag();
    }

    /// <summary>
    /// 鼠标移动：既要平移，又要触发悬停判定。
    /// 平移用**两次事件的差值**（本次位置 - _dragStart）累加到 _bounds.PanX/PanY，
    /// 然后立刻把 _dragStart 更新成当前点。
    /// 为什么不能直接拿绝对坐标当偏移量：绝对坐标里含着"按下之前已经平移过多少"，
    /// 直接赋值就会看到鼠标一动、地图猛跳一下。
    /// </summary>
    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        // 兜底重启：OnCanvasMouseLeave 里 Stop 过定时器，如果这里不重启，
        // 鼠标一旦离开过画布，悬停提示就永久失效（"动一下才好、再进来就再也不弹"）。
        // 放在 MouseMove 而不是 MouseEnter：MouseEnter 只在真正跨界时触发一次，
        // 而 MouseMove 每次都在，用最便宜的方式保证"手在画布上时定时器一定在转"。
        if (!_hoverTimer.IsEnabled) _hoverTimer.Start();

        var pos = e.GetPosition(MapCanvas);
        _lastMousePosition = pos;

        if (_dragging && _bounds is not null)
        {
            _bounds.PanX += pos.X - _dragStart.X;
            _bounds.PanY += pos.Y - _dragStart.Y;
            _dragStart = pos;
            RequestRedraw();
        }
        else
        {
            UpdateCoordinateReadout(pos);
        }
    }

    /// <summary>以固定间隔（40ms）做一次命中测试，避免每帧都遍历全部对象。</summary>
    private void OnHoverTimerTick(object? sender, EventArgs e)
    {
        if (_dragging) return;
        UpdateHoverTip(_lastMousePosition);
    }

    private void OnCanvasMouseLeave(object sender, MouseEventArgs e)
    {
        // 鼠标离开画布就收掉提示，否则它会孤零零停在原地
        _hoverTimer.Stop();
        HoverTip.Visibility = Visibility.Collapsed;
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        MapCanvas.ReleaseMouseCapture();
        MapCanvas.Cursor = Cursors.Arrow;

        // 松开后**不再立刻重画**：
        //   · 如果这一帧是粗版，就排一个"空闲就补细"的定时器（250 ms）。中途有任何新动作
        //     （再按下、滚轮）都会被 RequestRedraw() 抢掉，白算的活不会发生。
        //   · 如果这一帧本来就是细版（带余量），余量那一圈留着不影响正确性 —— 缓存里多出来的
        //     像素不要钱，反而让下一次按下立刻就能拖。
        if (_lastFrameCoarse) ScheduleFineUpgrade();
    }

    /// <summary>
    /// 空闲 250 ms 后补一帧细版。用 DispatcherTimer 而不是立即画：松手之后用户常常马上又来一下，
    /// 立刻花掉那 1 秒纯属浪费。
    /// </summary>
    private void ScheduleFineUpgrade()
    {
        _fineTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _fineTimer.Stop();
        _fineTimer.Tick -= OnFineTimerTick;
        _fineTimer.Tick += OnFineTimerTick;
        _fineTimer.Start();
    }

    private void OnFineTimerTick(object? sender, EventArgs e)
    {
        _fineTimer?.Stop();
        // 还在拖就别补了：RequestRedraw 会把它画成粗版，白忙。
        if (_dragging || !_lastFrameCoarse) return;

        // 滚轮引起的粗版同理：这一帧就是为了补细，先把"要粗的"意图清掉。
        _coarsePending = false;
        RequestRedraw();
    }

    // ─────────────────────────────── 悬停提示 ───────────────────────────────

    /// <summary>
    /// 按当前鼠标位置做一次命中测试：命中就填文字、显示提示浮层，没命中就收起来。
    /// HoverTip 设了 IsHitTestVisible=False，所以它不会反过来抢鼠标事件。
    /// </summary>
    private void UpdateHoverTip(Point pos)
    {
        if (_layer is null || _bounds is null || !_bounds.HasViewport) return;

        var features = new List<GeoFeature>();
        var seen = new HashSet<GeoFeature>();
        foreach (var candidate in QueryAround(pos))
            if (seen.Add(candidate.Feature)) features.Add(candidate.Feature);

        var candidates = new GeoLayer { FileName = _layer.FileName, Features = features };
        var hit = HitTester.HitTest(candidates, _settings, _bounds, pos.X, pos.Y);

        if (hit is null)
        {
            HoverTip.Visibility = Visibility.Collapsed;
            return;
        }

        var lines = hit.Properties.Take(3).Select(kv => $"{kv.Key}: {kv.Value}");
        HoverText.Text = hit.DisplayName + "\n" + string.Join("\n", lines);

        // 框子的大小由它自己按文字量出来（XAML 里设了 HorizontalAlignment/VerticalAlignment=Left/Top，
        // 所以它是"包住文字"而不是被拉伸填满画布）。这里只负责决定把它摆在哪儿。
        HoverTip.Visibility = Visibility.Visible;
        PositionHoverTip(pos);
    }

    /// <summary>
    /// 决定提示框摆在哪：优先鼠标右下方，右边放不下就翻到左边，还放不下就退回右下方保原样。
    /// 纵向同理（下 → 上 → 下）。
    /// </summary>
    private void PositionHoverTip(Point pos)
    {
        const double gapX = 14;   // 和鼠标拉开一点距离，别被光标压住
        const double gapY = 18;

        // 必须先把 Visibility 设成 Visible 再调这里：Collapsed 的元素不参与布局，
        // ActualWidth/ActualHeight 会是 0（或者上一次的旧值），拿它算出来的位置全是错的。
        //
        // 为什么还要 UpdateLayout()：异步的布局是在方法返回之后才跑的，所以刚设完 Text/Visible
        // 的这一刻，ActualWidth 里装的还是"上一次"的尺寸。第一次悬停时它甚至是 0 ——
        // 那时右边放不下、翻到左边会算出 pos.X - 14 - 0，框子直接压在光标上。
        // 强制量一次，代价只是重新量这个浮层（不是整窗），换来"位置一次就对、不闪不跳"。
        HoverTip.UpdateLayout();

        var tipWidth = HoverTip.ActualWidth;
        var tipHeight = HoverTip.ActualHeight;
        var canvasWidth = MapCanvas.ActualWidth;
        var canvasHeight = MapCanvas.ActualHeight;

        // 横向：鼠标右边放不下 → 试试鼠标左边 → 两边都放不下 → 保原样（右下方）
        var x = pos.X + gapX;
        if (x + tipWidth > canvasWidth)
        {
            var flipped = pos.X - gapX - tipWidth;
            if (flipped >= 0) x = flipped;
        }

        // 纵向：同样的三段逻辑
        var y = pos.Y + gapY;
        if (y + tipHeight > canvasHeight)
        {
            var flipped = pos.Y - gapY - tipHeight;
            if (flipped >= 0) y = flipped;
        }

        HoverTip.Margin = new Thickness(x, y, 0, 0);
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_bounds is null) return;

        // 只把"画布现在多大"告诉 GeoBounds —— 它只记尺寸、**不动 BaseScale**，
        // 所以拉窗口时已经滚到的位置和比例都不会被改。
        //
        // 光"不改比例"还不够：画布少掉的那几十像素总得从画面里裁掉，得说清从哪里裁。
        // GeoBounds 里按"内容整体挪半个尺寸差"来补 pan，实际效果就是**左上角钉住**：
        // 原来在左上角看到的东西，拉完窗口还在左上角；少掉的像素全从右边和下边去掉。
        // （推导和"为什么必须用 pan 补"写在 SetViewport 的注释里。）
        _bounds.SetViewport(e.NewSize.Width, e.NewSize.Height);
        RequestRedraw();
    }

    // ─────────────────────────────── 重画 ───────────────────────────────

    /// <summary>
    /// 唯一的"重画"入口。
    ///
    /// 两条路：
    ///   ① **只平移**（_lastCulled == false 且只有 pan 变了）：不动顶点，只改 _content 的 TranslateTransform；
    ///   ② **全量重画**：清空 → 查候选 → 重新投影 → 渲染 → 更新状态栏。
    ///
    /// 为什么平移不能无条件走 ①：上一帧若被裁剪过，画的是视野内的几十个部件，
    /// 一平移就会把"没画的对象"带进画面（漏画）。所以 ① 的前提写死在 CanTranslateOnly() 里。
    /// </summary>
    private void RebuildView()
    {
        if (_layer is null || _bounds is null || !_bounds.HasViewport) return;

        if (CanTranslateOnly())
        {
            // ① 图元复用：ScreenX = viewport/2 + pan + zoom*(x-center)，zoom 不变时
            //    屏幕位移就等于 pan 的增量 —— 一个平移矩阵就够了，8,531 个顶点一个都不碰。
            //
            // 位移要**对准设备像素**：位图缓存是栅格化的，落在半像素上会被双线性重采样，
            // 整幅图的边线一起发糊（pan-check 里表现为"全部边界都不同"）。所以按 DPI 取整。
            // 位移从"上一次真正画图时的 pan"算起，误差不会随帧数累积。
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var dx = Math.Round((_bounds.PanX - _drawPanX) * dpi) / dpi;
            var dy = Math.Round((_bounds.PanY - _drawPanY) * dpi) / dpi;
            // 画的时候整块内容是往右下挪了 (_drawnMarginX, _drawnMarginY) 才落进 enlarged canvas 的，
            // 平移要在那个基准上继续加，否则一进拖动画面就跳一屏。
            _content.RenderTransform = new TranslateTransform(_drawnMarginX + dx, _drawnMarginY + dy);

            _lastPanX = _bounds.PanX;
            _lastPanY = _bounds.PanY;
            _lastFrameTranslated = true;

            // 一帧的三笔账全是 0：没清空、没查候选、没重画。
            _lastBreakdownClear = 0;
            _lastBreakdownQuery = 0;
            _lastBreakdownDraw = 0;
            BenchFrameCount++;

            UpdateStatus();
            if (_dragging) HoverTip.Visibility = Visibility.Collapsed;
            return;
        }

        _lastFrameTranslated = false;

        var tClear = System.Diagnostics.Stopwatch.GetTimestamp();
        // 先把上一帧的平移清掉：新画的是绝对屏幕坐标，被残留的变换叠一次就会整体偏移。
        _content.RenderTransform = Transform.Identity;
        _content.Children.Clear();

        var marginX = _dragging ? MapCanvas.ActualWidth : 0;
        var marginY = _dragging ? MapCanvas.ActualHeight : 0;
        _content.Width = MapCanvas.ActualWidth + 2 * marginX;
        _content.Height = MapCanvas.ActualHeight + 2 * marginY;
        Canvas.SetLeft(_content, -marginX);
        Canvas.SetTop(_content, -marginY);
        _drawnMarginX = marginX;
        _drawnMarginY = marginY;

        _lastBreakdownClear = (System.Diagnostics.Stopwatch.GetTimestamp() - tClear) * 1000.0
            / System.Diagnostics.Stopwatch.Frequency;

        var tQuery = System.Diagnostics.Stopwatch.GetTimestamp();
        var parts = QueryCandidates(marginX, marginY);
        _lastCandidates = parts.Count;
        var tDraw = System.Diagnostics.Stopwatch.GetTimestamp();

        var coarse = _dragging || _coarsePending;
        _lastFrameCoarse = coarse;

        if (coarse)
            IndexedRenderer.RenderCoarse(parts, _settings, _bounds, _content, CoarseTolerancePx, drawStrokes: false);
        else if (Environment.GetEnvironmentVariable("GJV2_NOMERGE") == "1")
            IndexedRenderer.RenderUnmerged(parts, _settings, _bounds, _content);
        else
            IndexedRenderer.Render(parts, _settings, _bounds, _content);
        var tEnd = System.Diagnostics.Stopwatch.GetTimestamp();

        // 只给 --bench 看：把"一帧"拆成"找候选"和"画候选"两段。
        // 悬停那一行也靠它解释——悬停只跑前面那一段，跟画图无关。
        _lastBreakdownQuery = (tDraw - tQuery) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _lastBreakdownDraw = (tEnd - tDraw) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        // 记下这一帧的"身份"：下一次判断能不能只平移，全看这几个字段。
        _hasFrame = true;
        _lastLayer = _layer;
        _lastSettings = _settings;
        _lastZoom = _bounds.Zoom;
        _lastPanX = _bounds.PanX;
        _lastPanY = _bounds.PanY;
        _drawPanX = _bounds.PanX;
        _drawPanY = _bounds.PanY;
        _lastViewW = _bounds.ViewportWidth;
        _lastViewH = _bounds.ViewportHeight;
        _lastCulled = parts.Count < _lastTotalParts;

        // ③ 位图缓存：只要这一帧不是"全量全部"就挂上——挂上才有资格走"只平移"。
        // 分辨率跟屏幕走：试过拖动期间降到 1.0 省内存，但重画耗时几乎没变（瓶颈是每个图元一次光栅化，
        // 不是像素数），却把边缘糊掉（像素差 19.8k → 33k，最大通道差 20 → 63）。所以不降。
        _bitmapCache ??= new BitmapCache();
        _bitmapCache.RenderAtScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _content.CacheMode = _bitmapCache;

        // 拖动的那几帧：整块内容往右下挪一个余量，落进撑大的 _content 里。
        // （'只平移'时在这个基准上继续加 dx/dy，见 RebuildView 开头。）
        if (marginX > 0 || marginY > 0) _content.RenderTransform = new TranslateTransform(marginX, marginY);

        BenchRebuildCount++;
        BenchFrameCount++;

        // 细版已经落地 → "要粗的"这个意图用完了，清掉（否则以后每一帧都是粗的）。
        if (!coarse) _coarsePending = false;

        // 缩放那一帧是粗的：紧接着排一次"空闲补细"，让画面自己变清楚。
        if (coarse && !_dragging) ScheduleFineUpgrade();

        if (_layer.Features.Count == 0) ShowMessage("这个文件里没有可显示的对象");
        else HideMessage();

        UpdateStatus();

        if (_dragging) HoverTip.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 能不能"只平移"：图层 / 投影 / 缩放 / 视口都没变，**且当前视野仍落在上一帧画过的那块里**。
    /// 最后一条是 2026-10-26 加的：拖动时会多画一圈余量（`DragMarginPx`），
    /// 平移只要还在余量范围内，画过的东西就都在，直接移位图就行；出去了才重画。
    /// 这条判断宁严勿宽 —— 错了是"画面缺一块"，不会报错。
    /// </summary>
    private bool CanTranslateOnly()
    {
        if (!_hasFrame) return false;
        // 上一帧是粗版（拖动中画的）且已经松开手 → 不许平移，必须走重画，好让细版落地。
        // 还在拖动时不受这条限制：粗版照样能平移，这正是余量要的效果。
        if (_lastFrameCoarse && !_dragging) return false;
        if (!ReferenceEquals(_lastLayer, _layer)) return false;
        if (!_lastSettings.Equals(_settings)) return false;
        if (_lastZoom != _bounds!.Zoom) return false;
        if (_lastViewW != _bounds.ViewportWidth || _lastViewH != _bounds.ViewportHeight) return false;

        // 上一帧没被裁剪且没铺余量 → 全部部件都画了、内容就在原位（1× 那档），随便平移。
        if (!_lastCulled && _drawnMarginX == 0 && _drawnMarginY == 0) return true;

        // 铺了余量（放大档）→ 只允许在余量范围内平移。这里判**屏幕位移**，
        // 比数据矩形包含更好懂：余量就是"还能假装没变"的像素数。
        // 少算 26 px 是把点/线外扩的那一圈还回去（内容在贴边处本来就不完整）。
        if (_drawnMarginX <= 0 || _drawnMarginY <= 0) return false;
        return Math.Abs(_bounds.PanX - _drawPanX) <= _drawnMarginX - MaxPointRadiusPx
            && Math.Abs(_bounds.PanY - _drawPanY) <= _drawnMarginY - MaxPointRadiusPx;
    }

    // ────────────────────────── 候选来源 / 帧调度 ──────────────────────────

    /// <summary>
    /// 当前视野对应的**平面坐标**矩形（投影之后那套，不是经纬度）。
    /// </summary>
    private static (double MinX, double MinY, double MaxX, double MaxY) VisibleRect(GeoBounds bounds)
    {
        var (x0, y0) = bounds.ToData(0, 0);
        var (x1, y1) = bounds.ToData(bounds.ViewportWidth, bounds.ViewportHeight);
        return (Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
    }

    /// <summary>
    /// 只查光标周围一小块的候选（悬停专用）。
    /// </summary>
    private IReadOnlyList<GeoPartHit> QueryAround(Point pos)
    {
        if (_finder is null || _bounds is null) return AllParts(_layer!);

        var (x, y) = _bounds.ToData(pos.X, pos.Y);
        var (x1, _) = _bounds.ToData(pos.X + 1, pos.Y);
        var perPixel = Math.Abs(x1 - x);
        var d = (8.0 + MaxPointRadiusPx) * perPixel;

        return _finder.Query(x - d, y - d, x + d, y + d);
    }

    /// <summary>
    /// 查询当前视野里的候选部件。悬停和渲染共用这一条路径 ——
    /// 两处各写一遍"怎么取候选"，迟早会出现"画了但点不中"或反过来的鬼故事。
    /// </summary>
    /// <param name="marginX">视野左右各多要多少像素（拖动时 = 一个视口宽）。</param>
    /// <param name="marginY">视野上下各多要多少像素（拖动时 = 一个视口高）。</param>
    private IReadOnlyList<GeoPartHit> QueryCandidates(double marginX = 0, double marginY = 0)
    {
        if (_finder is null || _bounds is null) return AllParts(_layer!);

        // 视野矩形是**没有留白**的；点半径最大 26 px、线宽最大 6 px，
        // "包围盒刚好在视野外一点点"的对象仍有一半压在画面里。所以查询和裁剪都按外扩后的矩形走。
        var (minX, minY, maxX, maxY) = PadRect(
            VisibleRect(_bounds), _bounds, MaxPointRadiusPx + marginX, MaxPointRadiusPx + marginY);
        var candidates = _finder.Query(minX, minY, maxX, maxY);

        if (candidates.Count * GateFactor <= _lastTotalParts)
            return ViewportCuller.Cull(candidates, _settings, minX, minY, maxX, maxY);

        return candidates;
    }

    private static readonly int GateFactor =
        int.TryParse(Environment.GetEnvironmentVariable("GJ_GATE"), out var gate) && gate > 0 ? gate : 20;

    /// <summary>点半径上限（px）：`SimpleRenderer` 里 `SizeForZoom(5.5, zoom, 3.0, 26.0)` 的上限。</summary>
    private const double MaxPointRadiusPx = 26.0;

    /// <summary>把平面矩形按 N 个像素换算成平面单位外扩（x/y 可以不同）。</summary>
    private static (double MinX, double MinY, double MaxX, double MaxY) PadRect(
        (double MinX, double MinY, double MaxX, double MaxY) rect, GeoBounds bounds, double pixelsX, double pixelsY = -1)
    {
        if (pixelsY < 0) pixelsY = pixelsX;

        var (x0, y0) = bounds.ToData(0, 0);
        var (x1, y1) = bounds.ToData(1, 1);
        var perPixelX = Math.Abs(x1 - x0);   // 一个屏幕像素等于多少平面单位
        var perPixelY = Math.Abs(y1 - y0);
        var dx = pixelsX * perPixelX;
        var dy = pixelsY * perPixelY;
        return (rect.MinX - dx, rect.MinY - dy, rect.MaxX + dx, rect.MaxY + dy);
    }

    /// <summary>
    /// 没有 _finder 时的兜底：直接把全部部件当成候选。
    /// 复用 LinearFinder 而不是在这里再抄一遍"Parts 为空就取 Points"——那句话只允许有一个出处。
    /// </summary>
    // 非静态：要用实例字段 _settings（LinearFinder 现在也要它来投影）
    private IReadOnlyList<GeoPartHit> AllParts(GeoLayer layer)
        => new LinearFinder(layer, _settings).Query(double.MinValue, double.MinValue, double.MaxValue, double.MaxValue);

    /// <summary>
    /// 数一数这一层有多少个**部件**（状态栏分母用）。
    /// 口径必须和 `LinearFinder` 的"部件的定义"一致：`Parts` 非空就数 `Parts.Count`，否则算一个。
    /// 只在换图层时调一次，不进每帧的路径。
    /// </summary>
    private static int CountParts(GeoLayer layer)
    {
        var total = 0;
        foreach (var feature in layer.Features)
        {
            var parts = feature.Geometry.Parts.Count;
            total += parts > 0 ? parts : 1;
        }
        return total;
    }

    /// <summary>
    /// "要重画"的请求。拖动/滚轮/尺寸变化都调它，不再直接调 RebuildView()。
    ///
    /// 语义：当前这一帧已经排上了，后续事件只更新位置（_dragStart / _bounds），不再排队。
    /// 于是无论鼠标快慢，**一帧最多画一次**。这是第六节改动 2 的 ⑤。
    /// </summary>
    private void RequestRedraw()
    {
        // 有人要新的一帧了 → 之前排的"空闲补细"作废（那一帧马上就会被新的取代）。
        _fineTimer?.Stop();
        if (_redrawPending) return;
        _redrawPending = true;
        CompositionTarget.Rendering += OnFrameTick;
    }

    /// <summary>
    /// 帧时钟。只做两件事：摘掉自己、画一次。
    /// 必须在画之前就 -=：RebuildView() 里若有任何间接的再次请求，不会在同一个 tick 里递归画。
    /// </summary>
    private void OnFrameTick(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnFrameTick;
        _redrawPending = false;
        RebuildView();
    }

    /// <summary>刷新状态栏：文件名 / 对象数 / 跳过的对象数 / 缩放倍数 / 当前投影。</summary>
    private void UpdateStatus()
    {
        if (_layer is null)
        {
            StatusText.Text = "还没有打开文件";
            return;
        }

        var modeName = _settings.DisplayName;
        var skipped = _layer.SkippedCount > 0 ? $"　·　跳过了 {_layer.SkippedCount} 个不支持的对象" : "";
        var zoom = _bounds is null ? "" : $"　·　缩放 {_bounds.Zoom:0.00}×";

        var text = $"{_layer.FileName}　·　{_layer.Features.Count} 个对象{skipped}{zoom}　·　投影：{modeName}";
        
        Title = $"GeoJsonViewer — {_layer.FileName}（{_layer.Features.Count} 个对象）";
        if (_settings.Mode == ProjectionMode.GaussKruger)
        {
            if (_layer.LonLatBounds is null)
            {
                text += "　·　该文件没有经纬度边界，按照0°来处理";
            }
            else if(!GaussKruger.FitsInZone(_layer.LonLatBounds.Value.MinLon, _layer.LonLatBounds.Value.MaxLon, _settings.CentralMeridianDegrees, _settings.ZoneWidthDegrees))
            {
                text += "　·　警告：该文件的经纬度边界不在当前投影带内";
            }
            
        }
        if (_finder is not null)
        {
            // 分子是部件条数、分母也是部件数（口径一致）；对象数在标题栏里单列。
            // ① 之后要区分两种帧：重算过的才是真候选数，纯平移帧是"沿用上一帧、只是挪了位置"。
            text += _lastFrameTranslated
                ? $"  ·  {_lastCandidates}/{_lastTotalParts}（平移，未重算）"
                : $"  ·  候选：{_lastCandidates}/{_lastTotalParts}";
        }
        if (_prjWarning is not null)
        {
            text += $"　·　{_prjWarning}";
        }

        StatusText.Text = text;
    }

    /// <summary>把鼠标位置换算成经纬度显示在状态栏（缩放锚点对不对，看这个数字最直观）。</summary>
    private void UpdateCoordinateReadout(Point pos)
    {
        if (_layer is null || _bounds is null || !_bounds.HasViewport) return;

        try
        {
            var (x, y) = _bounds.ToData(pos.X, pos.Y);
            double lon, lat, nx, ny;
            var neighbourCm = double.NaN;
            if (_settings.Mode == ProjectionMode.GaussKruger)
            {
                var p = GaussKruger.Inverse(y, x, _settings.Ellipsoid, _settings.CentralMeridianDegrees);
                (lon, lat) = (p.Lon, p.Lat);
                neighbourCm = _settings.CentralMeridianDegrees + _settings.ZoneWidthDegrees;
                (nx, ny) = GaussKruger.ChangeZone(y, x, _settings.Ellipsoid, _settings.CentralMeridianDegrees, neighbourCm);
            }
            else
            {
                (lon, lat) = (x, y);   // 线性模式下平面数值就等于经纬度；墨卡托模式下这里只是近似
                (nx, ny) = (x, y);
            }
            CursorPositionText.Text = $"光标(°)：({lon:0.0000}, {lat:0.0000})";

            if (!double.IsNaN(neighbourCm)
                && lon < neighbourCm + _settings.ZoneWidthDegrees / 2
                && lon > neighbourCm - _settings.ZoneWidthDegrees / 2)
            {
                CursorPositionText.Text += $"　·　东邻带中央经线 {neighbourCm}°：(东 {ny:0.0000} m，北 {nx:0.0000} m)";
            }
        }
        catch (Exception ex)
        {
            // 光标读数只是显示用的信息：任何异常都不该让鼠标一动就崩掉窗口。
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    // ─────────────────────────────── 提示与错误 ───────────────────────────────

    private void ShowMessage(string text)
    {
        MessageText.Text = text;
        MessagePanel.Visibility = Visibility.Visible;
    }

    private void HideMessage() => MessagePanel.Visibility = Visibility.Collapsed;
}
