using System.IO;
using GeoJsonViewer.Models;

namespace GeoJsonViewer.Logic;

/// <summary>
/// 「打开文件」的统一入口：按扩展名分派到 GeoJSON 或 shapefile 读路径。
/// 放在 Logic 层是为了让自检能直接调到这里，而不是去碰界面里的分支。
/// </summary>
public static class LayerReader
{
    /// <summary>支持的扩展名（小写、带点）。界面过滤器与错误提示都从这里生成，避免多处各写一份。</summary>
    public static readonly string[] SupportedExtensions = { ".geojson", ".json", ".shp" };

    /// <summary>
    /// 读文件。路径为空、扩展名不认识、内容坏掉，一律抛 <see cref="GeoJsonException"/>（带中文说明），
    /// 由界面统一 catch 成状态栏里的一行字，不让窗口崩掉。
    /// </summary>
    public static GeoLayer ReadFile(string path, ShapefileReadOptions? shapefileOptions = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new GeoJsonException("此文件路径为空");

        string ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) throw new GeoJsonException("不支持无后缀的文件");

        // 扩展名统一转小写再比：Windows 文件系统不区分大小写，用户敲出 .SHP 也得认。
        string smallExt = ext.TrimStart('.').ToLowerInvariant();
        switch (smallExt)
        {
            case "json":
            case "geojson":
                return GeoJsonReader.ReadFile(path);
            case "shp":
                return ShapefileReader.ReadFile(path, shapefileOptions);
            default:
                throw new GeoJsonException(
                    $"不支持后缀名为{smallExt}的文件，请确保文件后缀名为【.geojson、.json、.shp】之一");
        }
    }

    /// <summary>OpenFileDialog 的过滤器（WPF 格式：「显示文本|通配符|…」）。</summary>
    public static string BuildDialogFilter()
    {
        var patterns = string.Join(";", Array.ConvertAll(SupportedExtensions, e => "*" + e));
        return $"所有支持的文件 ({patterns})|{patterns}"
             + "|GeoJSON (*.geojson;*.json)|*.geojson;*.json"
             + "|shapefile (*.shp)|*.shp"
             + "|所有文件 (*.*)|*.*";
    }
}
