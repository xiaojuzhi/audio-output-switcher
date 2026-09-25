<#
  “扬声器 (2)”重复虚拟声卡修复工具  v2
  ------------------------------------------------------------------
  问题：SoundID Reference 的虚拟声卡装了不止一份，产生了两个同名“扬声器”。
        只要那份多余的记录还留在系统里，Windows 就会把 (2) 挂到正在使用的
        那台设备上，表现为“开机时没有、过一会儿（SoundID 自启后）又冒出来”。

  本工具做三件事（只处理 SoundID / Sonarworks 的虚拟声卡，绝不碰其他设备）：
    1) 停用并卸载多余的虚拟声卡实例；
    2) 清除它残留在系统里的播放设备记录；
    3) 刷新音频服务，然后重新列出设备名做验证。

  用法：双击同目录的「修复重复声卡.bat」（会请求管理员权限）。
       只看状态、不做改动：powershell -File fix-duplicate-soundcard.ps1 -ScanOnly
       恢复被停用的实例  ：运行后在菜单里选 2
#>

param([switch]$ScanOnly)

$ErrorActionPreference = 'Stop'

$MEDIA_ROOT  = 'HKLM:\SYSTEM\CurrentControlSet\Enum\ROOT\MEDIA'
$ENUM_ROOT   = 'HKLM:\SYSTEM\CurrentControlSet\Enum'
$RENDER_ROOT = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render'
$PKEY_INSTANCE   = '{b3f8fa53-0004-438e-9003-51a46e139bfc},2'   # 端点属于哪个设备实例
$PKEY_NAME       = '{a45c254e-df1c-4efd-8020-67d146a850e0},2'   # 设备描述
$SERVICE_PATTERN = 'soundid|sonarworks'

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-Instances {
    # 找出所有由 SoundID / Sonarworks 驱动创建的虚拟音频设备实例
    $list = @()
    if (-not (Test-Path $MEDIA_ROOT)) { return $list }
    foreach ($key in Get-ChildItem $MEDIA_ROOT) {
        $props = Get-ItemProperty -Path $key.PSPath -ErrorAction SilentlyContinue
        if ($null -eq $props) { continue }
        $service = [string]$props.Service
        if ($service -notmatch $SERVICE_PATTERN) { continue }

        $instanceId = 'ROOT\MEDIA\' + $key.PSChildName
        $desc = [string]$props.DeviceDesc
        if ($desc -match ';(.+)$') { $desc = $Matches[1] }      # 去掉 @oem23.inf,%...%; 前缀
        $flags = [int]$props.ConfigFlags

        $eps = @()
        if (Test-Path $RENDER_ROOT) {
            foreach ($ep in Get-ChildItem $RENDER_ROOT) {
                $p = Get-ItemProperty -Path ($ep.PSPath + '\Properties') -ErrorAction SilentlyContinue
                if ($null -eq $p) { continue }
                if ([string]$p.$PKEY_INSTANCE -ne ('{1}.' + $instanceId)) { continue }
                $state = (Get-ItemProperty -Path $ep.PSPath -Name DeviceState -ErrorAction SilentlyContinue).DeviceState
                $name = [string]$p.$PKEY_NAME
                $eps += [pscustomobject]@{
                    Guid  = $ep.PSChildName
                    Key   = $ep.PSPath
                    Name  = if ($name) { $name } else { '(未命名)' }
                    State = [int]$state
                }
            }
        }

        $list += [pscustomobject]@{
            InstanceId = $instanceId
            Service    = $service
            Desc       = $desc
            Disabled   = (($flags -band 1) -ne 0)
            Endpoints  = $eps
        }
    }
    return $list
}

function Get-StateText([int]$state) {
    switch ($state) {
        1 { '可用（正在使用中）' }
        2 { '已停用' }
        4 { '未接入（多余残留）' }
        8 { '未插入' }
        default { "状态码 $state" }
    }
}

function Show-Report($instances) {
    Write-Host ''
    Write-Host '================ 检测结果 ================' -ForegroundColor Cyan
    $n = 0
    foreach ($inst in $instances) {
        $n++
        Write-Host ("[{0}] {1}" -f $n, $inst.Desc) -ForegroundColor White
        Write-Host ("    设备实例：{0}{1}" -f $inst.InstanceId, $(if ($inst.Disabled) { '（已停用）' } else { '' })) -ForegroundColor Gray
        if ($inst.Endpoints.Count -eq 0) {
            Write-Host '    对应的播放设备：无' -ForegroundColor Gray
        } else {
            foreach ($ep in $inst.Endpoints) {
                $color = if ($ep.State -eq 1) { 'Green' } else { 'DarkYellow' }
                Write-Host ("    对应的播放设备：{0} -> {1}" -f $ep.Name, (Get-StateText $ep.State)) -ForegroundColor $color
            }
        }
    }
    Write-Host '==========================================' -ForegroundColor Cyan
}

