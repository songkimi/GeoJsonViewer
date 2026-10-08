namespace GeoJsonViewer.Logic;

/// <summary>
/// 解析 GeoJSON 时"整个文件都用不了"的那类错误。
///
/// 为什么自定义一个异常类型，而不是直接抛 Exception：
///   界面需要在 catch 里区分"这是用户数据的问题"和"这是我自己代码的 bug"。
///   前者默默显示一行中文提示就好；后者要暴露出来，别被一个温和的提示掩盖掉。
///   有了专属类型，catch (GeoJsonException) 就能精确地只接住前者。
/// </summary>
public sealed class GeoJsonException : Exception
{
    public GeoJsonException(string message) : base(message) { }
}
