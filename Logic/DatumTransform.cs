namespace GeoJsonViewer.Logic;

using GeoJsonViewer.Models;
using System.ComponentModel.DataAnnotations;
using System.Transactions;

/// <summary>
/// 三维直角坐标（地心坐标系）：原点在地心，X 轴指向赤道与本初子午线的交点，Z 轴指向北极，单位米。
///
/// 为什么突然需要一个"不是经纬度"的坐标：
///   经纬度是挂在**某个椭球**上的（大地坐标 BLH）。同一个经纬度数值，放在北京54 的椭球上和放在
///   CGCS2000 的椭球上，是地面上**两个不同的点**（三维上差约 109 米；而且这 109 米几乎全在径向上 ——
///   同一个点的北京54 大地高 50 米，按 CGCS2000 读出来是 159 米，经纬度只挪 2.7 米）。
///   再加上两套基准的地心与轴向本来也不一样（那部分正是七参数管的），所以两套基准之间没法直接换算经纬度，
///   中间必须换到一个跟椭球无关的坐标系里 —— 就是这里的三维直角坐标。链路是：
///     源椭球上的 (B, L, H) → XYZ →（七参数变换）→ XYZ → 目标椭球上的 (B, L, H)
/// </summary>
public readonly record struct GeoXyz(double X, double Y, double Z);

/// <summary>
/// 七参数（布尔沙-沃尔夫 / Bursa-Wolf，也叫 Helmert 七参数）：描述两个大地基准之间
/// 原点差多少（平移）、轴差多少（旋转）、尺子差多少（尺度）。
///
/// 七个参数 = 3 平移 + 3 旋转 + 1 尺度：
///   · 平移 Dx, Dy, Dz —— 两个基准的地心差，中国境内常见几十到几百米；
///   · 旋转 Rx, Ry, Rz —— 两个基准的轴向差，**成果里通常写成角秒**（1″ ≈ 30.9 米，自检 [12] 会量给你看）；
///   · 尺度 m —— 尺子差，**成果里通常写成 ppm**（百万分之一，1 ppm ≈ 6.4 米）。
///
/// ★ 单位是这一步最大的坑：公式里要的是**弧度**和**无量纲小数**，所以
///   Rx/Ry/Rz 必须 ×(π/180/3600)、m 必须 ×1e-6。忘了换算不是差一点，是差 20 万倍和 100 万倍。
///
/// ★ 第二个坑是**约定方向**：同样叫"七参数"，有"位置矢量（Position Vector，EPSG 9606）"与
///   "坐标框架（Coordinate Frame）"两套约定，两者的旋转矩阵互为转置（三个旋转角差一个负号）。
///   本项目固定用**位置矢量**约定，公式写在 <see cref="DatumTransform.ApplySevenParameters"/> 的注释里。
///   拿到别人的参数却不问约定，结果会差两倍的旋转量 —— 国内不少软件用的是坐标框架约定。
///
/// ★ 第三个坑是"正反方向"：参数总是**有方向的**（"北京54 → CGCS2000"和"CGCS2000 → 北京54"不是同一组数）。
///   反着用可以把七个参数全部取负 —— 在小角度下这个近似误差远小于参数本身的精度，工程上都这么做。
/// </summary>
public sealed record DatumParameters(
    string Name,
    double Dx, double Dy, double Dz,   // 平移：米
    double Rx, double Ry, double Rz,   // 旋转：角秒（★不是弧度）
    double ScalePpm)                   // 尺度：ppm（★不是无量纲小数）
{
    /// <summary>七个参数全 0：不做任何变换。用来验证"整条链本身是透明的"。</summary>
    public static readonly DatumParameters Identity = new("恒等（七个参数全 0）", 0, 0, 0, 0, 0, 0, 0);

    /// <summary>
    /// 教学示例参数：北京54 → CGCS2000。
    ///
    /// ★ 这组数**只是教学示例**（平移取了常见的上百米量级、旋转取角秒量级、尺度取 ppm 量级），
    ///   不是我核过的官方成果，别用在真实生产上。真实七参数是**分区域**的，甚至同一省内不同
    ///   地市用的都不一样；而且它们是测绘成果，不是随便能从网上抄到的一组数 ——
    ///   这也解释了为什么"基准转换"在工程上总是一个需要写明参数来源的步骤。
    /// </summary>
    public static readonly DatumParameters Beijing54ToCgcs2000Example =
        new("北京54 → CGCS2000（教学示例）", -110.0, -41.0, -24.0, 1.5, -1.2, 2.0, 3.5);

    /// <summary>
    /// 教学示例参数：西安80 → CGCS2000。
    ///
    /// ★ 同样是**教学示例**。给第二组参数的目的，是让自检 [13] 能考"两个都不是 CGCS2000"这条
    ///   最难的分支（北京54 → 西安80 必须经 CGCS2000 中转），而不是只考"源 → CGCS2000"。
    /// </summary>
    public static readonly DatumParameters Xian80ToCgcs2000Example =
        new("西安80 → CGCS2000（教学示例）", -58.0, -74.0, -16.0, -1.0, 0.5, 1.2, 2.0);
}

