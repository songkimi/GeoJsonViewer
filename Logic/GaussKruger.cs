namespace GeoJsonViewer.Logic;

using GeoJsonViewer.Models;

/// <summary>
/// 高斯-克吕格投影（国际上叫横轴墨卡托 Transverse Mercator；UTM 是它的"米制表亲"，只差一个 0.9996 的比例因子）。
///
/// 和已有的两种投影比，差在哪：
///   · Linear      —— 经纬度直接当直角坐标，什么都不算。
///   · WebMercator —— 圆柱**竖着**套在地球上（轴与地轴重合），整条经线映成一条竖线，
///                    所以高纬度被拉得很开；用球体公式，只求形状像。
///   · 高斯-克吕格 —— 圆柱**横着**套（轴落在赤道平面里），于是变成"中央经线附近几乎不变形"。
///                    代价是离中央经线越远变形越大，所以才要**分带**：
///                    3 度带把中央经线两侧各 1.5 度算作一带，6 度带各 3 度、带宽更宽但边缘变形也更大。
///                    用椭球公式，算出来直接是米。
///
/// 三条要害（写代码时最容易错的地方）：
///   1. 经差 l 必须相对**本带的中央经线**算，不是相对 0 度经线；
///   2. 公式里的纬度 B 与经差 l 都是**弧度**，而输出 X/Y 是**米**；
///   3. y 要加 500 公里东偏（FalseEasting）：中央经线上的点 y 恰好是 500000，
///      这样一带之内 y 都是正数，不会冒出负坐标。
/// </summary>
public static class GaussKruger
{
    /// <summary>东偏：中央经线对应 500 000 米（所以中央经线上的点 y 恒等于它）。</summary>
    public const double FalseEasting = 500000.0;
    public const double eps = 1e-14; // 牛顿迭代的收敛阈值

    /// <summary>
    /// 子午线弧长：从赤道沿经线走到纬度 B 的路程，单位米。
    /// 它就是正算结果里 x 的主项 —— 经差为 0 时 x 恰好等于它。
    ///
    /// 为什么不能简单用 a × φ（弧度）：地球是扁的，越靠近两极、一度纬度对应的弧长越长
    /// （赤道附近约 110.57 km，极地附近约 111.69 km，差 1%）。
    /// 椭球弧长没有初等原函数，只能按 e² 的幂展开成三角级数（经典子午线弧长级数公式）：
    ///
    ///   X = a(1 − e²)·[ A·B − (B/2)·sin2B + (C/4)·sin4B − (D/6)·sin6B + (E/8)·sin8B ]
    ///
    ///   A = 1 + 3/4·e² + 45/64·e⁴ + 175/256·e⁶ + 11025/16384·e⁸
    ///   B = 3/4·e² + 15/16·e⁴ + 525/512·e⁶ + 2205/2048·e⁸
    ///   C = 15/64·e⁴ + 105/256·e⁶ + 2205/4096·e⁸
    ///   D = 35/512·e⁶ + 315/2048·e⁸
    ///   E = 315/16384·e⁸
    ///
    /// 注意：文献里 A…E 用于表示椭球系数，而 B 又用于表示纬度 ——
    /// 代码里把系数命名为 coefA…coefE、把纬度命名为 latRad，以示区分。
    /// 截到 8 次项（e⁸）在 CGCS2000 上已经优于 0.01 毫米，够用了。
    /// </summary>
    public static double MeridianArcLength(double latitudeDegrees, Ellipsoid ellipsoid)
    {
        double a = ellipsoid.A;
        double e2 = ellipsoid.E2;
        //计算级数系数
        double e4 = e2 * e2;
        double e6 = e4 * e2;
        double e8 = e6 * e2;
        double coefA = 1 + 3.0 / 4 * e2 + 45.0 / 64 * e4 + 175.0 / 256 * e6 + 11025.0 / 16384 * e8;
        double coefB = 3.0 / 4 * e2 + 15.0 / 16 * e4 + 525.0 / 512 * e6 + 2205.0 / 2048 * e8;
        double coefC = 15.0 / 64 * e4 + 105.0 / 256 * e6 + 2205.0 / 4096 * e8;
        double coefD = 35.0 / 512 * e6 + 315.0 / 2048 * e8;
        double coefE = 315.0 / 16384 * e8;

        double latRad = latitudeDegrees * Math.PI / 180;
        double X = a * (1 - e2) * 
            (coefA * latRad 
            - (coefB / 2) * Math.Sin(2 * latRad) 
            + (coefC / 4) * Math.Sin(4 * latRad) 
            - (coefD / 6) * Math.Sin(6 * latRad) 
            + (coefE / 8) * Math.Sin(8 * latRad));
        return X;
    }

