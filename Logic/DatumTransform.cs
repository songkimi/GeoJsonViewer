namespace GeoJsonViewer.Logic;

using GeoJsonViewer.Models;

/// <summary>
/// 三维直角坐标（地心坐标系）：原点在地心，X 轴指向赤道与本初子午线的交点，Z 轴指向北极，单位米。
///
/// 需要这样一个"不是经纬度"的坐标的原因：
///   经纬度依附于**某个椭球**（大地坐标 BLH）。同一组经纬度数值，定义在北京54 椭球上和 CGCS2000
///   椭球上，对应地面上**两个不同的点**（三维上差约 109 米；且这 109 米几乎全在径向上 ——
///   同一个点的北京54 大地高 50 米，按 CGCS2000 读出来是 159 米，经纬度只挪 2.7 米）。
///   两套基准的地心与轴向也不一致（这部分由七参数描述），因此两套基准之间不能直接换算经纬度，
///   中间必须经过一个与椭球无关的坐标系，即三维直角坐标。链路为：
///     源椭球上的 (B, L, H) → XYZ →（七参数变换）→ XYZ → 目标椭球上的 (B, L, H)
/// </summary>
public readonly record struct GeoXyz(double X, double Y, double Z);

/// <summary>
/// 七参数（布尔沙-沃尔夫 / Bursa-Wolf，也叫 Helmert 七参数）：描述两个大地基准之间
/// 原点差多少（平移）、轴差多少（旋转）、尺子差多少（尺度）。
///
/// 七个参数 = 3 平移 + 3 旋转 + 1 尺度：
///   · 平移 Dx, Dy, Dz —— 两个基准的地心差，中国境内常见几十到几百米；
///   · 旋转 Rx, Ry, Rz —— 两个基准的轴向差，**成果中通常以角秒为单位**（1″ ≈ 30.9 米，自检 [12] 会给出该量测）；
///   · 尺度 m —— 尺度差，**成果中通常以 ppm 为单位**（百万分之一，1 ppm ≈ 6.4 米）。
///
/// 单位是这一步最常见的错误来源：公式要求**弧度**与**无量纲小数**，因此
///   Rx/Ry/Rz 必须 ×(π/180/3600)、m 必须 ×1e-6。漏掉换算会使结果偏差 20 万倍和 100 万倍。
///
/// 第二点是**约定方向**：同样称为"七参数"，存在"位置矢量（Position Vector，EPSG 9606）"与
///   "坐标框架（Coordinate Frame）"两套约定，两者的旋转矩阵互为转置（三个旋转角差一个负号）。
///   本项目固定采用**位置矢量**约定，公式见 <see cref="DatumTransform.ApplySevenParameters"/> 的注释。
///   使用来源不明的参数而不确认约定，会得到两倍的旋转量 —— 国内不少软件采用坐标框架约定。
///
/// 第三点是"正反方向"：参数总是**有方向的**（"北京54 → CGCS2000"与"CGCS2000 → 北京54"不是同一组数）。
///   反向使用可将七个参数全部取负 —— 在小角度下该近似误差远小于参数本身的精度，工程上普遍如此处理。
/// </summary>
public sealed record DatumParameters(
    string Name,
    double Dx, double Dy, double Dz,   // 平移：米
    double Rx, double Ry, double Rz,   // 旋转：角秒（非弧度）
    double ScalePpm)                   // 尺度：ppm（非无量纲小数）
{
    /// <summary>七个参数全 0：不做任何变换。用来验证"整条链本身是透明的"。</summary>
    public static readonly DatumParameters Identity = new("恒等（七个参数全 0）", 0, 0, 0, 0, 0, 0, 0);

    /// <summary>
    /// 示例参数：北京54 → CGCS2000。
    ///
    /// 这组数**仅为示例**（平移取常见的上百米量级、旋转取角秒量级、尺度取 ppm 量级），
    ///   不是经核实的官方成果，不可用于实际生产。真实七参数是**分区域**的，甚至同一省内不同
    ///   地市也不相同；且属于测绘成果，并非可随意从公开渠道获得的一组数 ——
    ///   这也是"基准转换"在工程上始终是一个需要注明参数来源的步骤的原因。
    /// </summary>
    public static readonly DatumParameters Beijing54ToCgcs2000Example =
        new("北京54 → CGCS2000（示例参数）", -110.0, -41.0, -24.0, 1.5, -1.2, 2.0, 3.5);

    /// <summary>
    /// 示例参数：西安80 → CGCS2000。
    ///
    /// 同样**仅为示例**。提供第二组参数的目的，是让自检 [13] 能覆盖"两个都不是 CGCS2000"这条
    ///   最难的分支（北京54 → 西安80 必须经 CGCS2000 中转），而不只是"源 → CGCS2000"。
    /// </summary>
    public static readonly DatumParameters Xian80ToCgcs2000Example =
        new("西安80 → CGCS2000（示例参数）", -58.0, -74.0, -16.0, -1.0, 0.5, 1.2, 2.0);
}