/// <summary>
/// 基准转换（七参数）：把经纬度从源椭球换到目标椭球。
///
/// 为什么它和高斯-克吕格是**两件互不相干**的事：
///   高斯-克吕格解决的是"椭球面 → 平面"（怎么摊平、摊平后怎么分带），源和目标都在同一个椭球上；
///   七参数解决的是"这个椭球 → 那个椭球"（两套基准怎么对起来），全程都在三维空间里、不涉及平面。
///   所以正确顺序永远是：**先基准转换（换椭球），再投影（摊平、分带）**。反过来做没有意义 ——
///   你会拿北京54 的椭球参数去套 CGCS2000 的经纬度。
///
/// 四个方法的分工（也是自检 [12] 分层的顺序）：
///   ① BlhToXyz            ：大地坐标 → 三维直角坐标（纯几何，能用闭式公式对答案）
///   ② XyzToBlh            ：三维直角坐标 → 大地坐标（纬度要迭代，坑在极点和收敛判据）
///   ③ ApplySevenParameters：两个坐标系之间的七参数变换（坑全在单位和约定上）
///   ④ Transform           ：三步串起来，三行胶水
///
/// 为什么把 ①②③ 拆开、不写成一个方法：跟高斯那时同一个理由 —— 每一步都能**单独对答案**。
///   ① 有能用公式直接推出来的锚点（赤道、北极、赤道加高），② 有往返，③ 能把七个参数拆成一个一个用。
///   混在一起，出错的只能是"最后结果不对"，你无法知道是几何错了还是单位错了。
/// </summary>
public static class DatumTransform
{
    /// <summary>
    /// ① 大地坐标 → 空间直角坐标（同一椭球内，纯几何）。
    ///
    /// 公式（B = 纬度、L = 经度、H = 大地高，N = 卯酉圈曲率半径）：
    ///     N = a / √(1 − e²·sin²B)
    ///     X = (N + H)·cosB·cosL
    ///     Y = (N + H)·cosB·sinL
    ///     Z = (N·(1 − e²) + H)·sinB
    ///
    /// ★ 坑：Z 那一项里的 N 必须乘 (1 − e²)，写成 N + H 是错的。
    ///   症状：在纬度 40° 附近 Z 会偏大约 42 公里（N·e² ≈ 6378 km × 0.00669）。
    ///   为什么会有这个 (1 − e²)：赤道上的 N 是赤道半径，越往极点、椭球面离地心越近，
    ///   所以"面上的点"在 Z 方向的半径要按 (1 − e²) 缩一下。
    ///
    /// 为什么参数用 GeoPoint：本项目的经纬度到处是 GeoPoint(经度, 纬度)（经度在前！）。
    ///   这里继续用它，就是为了不引入第二种顺序 —— 顺序写反是那种"不报错、结果全错"的 bug。
    /// </summary>
    public static GeoXyz BlhToXyz(GeoPoint point, double heightMeters, Ellipsoid ellipsoid)
    {
        double latRad = point.Lat * Math.PI / 180;
        double lonRad = point.Lon * Math.PI / 180;
        double N = ellipsoid.A / Math.Sqrt(1 - ellipsoid.E2 * Math.Pow(Math.Sin(latRad), 2));
        double X = (N + heightMeters) * Math.Cos(latRad) * Math.Cos(lonRad);
        double Y = (N + heightMeters) * Math.Cos(latRad) * Math.Sin(lonRad);
        double Z = (N * (1 - ellipsoid.E2) + heightMeters) * Math.Sin(latRad);
        return new GeoXyz(X, Y, Z);
    }

