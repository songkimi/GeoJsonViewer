namespace GeoJsonViewer.Logic;

/// <summary>
/// 「这份 .prj 说明该用哪套源基准」的建议 —— 界面只负责照着做，判断都在这里。
///
/// 为什么值得单独一层：这件事有四条互斥的分支（没有声明 / 是投影坐标系 / 认得出 / 认不出），
/// 而其中只有一条该"擅自替用户改下拉框"。把它写成纯函数，自检就能一条条考，不用开窗口。
/// </summary>
/// <param name="Datum">建议的源基准；null = 别改（保持用户当前的选择）。</param>
/// <param name="FromName">true = 按 DATUM 名字认出来的；false = 只靠椭球参数猜的（该在提示里说清楚）。</param>
/// <param name="HasToWgs84">声明里带了七参数吗（原样透传 PrjInfo.HasToWgs84）。</param>
/// <param name="Warning">给状态栏的一句话；null = 没什么要提醒的。</param>
public sealed record PrjSuggestion(
    Datum? Datum,
    bool FromName,
    bool HasToWgs84,
    string? Warning)
{
    /// <summary>
    /// 按 .prj 的内容给出建议（prj 为 null = 没有 .prj / 读不出来）。
    ///
    /// 四条分支：
    ///   ① 没有 .prj         → 不建议（Datum = null），提示"没有坐标系声明"；
    ///   ② 是投影坐标系      → 不建议，提示"坐标是米、不是经纬度"（这时认基准没有意义）；
    ///   ③ 认得出基准        → 建议它；按名字认的没有警告，按椭球猜的要说明"是猜的"；
    ///   ④ 认不出            → 不建议，提示手动选。
    /// </summary>
    public static PrjSuggestion For(PrjInfo? prj)
    {
        
        if (prj is null)
            return new PrjSuggestion(null, false, false, "没有 .prj 声明，无法判断基准，请手动选择");
        if (prj.IsProjected)
            return new PrjSuggestion(null, false, prj.HasToWgs84, "这是投影坐标系（坐标单位是米），不参与基准识别");
        if (PrjReader.MatchDatum(prj) is not { } match)
            return new PrjSuggestion(null, false, prj.HasToWgs84, "无法识别这套基准，请手动选择");
        return new PrjSuggestion(match.Datum,match.FromName,prj.HasToWgs84,match.FromName ? null : "基准是按椭球参数猜的，位置可能有米级偏差");
    }
}