function Invoke-Disable([string]$instanceId) {
    & pnputil.exe /disable-device "$instanceId" 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { return $true }
    try { Disable-PnpDevice -InstanceId $instanceId -Confirm:$false -ErrorAction Stop; return $true }
    catch { return $false }
}

function Invoke-Enable([string]$instanceId) {
    & pnputil.exe /enable-device "$instanceId" 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { return $true }
    try { Enable-PnpDevice -InstanceId $instanceId -Confirm:$false -ErrorAction Stop; return $true }
    catch { return $false }
}

function Invoke-Remove([string]$instanceId) {
    & pnputil.exe /remove-device "$instanceId" 2>&1 | Out-Null
    return ($LASTEXITCODE -eq 0)
}

function Remove-LeftoverEndpoints {
    # 清掉“已被卸载 / 已被停用”的 SoundID 实例残留下来的播放设备记录
    $removed = @()
    foreach ($ep in Get-ChildItem $RENDER_ROOT) {
        $p = Get-ItemProperty -Path ($ep.PSPath + '\Properties') -ErrorAction SilentlyContinue
        if ($null -eq $p) { continue }
        $owner = [string]$p.$PKEY_INSTANCE
        if ($owner -notmatch '^\{1\}\.ROOT\\MEDIA\\') { continue }

        $instName = $owner.Substring(4)                        # 去掉开头的 "{1}."
        $instKey  = Join-Path $ENUM_ROOT $instName
        $svc = (Get-ItemProperty -Path $instKey -Name Service -ErrorAction SilentlyContinue).Service
        if ([string]$svc -notmatch $SERVICE_PATTERN) { continue }    # 只处理 SoundID 的

        $state = (Get-ItemProperty -Path $ep.PSPath -Name DeviceState -ErrorAction SilentlyContinue).DeviceState
        if ([int]$state -eq 1) { continue }                          # 正在用的设备绝不动

        $alive = Test-Path $instKey
        $flags = (Get-ItemProperty -Path $instKey -Name ConfigFlags -ErrorAction SilentlyContinue).ConfigFlags
        $orphan = (-not $alive) -or (([int]$flags -band 1) -ne 0)
        if (-not $orphan) { continue }

        try {
            Remove-Item -LiteralPath $ep.PSPath -Recurse -Force -ErrorAction Stop
            $removed += $ep.PSChildName
        } catch { }
    }
    return $removed
}

function Restart-AudioServiceSafe {
    try { Restart-Service -Name Audiosrv -Force -ErrorAction Stop; return $true } catch { }
    try {
        & net.exe stop Audiosrv /y 2>&1 | Out-Null
        Start-Sleep -Seconds 2
        & net.exe start Audiosrv 2>&1 | Out-Null
        return $true
    } catch { return $false }
}

function Show-CurrentNames {
    # 调用同目录的切换器，看看系统现在把设备叫什么名字
    $exe = Join-Path $PSScriptRoot '音频切换器.exe'
    if (-not (Test-Path $exe)) { return }
    Write-Host ''
    Write-Host '当前系统里的输出设备名：' -ForegroundColor Cyan
    Start-Sleep -Seconds 3
    & $exe --list 2>&1 | Out-Null
}

# ----------------------------------------------------------------------------
#  主流程
# ----------------------------------------------------------------------------

if (-not $ScanOnly -and -not (Test-IsAdmin)) {
    Write-Host '正在申请管理员权限，请在弹窗里选“是”...' -ForegroundColor Yellow
    $p = $MyInvocation.MyCommand.Path
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $p + '"')
    )
    exit
}

Clear-Host
Write-Host ''
Write-Host '  重复虚拟声卡修复工具  v2' -ForegroundColor Cyan
Write-Host '  （清理 SoundID Reference 装出来的多个虚拟声卡及其残留记录）' -ForegroundColor Gray

$instances = @(Get-Instances)

if ($instances.Count -eq 0) {
    Write-Host ''
    Write-Host '没有发现 SoundID / Sonarworks 相关的虚拟声卡设备。' -ForegroundColor Yellow
    Write-Host '可能已经清理干净，或者虚拟声卡已被卸载。'
    Write-Host ''
    if (-not $ScanOnly) { Read-Host '按回车键关闭' | Out-Null }
    exit
}

Show-Report $instances

if ($ScanOnly) { exit }

$active = @($instances | Where-Object { @($_.Endpoints | Where-Object { $_.State -eq 1 }).Count -gt 0 })
$idle   = @($instances | Where-Object { @($_.Endpoints | Where-Object { $_.State -eq 1 }).Count -eq 0 })