    /// <summary>
    /// ② 空间直角坐标 → 大地坐标（同一椭球内，纯几何）。
    ///
    /// 思路：
    ///     经度是免费的：L = atan2(Y, X)（X、Y 就是投影在赤道面上的两个分量，夹角就是经度）。
    ///     纬度和高要一起解，因为 N 依赖 B、H 又依赖 B，所以**要迭代**：
    ///         p = √(X² + Y²)
    ///         初值 B₀ = atan2(Z, p·(1 − e²))        ← 把椭球的扁率只算一次，是个不错的起点
    ///         重复：N = a/√(1 − e²sin²B)；H = p/cosB − N；B = atan2(Z, p·(1 − e²·N/(N + H)))
    ///         直到 |新 B − 旧 B| < 1e-14 弧度（≈ 6e-8 米，远小于任何实际精度）
    ///     最后再用收敛后的 B 把 H 算一次返回。
    ///
    /// ★ 坑一：别把初值当答案。GPS 那种"一步算出纬度"的近似在高纬能差几十米；
    ///   而 H = p/cosB − N 里的 cosB 在极点附近趋于 0，用没收敛的 B 算 H 会放大误差。
    /// ★ 坑二：极点。X = Y = 0 时 L 由 atan2(0, 0) 给出 0（可以接受，极点本来就没经度），
    ///   但 p = 0 时 cosB → 0，H 的式子会变成 0/0。本项目的办法是：极点附近 lat 由 Z 的符号直接定，
    ///   H 用 |Z| − b（见下），不要硬套 p/cosB。
    /// ★ 坑三：迭代要有上限次数（例如 100 次）再退出，否则一个坏输入会让自检永远转下去。
    /// </summary>
    public static (GeoPoint Point, double HeightMeters) XyzToBlh(GeoXyz p, Ellipsoid ellipsoid)
    {
        double p1 = Math.Sqrt(p.X * p.X + p.Y * p.Y);
        double b0 = Math.Atan2(p.Z, p1 * (1 - ellipsoid.E2));
        if(p1 < 1e-14) // 极点附近
        {
            double lat = Math.Sign(p.Z) * 90.0; // 纬度直接定为 ±90°
            double height = Math.Abs(p.Z) - ellipsoid.B; // 高度用 |Z| − b
            return (new GeoPoint(0.0, lat), height);
        }
        for (int i = 0; i < 100; i++)
        {
            double N = ellipsoid.A / Math.Sqrt(1 - ellipsoid.E2 * Math.Pow(Math.Sin(b0), 2));
            double H = p1 / Math.Cos(b0) - N;
            double b1 = Math.Atan2(p.Z, p1 * (1 - ellipsoid.E2 * N / (N + H)));
            if (Math.Abs(b1 - b0) < 1e-14)
            {

                return (new GeoPoint(Math.Atan2(p.Y, p.X)*180/Math.PI, b1*180/Math.PI), H);
            }
            b0 = b1;
        }
        throw new InvalidOperationException("存在点无法将三维直角坐标转换为大地坐标");
    }