/// <summary>
/// 基准转换（七参数）：把经纬度从源椭球换到目标椭球。
///
/// 它与高斯-克吕格是**两件互不相干**的事：
///   高斯-克吕格解决的是"椭球面 → 平面"（如何摊平、摊平后如何分带），源与目标在同一个椭球上；
///   七参数解决的是"这个椭球 → 那个椭球"（两套基准如何对应），全程在三维空间中进行、不涉及平面。
///   因此正确顺序始终是：**先基准转换（换椭球），再投影（摊平、分带）**。反向操作没有意义 ——
///   那等于拿北京54 的椭球参数去套 CGCS2000 的经纬度。
///
/// 四个方法的分工（也是自检 [12] 的分层顺序）：
///   ① BlhToXyz            ：大地坐标 → 三维直角坐标（纯几何，可用闭式公式验证）
///   ② XyzToBlh            ：三维直角坐标 → 大地坐标（纬度需要迭代，难点在极点与收敛判据）
///   ③ ApplySevenParameters：两个坐标系之间的七参数变换（难点集中在单位与约定）
///   ④ Transform           ：三步串起来，三行胶水代码
///
/// 之所以把 ①②③ 拆开、不写成一个方法：与高斯-克吕格同理 —— 每一步都能**单独验证**。
///   ① 有可直接由公式推出的锚点（赤道、北极、赤道加高），② 有往返一致性，③ 可把七个参数逐项单独使用。
///   混在一起时，错误只能表现为"最后结果不对"，无法判断是几何错误还是单位错误。
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
    /// 注意：Z 那一项里的 N 必须乘 (1 − e²)，写成 N + H 是错的。
    ///   症状：在纬度 40° 附近 Z 会偏大约 42 公里（N·e² ≈ 6378 km × 0.00669）。
    ///   (1 − e²) 的来源：赤道上的 N 是赤道半径，越往极点、椭球面离地心越近，
    ///   所以"面上的点"在 Z 方向的半径要按 (1 − e²) 缩一下。
    ///
    /// 参数使用 GeoPoint 的原因：本项目的经纬度统一为 GeoPoint(经度, 纬度)（经度在前）。
    ///   这里继续沿用，以避免引入第二种顺序 —— 顺序写反属于"不报错、结果全错"的缺陷。
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
    /// 注意一：不能把初值当答案。GPS 常用的一步近似在高纬可差几十米；
    ///   而 H = p/cosB − N 中的 cosB 在极点附近趋于 0，用未收敛的 B 计算 H 会放大误差。
    /// 注意二：极点。X = Y = 0 时 L 由 atan2(0, 0) 给出 0（可以接受，极点本就没有经度），
    ///   但 p = 0 时 cosB → 0，H 的表达式会变成 0/0。本项目的处理是：极点附近 lat 由 Z 的符号直接确定，
    ///   H 用 |Z| − b（见下），不套用 p/cosB。
    /// 注意三：迭代必须设上限次数（例如 100 次）后退出，否则错误输入会使自检无法结束。
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
    /// 进公式前必须换算单位：rx、ry、rz 由角秒 → 弧度要 ×(π/180/3600)；
    ///   m 由 ppm → ×1e-6。这两处换算最容易遗漏（遗漏后 XY 会偏到几万公里外）。
    /// (1+m) 乘的是**整个旋转后的向量**，而不是只乘 Z；平移量**不**乘 (1+m)。
    ///   这两处写错的症状都是"结果看似正确、但差几米到几十米"，只有按量级核对才能发现。
    /// 约定：本方法采用**位置矢量**约定。若参数来自坐标框架约定的资料，
    ///   把三个旋转角同时取负再传入即可（等价于把矩阵转置）。
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
    /// 各调用使用哪个椭球是这里的关键：进 XYZ 用**源**椭球，出 BLH 用**目标**椭球。
    ///
    /// 转换后大地高通常突变几十到上百米（例如从 50 米变成 170 多米）——这不是缺陷：
    ///   两个基准的地心差有上百米，同一个地面点在两套基准里的"大地高"本就不相同。
    ///   本项目不显示大地高，但自检会打印该值，以呈现这一变化。
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
    /// 返回新图层而不就地修改原图层的原因：
    ///   "原始数据"是唯一的真相（它记录的是文件中所写基准下的经纬度）。保留它，界面上更换「源基准」
    ///   才能重算；若就地修改，换回去就无法还原（信息已经丢失）。
    ///
    /// 除坐标外，其余内容一样都不能少：FileName、SkippedCount、每个 feature 的 Geometry.Kind、
    ///   点的**个数与顺序**（面的首尾闭合点也要跟着走）、Properties。
    ///   DisplayName 由属性计算得出，属性在就自然正确。
    ///
    /// 四个方向都要支持（参数是**有方向**的）：
    ///   · source 与 target 是同一个基准 → 恒等，不做任何改动；
    ///   · target 是 CGCS2000   → 用 source.ToCgcs2000（正向）；
    ///   · source 是 CGCS2000   → 用 target.ToCgcs2000，**七个参数全部取负**（反向；
    ///     小角度下取负是工程上通用的近似，误差远小于参数本身的精度）；
    ///   · 两个都不是 CGCS2000  → 分两步：source → CGCS2000 → target。
    ///
    /// 大地高在 GeoPoint 里没有存储位置，因此由参数给出（默认 0）：这是把"假设所有点都位于地面"
    ///   这一前提**写在签名上**，而不是隐藏在代码里。本项目固定传 0。
    ///
    /// 中转（两个都不是 CGCS2000）时，参数只负责"进第一腿"；**第一腿算出的中间大地高要带进第二腿**。
    ///   丢弃中间高度（第二腿继续用 0）会使结果差约 1.7 毫米 —— 量级很小，但这是一个"约定"，
    ///   不写在注释里就只能靠猜测，自检 [13] 按"带过去"校验。
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
            // 单点转换：三种中转情形只在这里分一次，下面按部件循环时不用再写三遍。
            GeoPoint Convert(GeoPoint point)
            {
                if (source.Ellipsoid == Ellipsoid.CGCS2000)
                {
                    return Transform(point, heightMeters, source.Ellipsoid, target.Ellipsoid, temporary).Point;
                }

                if (target.Ellipsoid == Ellipsoid.CGCS2000)
                {
                    return Transform(point, heightMeters, source.Ellipsoid, target.Ellipsoid, source.ToCgcs2000).Point;
                }

                var (temp1, temp2) = Transform(point, heightMeters, source.Ellipsoid, DatumCatalog.Cgcs2000.Ellipsoid, source.ToCgcs2000);
                return Transform(temp1, temp2, DatumCatalog.Cgcs2000.Ellipsoid, target.Ellipsoid, temporary).Point;
            }

            var sourceGeometry = feature.Geometry;

            IReadOnlyList<IReadOnlyList<GeoPoint>> parts;
            IReadOnlyList<GeoPoint> points;

            if (sourceGeometry.Parts.Count == 0)
            {
                // 单部件：Points 就是全部。
                parts = Array.Empty<IReadOnlyList<GeoPoint>>();
                points = sourceGeometry.Points.Select(Convert).ToList().AsReadOnly();
            }
            else
            {
                // ★ 多部件必须**逐块**转换再拼回 Parts。
                //   以前这里只转换 Points —— 而 Parts 非空时 Points 就是 Parts[0]（Models\GeoGeometry.cs:28），
                //   于是 Parts[1..] 整块被丢掉：ne_10m 的 8,531 个部件转换后只剩 4,595 个，
                //   海岛的省在图上只剩主岛。它不报错、不崩溃，只是"东西安静地少了"。
                var convertedParts = new List<IReadOnlyList<GeoPoint>>(sourceGeometry.Parts.Count);
                foreach (var part in sourceGeometry.Parts)
                {
                    convertedParts.Add(part.Select(Convert).ToList().AsReadOnly());
                }

                parts = convertedParts;
                points = convertedParts[0];   // 维持"Parts 非空时 Points == Parts[0]"这条约定
            }

            transformedFeatures.Add(new GeoFeature
            {
                Geometry = new GeoGeometry { Kind = sourceGeometry.Kind, Points = points, Parts = parts },
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
