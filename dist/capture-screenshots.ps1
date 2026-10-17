#Requires -Version 5.1
# Captura automatizada das telas do Systenclin para o README.
# Uso: powershell -ExecutionPolicy Bypass -File capture-screenshots.ps1 [-Exe <caminho>] [-OutDir <pasta>]
# Sobe uma PRÓPRIA instância do app, troca de aba por automação de interface
# (AutomationId = x:Name dos RadioButtons) e salva um PNG por aba.
param(
    [string]$Exe = (Join-Path $PSScriptRoot 'Systenclin.exe'),
    [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'docs\screenshots')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class CapWin {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hgt, bool repaint);
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L,T,Rt,B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
}
"@

if (-not (Test-Path -LiteralPath $Exe)) { throw "exe nao encontrado: $Exe" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# Tamanho que cabe na tela: janela grande para caber o maximo do conteudo.
$area = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$winW = [Math]::Min(1180, $area.Width - 120)
$winH = [Math]::Min(900, $area.Height - 80)

$proc = Start-Process -FilePath $Exe -PassThru

try {
    # Espera a janela principal aparecer (ate 30 s).
    $hwnd = [IntPtr]::Zero
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        $proc.Refresh()
        if ($proc.HasExited) { throw "o aplicativo encerrou durante a captura" }
        if ($proc.MainWindowHandle -ne 0) { $hwnd = $proc.MainWindowHandle; break }
    }
    if ($hwnd -eq [IntPtr]::Zero) { throw "janela principal nao apareceu" }

    [CapWin]::ShowWindow($hwnd, 9) | Out-Null        # SW_RESTORE
    [CapWin]::MoveWindow($hwnd, 60, 40, $winW, $winH, $true) | Out-Null
    [CapWin]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 1800                    # deixa a janela carregar

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)

    function Get-ById([string]$automationId) {
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
        return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    }

    function Invoke-ById([string]$automationId) {
        $el = Get-ById $automationId
        if ($null -eq $el) { return $false }

        $pattern = $null
        if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
            $pattern.Invoke()
            return $true
        }
        if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
            $pattern.Select()
            return $true
        }
        if ($el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) {
            $pattern.Toggle()
            return $true
        }
        return $false
    }

    # Espera um elemento aparecer na arvore (ex.: resultados da analise).
    function Wait-For-Id([string]$automationId, [int]$seconds = 20) {
        for ($i = 0; $i -lt ($seconds * 4); $i++) {
            if ($null -ne (Get-ById $automationId)) { return $true }
            Start-Sleep -Milliseconds 250
        }
        return $false
    }

    function Test-Enabled([string]$automationId) {
        $el = Get-ById $automationId
        if ($null -eq $el) { return $false }
        return [bool]$el.GetCurrentPropertyValue(
            [System.Windows.Automation.AutomationElement]::IsEnabledProperty)
    }

    function Select-Tab([string]$automationId) {
        if (-not (Invoke-ById $automationId)) {
            throw "aba nao encontrada/ativavel: $automationId"
        }
        Start-Sleep -Milliseconds 900
    }

    function Capture-Window([string]$fileName) {
        $r = New-Object CapWin+R
        [CapWin]::GetWindowRect($hwnd, [ref]$r) | Out-Null

        # Recuo: o rect inclui a moldura invisivel/rosa do Windows 11 e a
        # sombra; sem o recorte as bordas pegam o que esta atras da janela.
        $insetL = 12; $insetR = 12; $insetT = 10; $insetB = 24
        $w = ($r.Rt - $r.L) - $insetL - $insetR
        $h = ($r.B - $r.T) - $insetT - $insetB

        $bmp = New-Object System.Drawing.Bitmap $w, $h
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen(($r.L + $insetL), ($r.T + $insetT), 0, 0, (New-Object System.Drawing.Size $w, $h))
        $path = Join-Path $OutDir $fileName
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose()
        $bmp.Dispose()
        Write-Output "salvo: $path"
    }

    # Rola a aba ate o elemento ficar visivel (cartao de relatorios, por ex.).
    function Scroll-To([string]$automationId) {
        $el = Get-ById $automationId
        if ($null -eq $el) { return }

        # Caminho direto: o ancestral de rolagem sabe trazer o item.
        $pattern = $null
        if ($el.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$pattern)) {
            try { $pattern.ScrollIntoView(); return } catch { }
        }

        # Alternativa: sobe a arvore procurando quem suporta rolagem e vai ao fim.
        $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        $anc = $el
        for ($i = 0; $i -lt 14; $i++) {
            $anc = $walker.GetParent($anc)
            if ($null -eq $anc) { break }
            $sp = $null
            if ($anc.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$sp)) {
                try {
                    $sp.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 100)
                }
                catch { }
                return
            }
        }
    }

    # Arquivos: roda a analise (so leitura) para a captura mostrar dados reais.
    Select-Tab 'TabFiles'
    [void](Invoke-ById 'ScanButton')
    [void](Wait-For-Id 'ResultsPanel' 30)
    Start-Sleep -Milliseconds 600
    Capture-Window '01-arquivos.png'

    Select-Tab 'TabPrivacy'
    Capture-Window '02-privacidade.png'

    # Otimizacao: rola ate o cartao de relatorios de energia ficar visivel.
    Select-Tab 'TabOptimize'
    Scroll-To 'PowerEnergyButton'
    Start-Sleep -Milliseconds 500
    Capture-Window '03-otimizacao.png'

    # Aplicativos: espera a sondagem do winget e roda a busca (so leitura).
    Select-Tab 'TabApps'
    Start-Sleep -Milliseconds 1500
    if (Test-Enabled 'AppsScanButton') {
        [void](Invoke-ById 'AppsScanButton')
        Start-Sleep -Milliseconds 1200
        for ($i = 0; $i -lt 160; $i++) {
            if (Test-Enabled 'AppsScanButton') { break }
            Start-Sleep -Milliseconds 250
        }
    }
    Start-Sleep -Milliseconds 500
    Capture-Window '04-aplicativos.png'
}
finally {
    try {
        if (-not $proc.HasExited) {
            $proc.CloseMainWindow() | Out-Null
            if (-not $proc.WaitForExit(5000)) { $proc.Kill() }
        }
    }
    catch { }
}
