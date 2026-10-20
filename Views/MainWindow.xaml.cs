namespace GeoJsonViewer.Views;

using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GeoJsonViewer.Logic;
using GeoJsonViewer.Models;
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
    ///
    /// 下拉框的 SelectionChanged 分不清"用户点的"和"代码设的"。打开 shapefile 时我们要替用户选基准，
    /// 若不放这个旗子，OnSourceDatumChanged 会插进 OnOpenClick 中间跑一次 ——
    /// 那时 _layer 还是上一个文件的、_bounds 也还没按新文件重算，转出来是错的。
    /// </summary>
    private bool _suppressSourceDatumChanged;

    /// <summary>.prj 给出的那句话（没有 .prj、是投影坐标系、认不出基准……）；null = 没话要说。</summary>
    private string? _prjWarning;

    private GeoBounds? _bounds;
    private ProjectionSettings _settings = ProjectionSettings.Linear;

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

        try
        {
            // 读出来的这一份是「原始图层」：基准转换永远从它出发（理由见 RebuildTransformedLayer）。
            // 后缀分派在 Logic/LayerReader 里，窗口不再关心“这是 geojson 还是 shp”。
            _rawLayer = LayerReader.ReadFile(dialog.FileName);
            // 顺序要紧：.prj 说明的是"文件里的坐标是哪套基准"，所以必须先选定源基准，再转换。
            ApplyPrjSuggestion(dialog.FileName);
            _layer = RebuildTransformedLayer();
        }
        catch (GeoJsonException ex)
        {
            // 解析类错误（含"文件不存在"）：这不是崩溃，是要显示给用户的一行中文话。
            _layer = null;
            _rawLayer = null;
            _bounds = null;
            _prjWarning = null;
            MapCanvas.Children.Clear();
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
        
        RebuildView();
    }

    /// <summary>文件对话框默认目录：优先 Samples（测试数据就在那儿），否则用程序所在目录。</summary>
    private static string FindInitialDirectory()
    {
        var samples = Path.Combine(AppContext.BaseDirectory, "Samples");
        return Directory.Exists(samples) ? samples : AppContext.BaseDirectory;
    }

    /// <summary>
    /// 打开 .shp 时读旁边的 .prj：能认出来就把「源基准」替用户选好，认不出/是投影/没有声明就只留一句话。
    ///
    /// 只有 .shp 才有 .prj（GeoJSON 的坐标系写在标准里，永远是 WGS84 经纬度）。
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
    ///
    /// 为什么输入必须是 _rawLayer、而不是 _layer：
    ///   TransformLayer 是纯函数，但它**不幂等** —— 在已经转换过的图层上再按同一个源基准转一次，
    ///   等于把偏移量又叠加一遍（北京54 → CGCS2000 在北京那种位置一次约 198 米，转两次就快 400 米）。
    ///   自检 [13] 里有一条专门量这件事。"来回切下拉框能不能回到原样"全看这一条。
    ///
    /// 为什么目标固定是 CGCS2000：
    ///   本项目的投影（高斯 / 墨卡托）都按 CGCS2000 算。源基准只说明"文件里的坐标是哪套基准"，
    ///   统一到 CGCS2000 之后，投影、fit、命中测试就都能用同一套数字。
    ///
    /// 下拉框的项顺序和 DatumCatalog.All 一一对应（0=CGCS2000, 1=WGS84, 2=北京54, 3=西安80）；
    /// 大地高本项目一律按 0（"所有点都贴在地面上"这个前提写在 TransformLayer 的签名上）。
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
        RebuildView();
        
    }

    // ─────────────────────────────── 投影模式 ───────────────────────────────

    private void OnProjectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // XAML 是一个节点一个节点解析的：解析到"投影"这个下拉框并读到 SelectedIndex 时，
        // 它后面那两个下拉框（椭球、带宽）**还没有被创建**，读它们就是 null。
        // 这不是"理论上可能"，是每次启动都会发生的事 —— 少了这一句就是启动即崩。
        if (EllipsoidBox is null || ZoneWidthBox is null) return;

        // 现在是两项时的写法。加了第三项「高斯-克吕格」以后，这里要改成按三个下拉框建设置：
        //   高斯模式读椭球下拉 + 带宽下拉，中央经线则是拿数据的经度范围中点自动选出来的。
        // 下拉框的项顺序和枚举顺序一一对应：0=Linear, 1=WebMercator, 2=GaussKruger
        _settings = Projection.BuildSettings(ProjectionBox.SelectedIndex, EllipsoidBox.SelectedIndex, ZoneWidthBox.SelectedIndex, _layer?.LonLatBounds ?? new GeoRect());


        if (_layer is null) return;

        // 为什么切模式要重新 fit：两种投影下的"数据范围"不一样（墨卡托把高纬拉长了），
        // 沿用旧的 baseScale，切过去形状就是歪的。切投影 = 换了一把尺子，必须重新量。
        _bounds = new GeoBounds(ProjectAllPoints());
        _bounds.Reset(MapCanvas.ActualWidth, MapCanvas.ActualHeight);
        RebuildView();
    }

    /// <summary>把图层里所有点按当前设置投影一遍。fit 和渲染都以它为准。</summary>
    private IEnumerable<(double X, double Y)> ProjectAllPoints()
        => _layer!.AllPoints().Select(p => Projection.Project(p, _settings));

    // ─────────────────────────────── 缩放与平移 ───────────────────────────────

    /// <summary>
    /// 滚轮缩放，以鼠标位置为锚点（鼠标底下的数据点不动）。
    /// 每格滚轮乘 1.15，倍数夹在 GeoBounds.MinZoom ~ MaxZoom 之间；
    /// 夹过的目标值要先算回本步的有效因子，否则到了上限继续滚，画面会"漂"。
    /// </summary>
    private void OnCanvasMouseWheel(object sender, MouseWheelEventArgs e)
    {
        
        if (_bounds is null || !_bounds.HasViewport) return;

        var pos = e.GetPosition(MapCanvas);

        double steps =  e.Delta/ 120.0;
        double factor = Math.Pow(1.15, steps);
        double targrt = Math.Clamp(_bounds.Zoom * factor, GeoBounds.MinZoom, GeoBounds.MaxZoom);
        double effective = targrt / _bounds.Zoom;
        _bounds.ZoomAt(pos.X, pos.Y, effective);
        RebuildView();

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
            RebuildView();
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
    }

    // ─────────────────────────────── 悬停提示 ───────────────────────────────

    /// <summary>
    /// 按当前鼠标位置做一次命中测试：命中就填文字、显示提示浮层，没命中就收起来。
    /// HoverTip 设了 IsHitTestVisible=False，所以它不会反过来抢鼠标事件。
    /// </summary>
    private void UpdateHoverTip(Point pos)
    {
        if (_layer is null || _bounds is null || !_bounds.HasViewport) return;

        var hit = HitTester.HitTest(_layer, _settings, _bounds, pos.X, pos.Y);

        if (hit is null)
        {
            HoverTip.Visibility = Visibility.Collapsed;
            return;
        }

        // KeyValuePair 的 ToString() 长得像 [name, 北京]，直接 Join 出来是给调试看的，不是给人看的。
        // 挑前 3 个属性拼成「name: 北京」这种一行一个的样式。
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
        RebuildView();
    }

    // ─────────────────────────────── 重画 ───────────────────────────────

    /// <summary>唯一的"重画"入口：清空 → 重新投影 → 渲染 → 更新状态栏。</summary>
    private void RebuildView()
    {
        if (_layer is null || _bounds is null || !_bounds.HasViewport) return;

        MapCanvas.Children.Clear();

        var renderer = new Rendering.SimpleRenderer();
        renderer.Render(_layer, _settings, _bounds, MapCanvas);

        if (_layer.Features.Count == 0) ShowMessage("这个文件里没有可显示的对象");
        else HideMessage();

        UpdateStatus();

        if (_dragging) HoverTip.Visibility = Visibility.Collapsed;
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
                // 高斯模式的平面数值单位是米，要反算才能得到经纬度。
                // GaussKruger.Inverse 收的是测绘坐标 (x 北, y 东)，而这里的 x 是「水平」、y 是「垂直」，
                // 也就是这里的 (x, y) 恰好是 (东, 北)，所以两个实参要调过来。
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
