namespace GeoJsonViewer.Logic;

/// <summary>
/// 把平面数值映射到屏幕像素。含 fit、缩放、平移。
///
/// 它在整条链路里的位置（这步顺序错了就永远对不上）：
///
///   经纬度 ──Projection──▶ 平面数值 ──GeoBounds(fit+zoom+pan)──▶ 屏幕像素
///          （非线性，只跟点有关）      （线性，只跟视图有关）
///
/// 为什么这一层要单独成一个类、而不是塞进 Projection：
///   因为它「容易错但容易看出错」。fit 该拿哪套范围算、y 为什么反号、缩放锚点怎么补回来，
///   坑全在这里。独立成类之后，位置不对时该查哪边一目了然：形状歪 → 查 Projection；
///   位置偏/缩放跑 → 查这里。
///
/// 投影公式（Reset / SetDataBounds 时按数据跨度算一次 baseScale，之后屏幕位置只由 zoom / pan 改）：
///
///   让「数据点 p = center」映射到「视图中心」，
///   且「数据跨度 span」刚好铺满「可用像素 viewport - 2*padding」：
///
///       baseScaleX = (viewportW - 2*padding) / spanX
///       baseScaleY = (viewportH - 2*padding) / spanY
///       baseScale  = min(baseScaleX, baseScaleY)      ← 取小值，否则图会被拉变形
///
///   于是「相对中心的偏移」乘以 baseScale 就是像素偏移：
///
///       screenX = viewportW/2 + panX + zoom * ( x - centerX) * baseScale
///       screenY = viewportH/2 + panY + zoom * (-(y - centerY)) * baseScale
///                                                        ↑ 负号：纬度越大越靠北，
///                                                          而屏幕 y 越大越靠下
/// </summary>
public sealed class GeoBounds
{
    // ── 由数据决定（Reset / SetDataBounds 时算出）────────────────────────────
    private double _centerX;
    private double _centerY;
    private double _spanX = 1.0;
    private double _spanY = 1.0;

    /// <summary>
    /// fit 出来的「像素 / 平面单位」比例。zoom = 1 时，数据跨度刚好铺满可用像素。
    /// 对外暴露成只读，是为了让渲染器知道「现在 1 度等于多少像素」，
    /// 从而把点半径、线宽、命中容差换算成不随缩放乱变的尺寸。
    /// </summary>
    public double BaseScale { get; private set; } = 1.0;

    // ── 由视图决定（滚轮、拖拽、拉窗口只改这三个）──────────────────────────
    private double _zoom = 1.0;
    private double _panX;
    private double _panY;
    private double _viewportWidth;
    private double _viewportHeight;

    /// <summary>点的外接矩形最小跨度，防止「只有一个点」时 span 为 0 导致除零/无限放大。</summary>
    private const double SpanFloor = 1e-6;

    /// <summary>
    /// fit 时四周留白的像素数。存成字段是因为 <see cref="RecomputeBaseScale"/> 要用到它，
    /// 而窗口尺寸变化（SetViewport）时也要按同一个留白重算——留白跟着 Reset 的入参走。
    /// </summary>
    private double _padding = 20.0;

    /// <summary>用一批"已经投影过"的点定出数据范围。</summary>
    public GeoBounds(IEnumerable<(double X, double Y)> projectedPoints)
    {
        SetDataBounds(projectedPoints);
    }

    /// <summary>缩放倍数（1 = 适配窗口）。滚轮改的就是它。</summary>
    public double Zoom
    {
        get => _zoom;
        set => _zoom = Math.Clamp(value, MinZoom, MaxZoom);
    }

    /// <summary>缩放下限。需求写的是 0.2× ~ 20×，夹在这里就能保证任何路径都越不过去。</summary>
    public const double MinZoom = 0.2;
    public const double MaxZoom = 20.0;

    /// <summary>平移量（屏幕像素）。</summary>
    public double PanX { get => _panX; set => _panX = value; }
    public double PanY { get => _panY; set => _panY = value; }

    /// <summary>当前视图下「1 个平面单位（≈1 度）等于多少像素」。渲染器算点半径/线宽用它。</summary>
    public double PixelsPerDataUnit => BaseScale * _zoom;

    public double ViewportWidth => _viewportWidth;
    public double ViewportHeight => _viewportHeight;

