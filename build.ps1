# 这个脚本是给"在受限沙箱里干活"用的；本机终端直接跑 `dotnet run` 也一样。
# 作用：把 dotnet 的临时目录和 NuGet 包目录都指到项目文件夹里，
#       免得去写 C:\Users\... 下面那些没权限的地方。
#
# 用法：
#   pwsh -File build.ps1                  # 编译并运行程序
#   pwsh -File build.ps1 build            # 只编译
#   pwsh -File build.ps1 selftest         # 无窗口自检：打印"读文件→投影→fit→屏幕坐标"
#
# 为什么 selftest 单独一个分支：
#   这是 WPF 的 WinExe 程序，直接 `& GeoJsonViewer.exe --selftest` 拿不到输出、
#   也读不到退出码（它自己不附着控制台）。必须让 cmd 做 OS 级重定向到文件。
#   （Start-Process -Wait -PassThru 也不行：它的 ExitCode 是空的。）
#
# 在终端里跑 `.\build.ps1` 若报「禁止运行脚本」，那是 PowerShell 的执行策略，
# 与这个脚本无关。两种绕法：
#   pwsh -File .\build.ps1 selftest
#   powershell -ExecutionPolicy Bypass -File .\build.ps1 selftest

param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Args)

$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet-home'
$env:NUGET_PACKAGES  = Join-Path $PSScriptRoot '.nuget-packages'
$env:DOTNET_NOLOGO   = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

if ($Args.Count -eq 0) { $Args = @('run') }

if ($Args[0] -eq 'selftest') {
    if ($Args.Count -eq 1) { $Args = @('selftest', '--selftest') }
    $argList = ($Args[1..($Args.Count - 1)] -join ' ')

    $exe = Join-Path $PSScriptRoot 'bin\Debug\net10.0-windows\GeoJsonViewer.exe'
    $out = Join-Path $PSScriptRoot '.selftest-output.txt'

    $buildOut = dotnet build -v q --nologo 2>&1
    $buildCode = $LASTEXITCODE

    # 这一关不能省：编译失败时 bin 里的 exe/dll 还是上一次的，
    # 接下来这次自检会跑旧程序、打印一份看着正常的旧输出——比报错更容易骗人。
    # （最常见的原因是程序还开着：bin\...\GeoJsonViewer.exe 被占用，
    #   复制新 exe 失败会连累同一个目标里的 dll 也不更新。）
    if ($buildCode -ne 0) {
        Write-Host ''
        Write-Host "[X] 编译失败（退出码 $buildCode），自检没有跑。" -ForegroundColor Red
        Write-Host '    编译错误如下；如果是"文件正由另一进程使用"，把程序窗口关掉再来。' -ForegroundColor Yellow
        foreach ($line in $buildOut) { Write-Host "    $line" }
        exit $buildCode
    }

    # 必须走 cmd 的重定向，不能用 PowerShell 的 & exe *> file：
    # WinExe 程序不附着控制台，PS 的 *> 重定向抓不到它的 Console.WriteLine，
    # 结果就是"退出码对、输出全空"。只有 cmd /c 的 OS 级重定向能拿到。
    cmd /c "`"$exe`" $argList > `"$out`" 2>&1"
    $code = $LASTEXITCODE

    Get-Content $out
    $global:LASTEXITCODE = $code
    exit $code
}

dotnet @Args
exit $LASTEXITCODE
