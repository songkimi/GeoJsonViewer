namespace GeoJsonViewer;

using System.Windows;

/// <summary>
/// 应用入口。
///
/// 两个入口由这里分流：
///   · 正常启动      → 打开主窗口；
///   · `--selftest` → 不显示窗口，只跑"读文件 → 投影 → fit → 映射"这条计算链路并打印结果。
///
/// 为什么自检不弹窗口：这条链路的正确性不需要肉眼，只需要数字；
/// 能一条命令看结果，改代码的循环就快得多。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0 && e.Args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            Shutdown(Diagnostics.SelfTest.Run(e.Args.Length > 1 ? e.Args[1] : null));
            return;
        }

        new Views.MainWindow().Show();
    }
}
