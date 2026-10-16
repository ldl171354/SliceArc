# open-externally-test.ps1 — 端到端验证「双击归档内文件 → 临时副本 → 系统程序打开」
# 会真的弹出一个记事本窗口，测完自动关闭。
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class W {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint d,UIntPtr e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@
$App = 'D:\SliceArc\dist\app\SliceArc.App.exe'
$Arc = 'D:\SliceArc\artifacts\drag-shapes\shapes.7z'

$p = Start-Process -FilePath $App -ArgumentList @($Arc) -PassThru
Start-Sleep -Seconds 7
$pass = $false
try {
    $root = [Windows.Automation.AutomationElement]::RootElement
    $pidCond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $win = $root.FindAll([Windows.Automation.TreeScope]::Children, $pidCond) |
        Where-Object { $_.Current.Name -match 'shapes' } | Select-Object -First 1
    if (-not $win) { Write-Output '  没找到主窗口'; exit 1 }
    [W]::SetForegroundWindow([IntPtr]$win.Current.NativeWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 600

    # 关掉可能弹出的首次引导
    $wb = $win.FindFirst([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, '稍后再说')))
    if ($wb) {
        $wr = $wb.Current.BoundingRectangle
        [W]::SetCursorPos([int]($wr.X+$wr.Width/2), [int]($wr.Y+$wr.Height/2)) | Out-Null
        Start-Sleep -Milliseconds 250
        [W]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 60; [W]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero)
        Start-Sleep -Seconds 2
    }

    $grid = $win.FindFirst([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::DataGrid)))
    $itemCond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::DataItem)
    $rows = $grid.FindAll([Windows.Automation.TreeScope]::Descendants, $itemCond)

    # 找 note.txt 那一行（小文本，记事本一定能开）
    $target = $null
    foreach ($r in $rows) {
        $texts = $r.FindAll([Windows.Automation.TreeScope]::Descendants,
            (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text)))
        foreach ($t in $texts) { if ($t.Current.Name -eq 'note.txt') { $target = $r; break } }
        if ($target) { break }
    }
    if (-not $target) { Write-Output '  列表里没有 note.txt'; exit 1 }

    $rr = $target.Current.BoundingRectangle
    $x = [int]($rr.X + 140); $y = [int]($rr.Y + $rr.Height/2)
    Write-Output "  双击 note.txt @ ($x,$y)"
    [W]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 400
    [W]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 60; [W]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 120
    [W]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 60; [W]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero)

    Start-Sleep -Seconds 4
    $np = Get-Process notepad -EA SilentlyContinue | Where-Object { $_.MainWindowTitle -match 'note\.txt' } | Select-Object -First 1
    if ($np) {
        Write-Output "  ✅ 记事本已打开该条目: 标题='$($np.MainWindowTitle)'"
        $tmp = Get-ChildItem ([IO.Path]::GetTempPath()) -Directory -Filter 'SliceArc-open-*' -EA SilentlyContinue
        Write-Output "  临时副本目录: $(($tmp | ForEach-Object { $_.Name }) -join ', ')"
        if ($tmp) {
            foreach ($d in $tmp) { Get-ChildItem $d.FullName -Recurse -File | ForEach-Object { Write-Output "    落盘 $($_.Name) $($_.Length) B" } }
        }
        $pass = $true
        $np.CloseMainWindow() | Out-Null
    } else {
        Write-Output '  ❌ 没有弹出记事本'
        $titles = (Get-Process notepad -EA SilentlyContinue | ForEach-Object { $_.MainWindowTitle }) -join '|'
        Write-Output "    notepad 进程: $(if($titles){$titles}else{'无'})"
    }
} finally {
    if (-not $p.HasExited) { $p.Kill() }
    Get-Process notepad -EA SilentlyContinue | Where-Object { $_.MainWindowTitle -match 'note\.txt' } | ForEach-Object { $_.CloseMainWindow() | Out-Null }
    Start-Sleep -Milliseconds 500
    # 关窗后临时目录应被我们清掉
    $left = Get-ChildItem ([IO.Path]::GetTempPath()) -Directory -Filter 'SliceArc-open-*' -EA SilentlyContinue
    Write-Output "  关窗后残留临时副本: $(if ($left) { ($left.Name) -join ', ' } else { '（无，已清理 ✅）' })"
}
Write-Output "结果: $(if ($pass) {'PASS'} else {'FAIL'})"