Write-Host ''
Write-Host ("共发现 {0} 个虚拟声卡实例：正在工作的 {1} 个，多余的 {2} 个。" -f `
    $instances.Count, $active.Count, $idle.Count) -ForegroundColor White
Write-Host ''
Write-Host '  [1] 修复（推荐）：卸载多余的虚拟声卡并清掉残留记录，然后刷新音频'
Write-Host '  [2] 恢复：把被停用的虚拟声卡重新启用回来'
Write-Host '  [0] 退出，什么都不做'
Write-Host ''
$choice = Read-Host '请输入数字后回车'

if ($choice -eq '1') {
    if ($instances.Count -lt 2) {
        Write-Host ''
        Write-Host '只发现 1 个虚拟声卡实例，没有重复的设备。' -ForegroundColor Yellow
        Write-Host '如果你仍然看到“扬声器 (2)”，请重启电脑后重新运行本工具检查。'
        Write-Host ''
        Read-Host '按回车键关闭' | Out-Null
        exit
    }

    if ($active.Count -eq 1) {
        $targets = $idle                                      # 保留唯一在工作的那个
    } else {
        Write-Host ''
        if ($active.Count -eq 0) {
            Write-Host '现在没有任何 SoundID 虚拟声卡处于工作状态，脚本不会自动猜。' -ForegroundColor Yellow
            Write-Host '（建议先打开一次 SoundID Reference，或者重启后再运行。）'
        } else {
            Write-Host '有多个实例都处于工作状态，无法自动判断。' -ForegroundColor Yellow
        }
        $pick = Read-Host '请输入要【卸载】的编号（直接回车=取消）'
        $idx = 0
        if ([int]::TryParse($pick, [ref]$idx) -and $idx -ge 1 -and $idx -le $instances.Count) {
            $targets = @($instances[$idx - 1])
        } else {
            Write-Host '已取消，没有做任何改动。'
            Read-Host '按回车键关闭' | Out-Null
            exit
        }
    }

    Write-Host ''
    foreach ($t in $targets) {
        Write-Host ("处理 {0} ..." -f $t.InstanceId) -ForegroundColor White
        if (-not $t.Disabled) {
            Write-Host '    停用设备 ...' -NoNewline
            if (Invoke-Disable $t.InstanceId) { Write-Host ' 完成' -ForegroundColor Green }
            else { Write-Host ' 跳过（可能已经处于停用状态）' -ForegroundColor DarkYellow }
        }
        Write-Host '    卸载多余的虚拟声卡 ...' -NoNewline
        if (Invoke-Remove $t.InstanceId) { Write-Host ' 完成' -ForegroundColor Green }
        else { Write-Host ' 失败（设备可能正被占用）' -ForegroundColor Red }
    }

    Write-Host ''
    Write-Host '清除残留的设备记录 ...' -NoNewline
    $removed = @(Remove-LeftoverEndpoints)
    if ($removed.Count -gt 0) { Write-Host (" 完成（清掉 {0} 条）" -f $removed.Count) -ForegroundColor Green }
    else { Write-Host ' 没有需要清除的记录' -ForegroundColor DarkYellow }

    Write-Host '刷新音频服务 ...' -NoNewline
    if (Restart-AudioServiceSafe) { Write-Host ' 完成' -ForegroundColor Green }
    else { Write-Host ' 未能自动刷新（重启电脑后效果相同）' -ForegroundColor DarkYellow }

    Write-Host ''
    Write-Host '重新检测：' -ForegroundColor Cyan
    Show-Report @(Get-Instances)
    Show-CurrentNames

    Write-Host ''
    Write-Host '处理完毕。请这样确认：' -ForegroundColor Cyan
    Write-Host '  1) 看看上面的设备名里还有没有“(2)”；'
    Write-Host '  2) 打开 设置 -> 系统 -> 声音 -> 所有声音设备，确认重复项是否消失；'
    Write-Host '  3) 如果还没变化，重启一次电脑（设备列表重启后才会彻底刷新）；'
    Write-Host '  4) 万一 SoundID Reference 提示找不到虚拟声卡，打开它的系统级处理'
    Write-Host '     (Systemwide) 重新启用一次，它会自动装回一个干净的虚拟声卡。'
}
elseif ($choice -eq '2') {
    Write-Host ''
    foreach ($t in $instances) {
        Write-Host ("启用 {0} ..." -f $t.InstanceId) -NoNewline
        if (Invoke-Enable $t.InstanceId) { Write-Host ' 完成' -ForegroundColor Green }
        else { Write-Host ' 失败' -ForegroundColor Red }
    }
    Write-Host ''
    Write-Host '已尝试恢复。建议重启电脑后再查看声音设置。' -ForegroundColor Cyan
}
else {
    Write-Host '已退出，没有做任何改动。'
}

Write-Host ''
Read-Host '按回车键关闭' | Out-Null