    /// <summary>
    /// ③ 七参数变换：在同一套三维直角坐标里，把源基准的坐标换到目标基准。
    ///
    /// 公式（位置矢量约定 / Position Vector，EPSG 9606；rx、ry、rz 是**弧度**，m 是**无量纲小数**）：
    ///     [Xt]   [Dx]          [  1   −rz   +ry ] [X]
    ///     [Yt] = [Dy] + (1+m)· [ +rz   1    −rx ] [Y]
    ///     [Zt]   [Dz]          [ −ry   +rx   1  ] [Z]
    /// 展开就是：
    ///     Xt = Dx + (1+m)·(X − rz·Y + ry·Z)
    ///     Yt = Dy + (1+m)·(rz·X + Y − rx·Z)
    ///     Zt = Dz + (1+m)·(−ry·X + rx·Y + Z)
    ///
    /// ★ 进公式前必须换算单位：rx、ry、rz 是角度秒 → 弧度要 ×(π/180/3600)；
    ///   m 是 ppm → ×1e-6。这两个换算就是本方法最容易漏的一步（漏了以后 XY 会飞到几万公里外）。
    /// ★ (1+m) 乘的是**整个旋转后的向量**，不是只乘 Z；平移量**不**乘 (1+m)。
    ///   写错这两处的症状都是"结果看着像对的、但差几米到几十米"，只有拿量级去卡才发现。
    /// ★ 约定：本方法用**位置矢量**约定。如果你手上的参数来自坐标框架约定的资料，
    ///   把三个旋转角同时取负再喂进来即可（等价于把矩阵转置）。
    /// </summary>
    public static GeoXyz ApplySevenParameters(GeoXyz source, DatumParameters parameters)
    {
        double rxRad = parameters.Rx * Math.PI / 180 / 3600;
        double ryRad = parameters.Ry * Math.PI / 180 / 3600;
        double rzRad = parameters.Rz * Math.PI / 180 / 3600;
        double mScale = parameters.ScalePpm * 1e-6;

        return new GeoXyz(
            parameters.Dx + (1 + mScale) * (source.X - rzRad * source.Y + ryRad * source.Z),
            parameters.Dy + (1 + mScale) * (rzRad * source.X + source.Y - rxRad * source.Z),
            parameters.Dz + (1 + mScale) * (-ryRad * source.X + rxRad * source.Y + source.Z)
        );
    }

    /// <summary>
    /// ④ 整条链：源椭球上的经纬度 → 目标椭球上的经纬度（附目标椭球上的大地高）。
    ///
    /// 就是三行：BlhToXyz（源椭球）→ ApplySevenParameters → XyzToBlh（目标椭球）。
    /// 每个调用用哪个椭球是这里唯一的看点：进 XYZ 用**源**椭球，出 BLH 用**目标**椭球。
    ///
    /// ★ 转换后大地高通常会突变几十到上百米（例如从 50 米变成 170 多米）——这不是 bug：
    ///   两个基准的地心差有上百米，同一个地面点在两套基准里的"大地高"本来就不一样。
    ///   本项目不显示大地高，但自检会把它打印出来，让你看见这个变化。
    /// </summary>
    public static (GeoPoint Point, double HeightMeters) Transform(
        GeoPoint point, double heightMeters,
        Ellipsoid sourceEllipsoid, Ellipsoid targetEllipsoid, DatumParameters parameters)
    {
        var xyz = BlhToXyz(point, heightMeters, sourceEllipsoid);
        var transformedXyz = ApplySevenParameters(xyz, parameters);
        return XyzToBlh(transformedXyz, targetEllipsoid);
    }