    /// <summary>
    /// 用一批"已经投影过"的点定出数据范围。
    /// 注意：传进来的必须是**投影后**的点，不是经纬度。
    /// </summary>
    public void SetDataBounds(IEnumerable<(double X, double Y)> projectedPoints)
    {
        // 空输入的守住点：一个可用的点都没有时，Min/Max 会直接抛
        //   InvalidOperationException: 序列不包含任何元素 —— 这个异常一路冒到
        //   打开文件那里，就是"程序崩了"。
        //
        //   为什么把守卫修在这里、而不是"不许把空数据传进来"：
        //   本方法收到空集合是**完全合法**的输入。空 FeatureCollection、
        //   整个文件都是不支持的几何类型，都会走到这儿。让这里自己兜住，
        //   任何调用方（现在两个、以后可能更多）都不可能再踩到那个异常。
        //   在**调用方**加判断就是"同一件事写两遍"，漏一处就崩一次。
        //
        //   兜住的值选 (0, 0)：中心放在原点、跨度为 0，配合 RecomputeBaseScale 里的
        //   SpanFloor 得到有限的比例（不会除零、不会 Infinity）。
        //   反正一个点都没有，画面上什么都不画，视图中心在哪其实无人看见——
        //   但**它必须是有限数**，不能是 NaN/∞。
        var valid = projectedPoints.Where(p => !double.IsNaN(p.X) && !double.IsNaN(p.Y)).ToList();
        if (valid.Count == 0)
        {
            _centerX = 0.0;
            _centerY = 0.0;
            _spanX = 0.0;
            _spanY = 0.0;
            RecomputeBaseScale();
            return;
        }

        _centerX = (valid.Min(p => p.X) + valid.Max(p => p.X)) / 2.0;
        _centerY = (valid.Min(p => p.Y) + valid.Max(p => p.Y)) / 2.0;
        _spanX = valid.Max(p => p.X) - valid.Min(p => p.X);
        _spanY = valid.Max(p => p.Y) - valid.Min(p => p.Y);
        RecomputeBaseScale();
    }

    /// <summary>
    /// 以某个屏幕点为锚点缩放（滚轮缩放要用它）。
    ///
    /// 为什么不能只改 Zoom：Zoom 是绕「视图中心」缩放的，而鼠标通常不在中心，
    /// 于是鼠标底下的那个点会顺着缩放方向跑掉。
    /// 正确做法两步：
    ///   1. 缩放前记下鼠标位置对应的数据坐标：(dx,dy) = ToData(screenX, screenY)；
    ///   2. 改完 Zoom 后，反算 pan，让 (dx,dy) 重新落回 (screenX,screenY)。
    /// 把 ToScreen 的公式反着解一遍，就得到下面 pan 的表达式。
    /// </summary>
    public void ZoomAt(double screenX, double screenY, double factor)
    {
        var (dx, dy) = ToData(screenX, screenY);
        // 走带 Clamp 的属性，不要直接写 _zoom：直接写字段会绕过 0.2×~20× 的夹，
        // 顶到上限后继续滚，_zoom 会在内部越涨越大（实测能到 66×），
        // 表现为"往回滚了一大截才动"，也就是手感上的"漂"。
        Zoom = _zoom * factor;
        _panX = screenX - _viewportWidth / 2 - _zoom * (dx - _centerX) * BaseScale;
        _panY = screenY - _viewportHeight / 2 - _zoom * (-(dy - _centerY)) * BaseScale;
    }

    /// <summary>平面数值 → 屏幕像素。公式见类注释里的推导。</summary>
    public (double ScreenX, double ScreenY) ToScreen(double x, double y)
    {
        var ScreenX = _viewportWidth / 2 + _panX + _zoom * (x - _centerX) * BaseScale;
        var ScreenY = _viewportHeight / 2 + _panY + _zoom * (-(y - _centerY)) * BaseScale;
        return (ScreenX, ScreenY);
    }

    /// <summary>屏幕像素 → 平面数值（命中测试、缩放锚点都要用）。它是 ToScreen 的反函数。</summary>
    public (double X, double Y) ToData(double screenX, double screenY)
    {
        var x = (screenX - _viewportWidth / 2 - _panX) / (_zoom * BaseScale) + _centerX;
        var y = (screenY - _viewportHeight / 2 - _panY) / (-_zoom * BaseScale) + _centerY;
        return (x, y);
    }

