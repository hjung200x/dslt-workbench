[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string]$PackageRoot,

    [Parameter(Mandatory)]
    [string]$EvidencePath,

    [ValidateRange(0, 400)]
    [int]$ExpectedDpiPercent = 0,

    [ValidateRange(5, 120)]
    [int]$StartupTimeoutSeconds = 30,

    [switch]$RequireCuda,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$packageRootPath = (Resolve-Path -LiteralPath $PackageRoot).Path
$applicationPath = Join-Path $packageRootPath 'Dslt.App.exe'
$nativePath = Join-Path $packageRootPath 'dslt_core.dll'
$buildInfoPath = Join-Path $packageRootPath 'BUILD-INFO.json'
foreach ($requiredPath in @($applicationPath, $nativePath, $buildInfoPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required package file is missing: $requiredPath"
    }
}

$evidenceFullPath = [IO.Path]::GetFullPath($EvidencePath)
$evidenceChecksumPath = "$evidenceFullPath.sha256"
if (((Test-Path -LiteralPath $evidenceFullPath) -or
     (Test-Path -LiteralPath $evidenceChecksumPath)) -and -not $Force) {
    throw "Evidence or its checksum already exists; pass -Force to replace it: $evidenceFullPath"
}
$evidenceDirectory = Split-Path -Parent $evidenceFullPath
if (-not (Test-Path -LiteralPath $evidenceDirectory)) {
    New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
}

$buildInfo = Get-Content -LiteralPath $buildInfoPath -Raw | ConvertFrom-Json
if ($buildInfo.runtimeIdentifier -ne 'win-x64' -or $buildInfo.selfContained -ne $true) {
    throw 'Host validation requires a self-contained win-x64 package.'
}
if ($buildInfo.sourceCommit -notmatch '^[0-9a-f]{40}$' -or
    $buildInfo.nativeSourceCommit -notmatch '^[0-9a-f]{40}$') {
    throw 'BUILD-INFO.json does not contain full lowercase managed/native source commits.'
}

