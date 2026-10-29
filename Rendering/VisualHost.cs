namespace GeoJsonViewer.Rendering;

using System.Windows;
using System.Windows.Media;

/// <summary>
/// 只用来"装一个视觉对象"的壳。
///
/// 为什么需要它：<see cref="DrawingVisual"/> 是 <see cref="Visual"/>，不是 <see cref="UIElement"/>，
/// 而 <c>Canvas.Children</c> 只收 <see cref="UIElement"/>。所以想往画布上放"一整张画好的图"，
/// 就得有这么一层壳把 <see cref="Visual"/> 包成 <see cref="UIElement"/>。
///
/// 这一层壳几乎不花钱：它没有模板、没有内容、不参与布局测量（下面两个重写就是全部），
/// 相比之前的 8,531 个 <see cref="System.Windows.Shapes.Shape"/> 少掉了 8,531 份
/// "布局 → 属性 → 渲染数据"的账。合并渲染省下来的就是这些。
/// </summary>
internal sealed class VisualHost : FrameworkElement
{
    private readonly Visual _content;

    public VisualHost(Visual content) => _content = content;

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _content;
}