    /// <summary>把视图重置成"全部适配"：缩放回到 1、平移归零、按当前窗口尺寸重算比例。</summary>
    public void Reset(double viewportWidth, double viewportHeight, double padding = 20)
    {
        _padding = padding;
        _viewportWidth = viewportWidth;
        _viewportHeight = viewportHeight;
        // fit 只发生在这里（和 SetDataBounds 里那一次）。
        //   BaseScale 是"1 度等于多少像素"的基准，一旦跟着窗口尺寸重算，
        //   用户辛苦滚到的位置就会在拉窗口时被悄悄改掉（实测：窗口缩 40%，内容跟着缩 40%）。
        RecomputeBaseScale();
        _zoom = 1.0;                                  // 直接写字段，避开属性上的 Clamp（值本来就合法）
        _panX = 0.0;
        _panY = 0.0;
    }

    /// <summary>
    /// 只更新"画布现在多大"，**不碰 BaseScale**（所以拉窗口时内容不会跟着缩放），
    /// 并补偿 pan，让用户正在看的那块地方不会在拉窗口时跑掉。
    ///
    /// 它和 <see cref="Reset"/> 的分工：
    ///   · SetViewport —— 帧尺寸变了，用户看的还是同一块地方（窗口缩放）；
    ///   · Reset       —— 用户要求重新 fit（打开文件 / 切投影），这时候才该重算 BaseScale。
    /// 两件事分开之后，BaseScale 就不再依赖"窗口当前多大"，依赖它的东西自然不会被改。
    /// </summary>
    public void SetViewport(double width, double height)
    {
        // 结论：panDelta = (旧宽 - 新宽) / 2，横向纵向各一份。
        //   「任意像素底下压着的地理坐标不变」和「钉住某个锚点像素」化简出来是同一个式子；
        //   写成这个形式是为了不在符号上翻车。
        //
        // 为什么非要用 pan 来补、不能"把算出来的坐标直接减掉一点"：
        //   因为屏幕上看到的每个点 = ToScreen(数据)，而 ToScreen 里带 pan。
        //   pan 是**唯一的那个"内容整体挪了多少"**的存储位置；直接去改屏幕坐标的话，
        //   要么改不到（它每帧由 ToScreen 现算，是个结果不是状态），要么就得在每个
        //   用到 ToScreen 的地方各减一次——那正是"同一件事写在两个地方"，迟早不一致
        //   （悬停、缩放锚点都会用 ToScreen，漏一处就是那个地方的鼠标准星偏掉）。
        //
        // 判定标准：
        //   拉窗口前后，**同一个屏幕位置上压着的数据（ToData）不能变**，两点间的像素距离也不能变。
        //   拿"某个屏幕坐标本身前后相等"去判会误判——画布变小，屏幕坐标系的原点跟着换了
        //   （旧正中 560 → 新正中 460），那个坐标本来就该变。
        //
        // 推导（横向；纵向同理，W→H、x→y。k = Zoom * BaseScale，拉窗口时不变）：
        //   设 a 是旧画布上的某个屏幕位置，它底下压着 d = ToData(a)，反解得
        //       k(d - cx) = a - W/2 - panX_旧
        //   要让这块 d 在新画布上仍落在 a（即 d "没跑"）：
        //       a = W'/2 + panX_新 + k(d - cx)
        //   代入上式：a = W'/2 + panX_新 + a - W/2 - panX_旧
        //   两边的 a 消掉 ⇒ panX_新 - panX_旧 = (W - W')/2  ⇒  panDelta = (旧宽 - 新宽) / 2 ✓
        //
        //   一行数值例子（W=1120 → W'=920，缩小 200，故 panDelta = +100）：
        //     旧帧 a=(0,0) 底下压着 d=(103.3107, 40.7021)（由 ToData 反解得到）
        //     新帧：k 不变 ⇒ a' = 920/2 + 100 + (103.3107 - 117.3691) * 39.8339 = 0 ✓ 原地不动
        var panDeltaX = (_viewportWidth - width) / 2.0;
        var panDeltaY = (_viewportHeight - height) / 2.0;

        _panX += panDeltaX;
        _panY += panDeltaY;
        _viewportWidth = width;
        _viewportHeight = height;
    }

    /// <summary>fit 的核心：由可用像素和数据跨度算出 BaseScale。两轴取较小值，保证长宽比不变。</summary>
    private void RecomputeBaseScale()
    {
        BaseScale = Math.Min(
            (_viewportWidth - 2 * _padding) / Math.Max(_spanX, SpanFloor),
            (_viewportHeight - 2 * _padding) / Math.Max(_spanY, SpanFloor)); 
    }

    /// <summary>当前是否已经拿到窗口尺寸（拿不到就算不出比例，渲染要跳过）。</summary>
    public bool HasViewport => _viewportWidth > 0 && _viewportHeight > 0;
}
