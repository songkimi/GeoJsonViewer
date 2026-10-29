namespace GeoJsonViewer.Rendering;

using GeoJsonViewer.Models;

/// <summary>
/// 一次空间查询的返回项：一个**部件**及其所属的对象。
///
/// 为什么不是直接返回 GeoFeature：
///   索引的粒度可能是"部件级"（自选题 A 的答案之一）。渲染本来就是**按部件**画的
///   （SimpleRenderer 里那段 foreach (var part in geometry.Parts)），所以返回部件最贴合下游。
///   归属的对象留在手上，是因为：
///     ① 渲染要按对象画（点半径/线宽跟对象类型、跟 Kind 绑在一起），只拿到点串不知道该怎么画；
///     ② 悬停命中之后，界面要显示的是**对象**的名字（GeoFeature 的属性），不是一串坐标；
///     ③ 第 7 步"失效"要能从一个候选反查到它属于哪个对象，才能做增量更新。
///   ——这条注释是做完自选题 A 之后补的：那次对比证明了"按部件给候选"比"按对象给候选"
///   数字小得多（一个横跨全国的对象的包围盒会盖住整个视野），而**下游完全不需要知道这件事**。
/// </summary>
public sealed record GeoPartHit(GeoFeature Feature, IReadOnlyList<GeoPoint> Points);