$windowsVersion = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$evidence = [ordered]@{
    schemaVersion = 1
    capturedAtUtc = [DateTime]::UtcNow.ToString('O')
    passed = $false
    error = $null
    host = [ordered]@{
        computerName = $env:COMPUTERNAME
        productName = $windowsVersion.ProductName
        displayVersion = $windowsVersion.DisplayVersion
        currentBuild = $windowsVersion.CurrentBuild
        ubr = $windowsVersion.UBR
        osVersion = [Environment]::OSVersion.Version.ToString()
        processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    }
    package = [ordered]@{
        root = $packageRootPath
        version = $buildInfo.packageVersion
        backend = $buildInfo.backend
        runtimeIdentifier = $buildInfo.runtimeIdentifier
        selfContained = $buildInfo.selfContained
        sourceCommit = $buildInfo.sourceCommit
        nativeSourceCommit = $buildInfo.nativeSourceCommit
        validationLevel = $buildInfo.validationLevel
        applicationSha256 = (Get-FileHash -LiteralPath $applicationPath -Algorithm SHA256).Hash.ToLowerInvariant()
        nativeSha256 = (Get-FileHash -LiteralPath $nativePath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    ui = $null
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
if (-not ('DsltHostValidationNative' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DsltHostValidationNative {
    [DllImport("user32.dll")] public static extern bool MoveWindow(
        IntPtr window, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll")] static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    public static bool IsPerMonitorV2(IntPtr window) {
        return AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(window), new IntPtr(-4));
    }
}
'@
}

$process = $null
$failure = $null
try {
    $process = Start-Process -FilePath $applicationPath -WorkingDirectory $packageRootPath -WindowStyle Normal -PassThru
    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    do {
        $process.Refresh()
        if ($process.HasExited) {
            throw "DSLT Workbench exited during startup with code $($process.ExitCode)."
        }
        if ($process.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    if ($process.MainWindowHandle -eq 0) {
        throw "DSLT Workbench did not create a main window within $StartupTimeoutSeconds seconds."
    }

    $processCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $process.Id)
    $root = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children,
        $processCondition)
    if ($null -eq $root) { throw 'The DSLT Workbench UI Automation root was not found.' }

    $defaultBounds = $root.Current.BoundingRectangle.ToString()
    $dpi = [DsltHostValidationNative]::GetDpiForWindow($process.MainWindowHandle)
    $dpiPercent = [int][Math]::Round($dpi / 96.0 * 100.0)
    $perMonitorV2 = [DsltHostValidationNative]::IsPerMonitorV2($process.MainWindowHandle)
    if (-not $perMonitorV2) { throw 'The main window is not running with PerMonitorV2 DPI awareness.' }
    if ($ExpectedDpiPercent -gt 0 -and $dpiPercent -ne $ExpectedDpiPercent) {
        throw "Expected $ExpectedDpiPercent% DPI scaling, but the window reported $dpiPercent%."
    }

    [void][DsltHostValidationNative]::MoveWindow($process.MainWindowHandle, 50, 50, 900, 500, $true)
    Start-Sleep -Milliseconds 500
    $minimumBounds = $root.Current.BoundingRectangle.ToString()

    $readinessDeadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    do {
        $all = $root.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        $focusableWithoutName = [Collections.Generic.List[string]]::new()
        $statusTexts = [Collections.Generic.List[string]]::new()
        for ($index = 0; $index -lt $all.Count; $index++) {
            $element = $all.Item($index)
            if ($element.Current.IsKeyboardFocusable -and [string]::IsNullOrWhiteSpace($element.Current.Name)) {
                $focusableWithoutName.Add($element.Current.ControlType.ProgrammaticName)
            }
            if ($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text -and
                $element.Current.Name -match 'CPU|CUDA|Native core') {
                $statusTexts.Add($element.Current.Name)
            }
        }
        $statusSummary = $statusTexts -join ' | '
        if ($statusSummary -match 'Native core ready') { break }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $readinessDeadline)
    if ($focusableWithoutName.Count -ne 0) {
        throw "Focusable elements without accessible names: $($focusableWithoutName -join ', ')"
    }

    if ($statusSummary -notmatch 'Native core ready') {
        throw "The native core did not report ready: $statusSummary"
    }
    if ($RequireCuda -and $statusSummary -notmatch 'CUDA:') {
        throw "The host did not report an available CUDA device: $statusSummary"
    }

    $scrollPatterns = @{}
    foreach ($scrollName in @('Navigation and volume controls', 'Processing and editing controls')) {
        $scrollCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $scrollName)
        $scrollElement = $root.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $scrollCondition)
        if ($null -eq $scrollElement) { throw "Scrollable control group was not found: $scrollName" }
        $scrollPatternObject = $null
        if (-not $scrollElement.TryGetCurrentPattern(
            [System.Windows.Automation.ScrollPattern]::Pattern,
            [ref]$scrollPatternObject)) {
            throw "Control group does not support ScrollPattern: $scrollName"
        }
        $scrollPatterns[$scrollName] = [System.Windows.Automation.ScrollPattern]$scrollPatternObject
    }

    $commandResults = [ordered]@{}
    foreach ($commandName in @('Open TIFF / LSM / CZI', 'Run', 'Cancel', 'Export result + provenance')) {
        $scrollName = if ($commandName -eq 'Open TIFF / LSM / CZI') {
            'Navigation and volume controls'
        } else {
            'Processing and editing controls'
        }
        $verticalPercent = if ($commandName -eq 'Open TIFF / LSM / CZI') { 0 } else { 100 }
        $scrollPatterns[$scrollName].SetScrollPercent(
            [System.Windows.Automation.ScrollPattern]::NoScroll,
            $verticalPercent)
        Start-Sleep -Milliseconds 100

        $typeCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)
        $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $commandName)
        $buttonCondition = New-Object System.Windows.Automation.AndCondition($typeCondition, $nameCondition)
        $button = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
        if ($null -eq $button) { throw "Primary command was not found: $commandName" }
        $visible = -not $button.Current.IsOffscreen
        $commandResults[$commandName] = $visible
        if (-not $visible) { throw "Primary command could not be scrolled into view: $commandName" }
    }

    $evidence.ui = [ordered]@{
        defaultBounds = $defaultBounds
        minimumBounds = $minimumBounds
        dpi = $dpi
        dpiPercent = $dpiPercent
        expectedDpiPercent = $ExpectedDpiPercent
        requireCuda = [bool]$RequireCuda
        perMonitorV2 = $perMonitorV2
        automationElementCount = $all.Count
        focusableWithoutNameCount = $focusableWithoutName.Count
        primaryCommandsVisible = $commandResults
        statusTexts = @($statusTexts)
    }

    $closeRequested = $process.CloseMainWindow()
    if (-not $closeRequested -or -not $process.WaitForExit(10000)) {
        throw 'DSLT Workbench did not accept a normal close request within 10 seconds.'
    }
    $evidence.ui.normalClose = $true
    $evidence.passed = $true
}
catch {
    $failure = $_
    $evidence.error = $_.Exception.Message
}
finally {
    if ($null -ne $process) {
        $process.Refresh()
        if (-not $process.HasExited) {
            if ($process.Path -ne $applicationPath) {
                throw "Refusing to stop an unexpected process: $($process.Path)"
            }
            Stop-Process -Id $process.Id -Force
        }
    }
    $evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $evidenceFullPath -Encoding utf8
    $evidenceHash = (Get-FileHash -LiteralPath $evidenceFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $evidenceName = Split-Path -Leaf $evidenceFullPath
    Set-Content -LiteralPath $evidenceChecksumPath -Value "$evidenceHash  $evidenceName" -Encoding ascii
}

if ($null -ne $failure) { throw $failure }
Write-Host "Windows host validation passed: $evidenceFullPath"
Write-Host "Evidence SHA-256: $evidenceHash"
