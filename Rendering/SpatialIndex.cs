namespace GeoJsonViewer.Rendering;

/// <summary>
/// 空间查询的抽象。
///
/// 为什么要把"接口"和"实现"分开，而不是直接在 MainWindow 里调 STRtree：
///   因为**先要有一个"慢但一定对"的参照实现**（见第八节第 1 步）。
///   有了这层接口，两种实现可以互换，而调用方的代码一个字都不用变——
///   于是"索引有没有改坏东西"才能被证明，而不是被相信。
///
/// 名字里不带 Index 两个字是有意的：它回答的是"这个矩形里有什么"，
/// 至于底下是遍历还是查树，是这个类的实现细节，调用方不该知道。
/// </summary>
public interface IFeatureFinder
{
    /// <summary>返回与矩形相交的候选（不保证真的相交，可以多给，不能少给）。</summary>
    IReadOnlyList<GeoPartHit> Query(double minX, double minY, double maxX, double maxY);
}
