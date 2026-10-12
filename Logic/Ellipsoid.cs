namespace GeoJsonViewer.Logic;

/// <summary>
/// 参考椭球：把不规则、有起伏的地球近似成一个"绕短轴旋转出来的椭球"，两个数就能描述 ——
/// 长半轴 a（赤道半径，米）与扁率倒数 1/f（越扁 f 越大、1/f 越小）。
///
/// 为什么突然需要一个新类型：
///   球面墨卡托（EPSG:3857）是**故意**用球体算的，最大能差到二十公里 —— 它只管"图看起来像"。
///   而高斯-克吕格、UTM 这类正经测量投影必须换成椭球，否则同一份经纬度会算偏几百米。
///
/// 为什么列了四种椭球：这是"基准（datum）"的另一半。同一个经纬度数值，
/// 放在北京54 的克拉索夫斯基椭球上和放在 CGCS2000 的椭球上，算出的平面坐标能差上百米。
/// 这不是谁算错了，而是两套坐标系的原点和形状本来就不一样 ——
/// 真要在两套坐标之间换算，还得先用七参数做基准转换，光换椭球是不够的（下一阶段的事）。
/// </summary>
public sealed record Ellipsoid(string Name, double A, double InverseFlattening)
{
    /// <summary>扁率 f = 1/(1/f)。</summary>
    public double F => 1.0 / InverseFlattening;

    /// <summary>短半轴 b = a(1 − f)，极半径。</summary>
    public double B => A * (1.0 - F);

    /// <summary>第一偏心率的平方 e² = f(2 − f)，衡量"扁的程度"；e² 出现在所有椭球公式里。</summary>
    public double E2 => F * (2.0 - F);

    /// <summary>第二偏心率的平方 e'² = e²/(1 − e²)。正算公式里的 η² 就是 e'²·cos²B。</summary>
    public double Ep2 => E2 / (1.0 - E2);

    // 四个常用椭球。CGCS2000 与 WGS84 几乎重合（扁率只差 1e-9 量级），
    // 但北京54、西安80 明显不同：手上的老地图必须用当年那套椭球，才会对得上。
    public static readonly Ellipsoid CGCS2000 = new("CGCS2000", 6378137.0, 298.257222101);
    public static readonly Ellipsoid WGS84 = new("WGS84", 6378137.0, 298.257223563);
    public static readonly Ellipsoid Beijing54 = new("北京54（克拉索夫斯基）", 6378245.0, 298.3);
    public static readonly Ellipsoid Xian80 = new("西安80（IAG75）", 6378140.0, 298.257);
}