    /// <summary>
    /// 分带并返回中央经线经度（度）。zoneWidthDegrees 只认 3 和 6，别的值抛 ArgumentOutOfRangeException。
    ///
    ///   · 6 度带：带号 n = ⌊经度/6⌋ + 1，中央经线 = 6n − 3。
    ///     6 度带从本初子午线开始切，所以 105 = 6×18 − 3（东经 105 那一带是第 18 带）。
    ///   · 3 度带：带号 n = 经度/3 四舍五入（Math.Round），中央经线 = 3n。
    ///     3 度带从 1.5 度开始切，所以 106.6302 落在 (106.5, 109.5) 里，n = 36，中央经线 108。
    ///
    /// 两个示例：上海 121.4737 —— 6 度带落在第 21 带、中央经线 123；
    /// 3 度带落在第 40 带、中央经线 120。同一个点、不同带宽，中央经线能差 3 度。
    /// </summary>
    public static double CentralMeridian(double longitudeDegrees, int zoneWidthDegrees)
    {
        if(zoneWidthDegrees == 6)
        {
            int zoneNumber = (int)Math.Floor(longitudeDegrees / 6.0) + 1;
            return 6 * zoneNumber - 3;
        }
        else if (zoneWidthDegrees == 3)
        {
            int zoneNumber = (int)Math.Round(longitudeDegrees / 3.0);
            return 3 * zoneNumber;
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(zoneWidthDegrees), "不合理的分带宽度");
        }
    }

    /// <summary>
    /// 这份经度范围配这条中央经线，还落在带内吗？（判据：两端的经差绝对值都不超过 带宽/2）
    ///
    /// 为什么要单独写成方法，而不是在界面里随手比一下：
    ///   ① 它是**投影的适用条件**，不是界面的装饰 —— 越出带外，级数的截断误差会从毫米级涨到米级（[9] 段量过）；
    ///   ② 放在 Logic 里，自检就能不问窗口地验它；写在 MainWindow 里就只能靠眼睛看，
    ///      而"数据到底有没有出带"这种事眼睛是看不出来的（图上只是边缘稍微变形）。
    /// </summary>
    public static bool FitsInZone(double minLon, double maxLon, double centralMeridianDegrees, int zoneWidthDegrees)
    {
        if(zoneWidthDegrees != 3 && zoneWidthDegrees != 6)
        {
            throw new ArgumentOutOfRangeException(nameof(zoneWidthDegrees), "不合理的分带宽度");
        }
        var zoneMax = zoneWidthDegrees / 2.0 + centralMeridianDegrees;
        var zoneMin = centralMeridianDegrees - zoneWidthDegrees / 2.0;
        return minLon >= zoneMin && maxLon <= zoneMax;
    }

    /// <summary>
    /// 正算：经纬度 → 平面坐标，单位米。x 向北为正，y 向东为正（测绘习惯；注意与屏幕坐标 x 横 y 竖正好相反）。
    ///
    ///   B  = 纬度（弧度），l = 经度 − 中央经线（弧度）
    ///   t  = tanB，η² = e'²·cos²B，N = a/√(1 − e²·sin²B)（卯酉圈曲率半径）
    ///   X₀ = 子午线弧长（就是上面那个方法算出来的数）
    ///
    ///   x = X₀ + (N/2)·sinB·cosB·l²
    ///          + (N/24)·sinB·cos³B·(5 − t² + 9η² + 4η⁴)·l⁴
    ///          + (N/720)·sinB·cos⁵B·(61 − 58t² + t⁴)·l⁶
    ///
    ///   y = N·cosB·l
    ///          + (N/6)·cos³B·(1 − t² + η²)·l³
    ///          + (N/120)·cos⁵B·(5 − 18t² + t⁴ + 14η² − 58η²t²)·l⁵
    ///          + FalseEasting
    ///
    /// 三个容易翻车的细节：
    ///   · x 的主项是子午线弧长，后面全是"小量修正"：3 度带里 l ≤ 0.026 弧度，l⁶ 已经小到纳米级；
    ///   · 每一项都是 cosB 的**幂**（cos³B 写成 cos3B 就全错了），sinB 只在 x 的修正项里出现；
    ///   · 先把 B、l 换成弧度、再算幂和三角函数，最后才乘 N。
    /// </summary>
    public static (double X, double Y) Forward(GeoPoint point, Ellipsoid ellipsoid, double centralMeridianDegrees)
    {
        double latRad = point.Lat * Math.PI / 180;
        double lRad = (point.Lon - centralMeridianDegrees) * Math.PI / 180;
        double t = Math.Tan(latRad);
        double cosB = Math.Cos(latRad);
        double sinB = Math.Sin(latRad);
        double a = ellipsoid.A;
        double e2 = ellipsoid.E2;
        double n2 = ellipsoid.Ep2 * cosB * cosB;
        double N = a / Math.Sqrt(1.0 - e2 * sinB * sinB);
        double X0 = MeridianArcLength(point.Lat, ellipsoid);

        double X = X0
            + (N / 2) * sinB * cosB * lRad * lRad
            + (N / 24) * sinB * Math.Pow(cosB, 3) * (5 - t * t + 9 * n2 + 4 * n2 * n2) * Math.Pow(lRad, 4)
            + (N / 720) * sinB * Math.Pow(cosB, 5) * (61 - 58 * t * t + t * t * t * t) * Math.Pow(lRad, 6);

        double Y = N * cosB * lRad
            + (N / 6) * Math.Pow(cosB, 3) * (1 - t * t + n2) * Math.Pow(lRad, 3)
            + (N / 120) * Math.Pow(cosB, 5) * (5 - 18 * t * t + t * t * t * t + 14 * n2 - 58 * n2 * t * t) * Math.Pow(lRad, 5) + FalseEasting;
        return (X, Y);
    }

    /// <summary>
    /// 反算：平面坐标（米）→ 经纬度（度）。x 向北、y 向东，是 Forward 的逆运算。
    ///
    /// 反算不能"照抄正算"：正算是把 (B, l) 代进级数，反算要先把平面坐标"卸"回接近中央经线，
    /// 也就是多一个**底点纬度**（垂足纬度）的概念。两步走：
    ///
    ///   第一步：求底点纬度 Bf —— 解方程 M(Bf) = x，用牛顿迭代：
    ///             f(B) = MeridianArcLength(B) − x
    ///             f′(B) = R_M(B) = a(1 − e²) / (1 − e²·sin²B)^1.5   ← 子午圈曲率半径
    ///             （不是巧合：M(B) 是 R_M 从赤道到 B 的积分，所以 dM/dB 就等于 R_M(B)。）
    ///             初值 Bf₀ = x / (a(1 − e²))，即"先把椭球当成半径 a(1−e²) 的圆"；
    ///             每步修正量小于 1e-14 弧度就停，写个 30 次上限防御性兜底。
    ///
    ///   第二步：在 Bf 处把平面坐标映回经纬度（反算级数公式，与正算级数对称）：
    ///             tf = tanBf，ηf² = e'²·cos²Bf，Nf = a/√(1 − e²·sin²Bf)，Rf = a(1−e²)/(1−e²·sin²Bf)^1.5
    ///             D  = (y − FalseEasting) / Nf
    ///             B  = Bf − (Nf·tf/Rf)·[ D²/2 − (5 + 3tf² + ηf² − 9ηf²tf²)·D⁴/24 + (61 + 90tf² + 45tf⁴)·D⁶/720 ]
    ///             l  = [ D − (1 + 2tf² + ηf²)·D³/6 + (5 + 28tf² + 24tf⁴ + 6ηf² + 8ηf²tf²)·D⁵/120 ] / cosBf
    ///
    /// 一处常见错误：x 里除了子午线弧长还含着
    ///   (N/2)·sinB·cosB·l² 这个小量，所以 M⁻¹(x) 只是**底点纬度**、不是真实纬度，真实纬度比它小一点。
    ///   差值量级：l = 1.6° 时约 1032 米 ≈ 0.0093 度；l = 3.5° 时约 0.04 度。
    ///   表现为**纬度整体偏大、且越远离中央经线偏得越多**（中央经线上恰好为 0）。
    ///
    /// 返回 GeoPoint(经度, 纬度) —— **第一个参数是经度**，别写成 (纬度, 经度)：
    ///   经度 = 中央经线 + l（换回度），纬度 = B（换回度）。
    /// </summary>
    public static GeoPoint Inverse(double x, double y, Ellipsoid ellipsoid, double centralMeridianDegrees)
    {
        y = y - FalseEasting;
        double Bf = CalFootlatitude(x, ellipsoid);

        double tf = Math.Tan(Bf);
        double cosBf = Math.Cos(Bf);
        double sinBf = Math.Sin(Bf);
        double n2f = ellipsoid.Ep2 * cosBf * cosBf;
        double Nf = ellipsoid.A / Math.Sqrt(1.0 - ellipsoid.E2 * sinBf * sinBf);
        double Rf = ellipsoid.A * (1 - ellipsoid.E2) / Math.Pow(1 - ellipsoid.E2 * sinBf * sinBf, 1.5);
        
        double D = y / Nf;
        double B = Bf - (Nf * tf / Rf) * (D * D / 2 - (5 + 3 * tf * tf + n2f - 9 * n2f * tf * tf) * Math.Pow(D, 4) / 24 + (61 + 90 * tf * tf + 45 * Math.Pow(tf, 4)) * Math.Pow(D, 6) / 720);
        double l = (D - (1 + 2 * tf * tf + n2f) * Math.Pow(D, 3) / 6 + (5 + 28 * tf * tf + 24 * Math.Pow(tf, 4) + 6 * n2f + 8 * n2f * tf * tf) * Math.Pow(D, 5) / 120) / cosBf;

        double L0Rad = centralMeridianDegrees * Math.PI / 180;
        double lonRad = L0Rad + l;
        double latRad = B;
        double lonDeg = lonRad * 180 / Math.PI;
        double latDeg = latRad * 180 / Math.PI;
        return new GeoPoint { Lon = lonDeg, Lat = latDeg };
    }
    public static double CalFootlatitude(double x, Ellipsoid ellipsoid)
    {
        // 牛顿迭代求底点纬度 Bf
        double a = ellipsoid.A;
        double e2 = ellipsoid.E2;
        double Bf0 = x / (a * (1 - e2)); // 初值，假设椭球是半径 a(1-e²) 的圆
        for (int i = 0; i < 30; i++)
        {
            double M = MeridianArcLength(Bf0 * 180 / Math.PI, ellipsoid); // 子午线弧长
            double R_M = a * (1 - e2) / Math.Pow(1 - e2 * Math.Sin(Bf0) * Math.Sin(Bf0), 1.5); // 子午圈曲率半径
            double deltaBf = (M - x) / R_M; // 修正量
            Bf0 -= deltaBf;
            if (Math.Abs(deltaBf) < eps) break; // 收敛阈值
        }
        return Bf0;
    }

    /// <summary>
    /// 换带：把同一个点从「源带」的高斯平面坐标搬到「目标带」。
    /// 数学上只有两步：先用源带反算回经纬度（<see cref="Inverse"/>），再用目标带正算回去（<see cref="Forward"/>）。
    /// 经纬度是这两套平面坐标之间唯一的「共同语言」：两个带里的 (x, y) 不能直接相加相减，
    /// 因为它们的原点、中央经线、尺度都不是同一把尺子。
    ///
    /// 两个必须留意的点：
    ///  · 换带**不**解决「这个点离目标带中央经线 5°」的问题 —— 它只是换了一把尺子量同一个点，
    ///    出带后的变形要另外判断（见 <see cref="FitsInZone"/>）。
    ///  · 「带号前缀」不应混进来：真实成果里的 y 常写成 带号×1e6 + 500 km 偏移（如 20500000），
    ///    直接传入会多出 2000 万米 —— 见 <see cref="StripZonePrefix"/>。
    ///
    /// 一处**约定**（实现里已经这么做）：y ≥ 1e6 时会被当成「带了带号前缀」自动剥掉。
    /// 这是个启发式而不是判据 —— 合法的无前缀东偏永远落在 16.6 万~83.4 万米之间（500 km ± 半带宽），
    /// 所以这个界在本项目的取值范围内够用；但「手里这份数据到底带不带前缀」最终应由数据来源说明，
    /// 而不是靠数值推断（接界面时会做成一个显式选择）。自检 [14] 的第 ⑥ 条专门喂一个带前缀的 y 来覆盖这一行。
    /// </summary>
    public static (double X, double Y) ChangeZone(
        double x, double y, Ellipsoid ellipsoid,
        double sourceCentralMeridianDegrees, double targetCentralMeridianDegrees)
    {
        if (y >= 1_000_000)
        {
            int zoneNumber = (int)(y / 1_000_000);
            y = StripZonePrefix(y, zoneNumber);
        }
        var transPoint = Inverse(x, y, ellipsoid, sourceCentralMeridianDegrees);
        return Forward(transPoint, ellipsoid, targetCentralMeridianDegrees);
    }

    /// <summary>
    /// 剥掉「带号前缀」。
    /// 6° 带的国家标准成果里，y 常写成「带号 + 500 km 偏移」的整数形式：
    /// 20 带（中央经线 117°）里 y = 20500000 表示「本带中央经线以东 0 米」（即东偏 500 km）。
    /// 所以真正的东偏 = y − 带号 × 1 000 000。
    ///
    /// 为什么值得单独一个方法：减不减这一千万，差的是 2000 万米（约 2000 公里）——
    /// 这种量级的错误在图上表现为「什么都不见了」，肉眼最容易归咎于别的地方。
    /// （3° 带的成果一般不加带号前缀，所以调用前要先确认手里这份是不是这种写法。）
    /// </summary>
    public static double StripZonePrefix(double y, int zoneNumber)
    {
        return y - zoneNumber * 1_000_000;
    }

    
}