    /// <summary>
    /// ⑤ 一整层对象穿过基准转换：遍历所有几何的所有点，逐点 <see cref="Transform"/>，组装出一个**新的** GeoLayer。
    ///
    /// ★ 为什么返回新图层、不改原来那个：
    ///   "原始数据"是唯一的真相（它记的是文件里写的那个基准下的经纬度）。保留它，界面上换「源基准」
    ///   才能重算；如果就地改，换回去就再也换不回来了（信息已经丢了）。
    ///
    /// ★ 除了坐标，一样都不能少：FileName、SkippedCount、每个 feature 的 Geometry.Kind、
    ///   点的**个数与顺序**（面的首尾闭合点也要跟着走）、Properties。
    ///   DisplayName 是按属性算出来的，属性在就自然对。
    ///
    /// ★ 四个方向都要能用（参数是**有方向**的）：
    ///   · source 与 target 是同一个基准 → 恒等，一点都不能动；
    ///   · target 是 CGCS2000   → 用 source.ToCgcs2000（正向）；
    ///   · source 是 CGCS2000   → 用 target.ToCgcs2000，**七个参数全部取负**（反向；
    ///     小角度下取负是工程上通用的近似，误差远小于参数本身的精度）；
    ///   · 两个都不是 CGCS2000  → 分两步：source → CGCS2000 → target。
    ///
    /// ★ 大地高在 GeoPoint 里没地方存，所以由参数给（默认 0）：这是把"我们假设所有点都贴在地面上"
    ///   这个前提**写在签名上**，而不是藏在代码里。本项目固定传 0。
    ///
    /// ★ 中转（两个都不是 CGCS2000）时，参数只负责"进第一腿"；**第一腿算出来的中间大地高要带进第二腿**。
    ///   把中间高度丢掉（第二腿继续用 0）结果会差约 1.7 毫米 —— 量级很小，但这是个"约定"，
    ///   不写在注释里就只能靠猜，自检 [13] 按"带过去"卡。
    /// </summary>
    public static GeoLayer TransformLayer(GeoLayer layer, Datum source, Datum target, double heightMeters = 0.0)
    {
        if (source.Ellipsoid == target.Ellipsoid)
        {
            // 同一个椭球 → 恒等，直接返回原图层（不改原图层）。
            return layer;
        }
        DatumParameters temporary = new DatumParameters("null",0,0,0,0,0,0,0);
        
        if (source.Ellipsoid == Ellipsoid.CGCS2000 || (source.Ellipsoid != Ellipsoid.CGCS2000 && target.Ellipsoid != Ellipsoid.CGCS2000))
        {
            temporary = new DatumParameters
            (
                "temporary",
                - target.ToCgcs2000.Dx,
                -target.ToCgcs2000.Dy,
                -target.ToCgcs2000.Dz,
                -target.ToCgcs2000.Rx,
                -target.ToCgcs2000.Ry,
                -target.ToCgcs2000.Rz,
                -target.ToCgcs2000.ScalePpm
            );
        }
        var transformedFeatures = new List<GeoFeature>(layer.Features.Count);
        foreach (var feature in layer.Features)
        {
            IEnumerable<(GeoPoint Point, double HeightMeters)> transformedPoints;
            if (source.Ellipsoid == Ellipsoid.CGCS2000)
            {
                transformedPoints = feature.Geometry.Points.Select(point => Transform(point, heightMeters, source.Ellipsoid,target.Ellipsoid,temporary));
            }
            else if (target.Ellipsoid == Ellipsoid.CGCS2000)
            {
                transformedPoints = feature.Geometry.Points.Select(point => Transform(point, heightMeters, source.Ellipsoid, target.Ellipsoid, source.ToCgcs2000));
            }
            else
            {
                transformedPoints = feature.Geometry.Points.Select(point => 
                { var (temp1,temp2) = Transform(point, heightMeters, source.Ellipsoid, DatumCatalog.Cgcs2000.Ellipsoid, source.ToCgcs2000);
                    return Transform(temp1, temp2, DatumCatalog.Cgcs2000.Ellipsoid, target.Ellipsoid, temporary); 
                });
            }
            
            var transformedGeometry = new GeoGeometry { Kind = feature.Geometry.Kind, Points = transformedPoints.Select(p => p.Point).ToList().AsReadOnly() };
            transformedFeatures.Add(new GeoFeature
            {
                Geometry = transformedGeometry,
                Properties = feature.Properties
            });
        }

        return new GeoLayer
        {
            FileName = layer.FileName,
            SkippedCount = layer.SkippedCount,
            Features = transformedFeatures
        };
    }
    
}
