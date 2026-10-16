# drag-to-explorer.ps1 — 真鼠标拖拽验证：把压缩包条目拖进一个真实资源管理器窗口
# 落点是 Explorer 窗口（目标目录明确、结果可校验），不再受桌面/任务栏歧义影响。
# 合成鼠标在 WPF 上不稳定（有时触发不了 PreviewMouseMove），所以按日志判断是否真的
# 起了拖拽，没起就重试。
param(
    [string]$App = 'D:\SliceArc\dist\app\SliceArc.App.exe',
    [string]$Arc = 'D:\SliceArc\artifacts\dragout-test\dragtest.7z',
    [int]$Attempts = 4
)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class M {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
}
"@

$log = Join-Path (Split-Path $App) 'SliceArc.log'
$target = 'D:\SliceArc\artifacts\dragout-drop'
Remove-Item $target -Recurse -Force -EA SilentlyContinue
New-Item -ItemType Directory $target | Out-Null

Start-Process explorer.exe -ArgumentList $target
Start-Sleep -Seconds 3

Write-Output "被测程序: $App"
$p = Start-Process -FilePath $App -ArgumentList @($Arc) -PassThru
Start-Sleep -Seconds 7

function CountDragLog { if (Test-Path $log) { @(Get-Content $log -EA SilentlyContinue | Select-String '拖拽:').Count } else { 0 } }

$orig = New-Object M+POINT
[M]::GetCursorPos([ref]$orig) | Out-Null

try {
    $root = [Windows.Automation.AutomationElement]::RootElement
    $pidCond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $kids = $root.FindAll([Windows.Automation.TreeScope]::Children, $pidCond)
    $win = $kids | Where-Object { $_.Current.Name -match 'dragtest|shapes|\.7z|\.zip|\.rar' } | Select-Object -First 1
    if (-not $win) { $win = $kids | Select-Object -First 1 }
    if (-not $win) { Write-Output '  找不到被测窗口'; exit 1 }

    $allWin = $root.FindAll([Windows.Automation.TreeScope]::Children, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Window)))
    $expWin = $allWin | Where-Object { $_.Current.Name -match 'dragout-drop' } | Select-Object -First 1
    if (-not $expWin) { Write-Output '  找不到资源管理器窗口'; exit 1 }

    $hwnd = [IntPtr]$win.Current.NativeWindowHandle
    [M]::SetForegroundWindow($hwnd) | Out-Null
    [M]::ShowWindowAsync($hwnd, 9) | Out-Null
    Start-Sleep -Milliseconds 600
    $ehwnd = [IntPtr]$expWin.Current.NativeWindowHandle

    $wb = $win.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, '稍后再说')))
    if ($wb) {
        $wr = $wb.Current.BoundingRectangle
        [M]::SetCursorPos([int]($wr.X+$wr.Width/2), [int]($wr.Y+$wr.Height/2)) | Out-Null
        Start-Sleep -Milliseconds 250
        [M]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 80; [M]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero)
        Start-Sleep -Seconds 2
        Write-Output '  （关掉首次引导）'
    }

    $grid = $null
    for ($wait = 1; $wait -le 8 -and -not $grid; $wait++) {
        $grid = $win.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::DataGrid)))
        if (-not $grid) { Start-Sleep -Milliseconds 900 }
    }
    if (-not $grid) { Write-Output '  没有 DataGrid（等待超时）'; exit 1 }
    $rows = $grid.FindAll([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::DataItem)))
    if ($rows.Count -eq 0) { Write-Output '  列表为空'; exit 1 }

    for ($try = 1; $try -le $Attempts; $try++) {
        # 每轮都重排两个窗口，保证互不遮挡且起点/终点是最新坐标
        [M]::SetWindowPos($hwnd, [IntPtr]::Zero, 20, 40, 880, 540, 0x0040) | Out-Null
        [M]::SetWindowPos($ehwnd, [IntPtr]::Zero, 1000, 200, 820, 600, 0x0040) | Out-Null
        Start-Sleep -Milliseconds 700
        [M]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 300

        $before = CountDragLog
        $r0 = $rows[0].Current.BoundingRectangle
        $sx = [int]($r0.X + 140); $sy = [int]($r0.Y + $r0.Height/2)
        $ex = 1400; $ey = 470
        Write-Output "  第 $try 次：($sx,$sy) → 资源管理器窗口 ($ex,$ey)"
        [M]::SetCursorPos($sx, $sy) | Out-Null
        Start-Sleep -Milliseconds 500
        [M]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero)
        Start-Sleep -Milliseconds 400
        for ($i = 1; $i -le 28; $i++) {
            [M]::SetCursorPos([int]($sx + ($ex-$sx)*$i/28), [int]($sy + ($ey-$sy)*$i/28)) | Out-Null
            Start-Sleep -Milliseconds 40
        }
        Start-Sleep -Milliseconds 800
        [M]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero)
        Start-Sleep -Seconds 5

        if (CountDragLog -gt $before) {
            Write-Output '  拖拽诊断：'
            Get-Content $log -Tail 18 | Select-String '拖拽:' | Select-Object -Last 8 | ForEach-Object { Write-Output ("    " + ($_ -replace '^(\S+ \S+) ','')) }
            break
        }
        Write-Output '    （未触发 WPF 拖拽，重试）'
    }
}
finally {
    [M]::SetCursorPos($orig.X, $orig.Y) | Out-Null
    if (-not $p.HasExited) { $p.Kill() }
}

$got = Get-ChildItem $target -File -EA SilentlyContinue
Write-Output "目标文件夹结果: $(if ($got) { ($got | ForEach-Object { "$($_.Name)($($_.Length)B)" }) -join ', ' } else { '空 —— 拖拽未产出文件' })"
