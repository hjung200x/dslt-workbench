[CmdletBinding()]
param(
    [switch]$WriteGitHubSummary
)

$ErrorActionPreference = 'Stop'
$issues = [Collections.Generic.List[string]]::new()
$report = [ordered]@{
    gpu = $null
    cudaToolkit = $null
    cmake = $null
    visualStudio2022 = $null
    dotnetSdk = $null
    ready = $false
}

function Find-Command([string]$Name) {
    return Get-Command $Name -ErrorAction SilentlyContinue
}

$nvidiaSmi = Find-Command 'nvidia-smi'
if (-not $nvidiaSmi) {
    $issues.Add('nvidia-smi was not found; install a supported NVIDIA driver.')
} else {
    $gpu = (& $nvidiaSmi.Source --query-gpu=name,driver_version,memory.total --format=csv,noheader 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gpu)) {
        $issues.Add('nvidia-smi could not query an NVIDIA GPU.')
    } else {
        $report.gpu = $gpu.Trim()
    }
}

$nvcc = Find-Command 'nvcc'
if (-not $nvcc) {
    $issues.Add('nvcc was not found; install CUDA Toolkit 13.2 and expose its bin directory on PATH.')
} else {
    $nvccOutput = (& $nvcc.Source --version 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $nvccOutput -notmatch 'release\s+([0-9]+\.[0-9]+)') {
        $issues.Add('nvcc did not report a parseable CUDA Toolkit release.')
    } else {
        $report.cudaToolkit = $Matches[1]
        if ($report.cudaToolkit -ne '13.2') {
            $issues.Add("CUDA Toolkit 13.2 is required; found $($report.cudaToolkit).")
        }
    }
}

$cmakeCommand = Find-Command 'cmake'
if (-not $cmakeCommand) {
    $issues.Add('cmake was not found; install CMake 3.30 or newer and expose it on PATH.')
} else {
    $cmakeOutput = (& $cmakeCommand.Source --version 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $cmakeOutput -notmatch 'cmake version\s+([0-9]+\.[0-9]+\.[0-9]+)') {
        $issues.Add('cmake did not report a parseable version.')
    } else {
        $cmakeVersion = [version]$Matches[1]
        $report.cmake = $cmakeVersion.ToString()
        if ($cmakeVersion -lt [version]'3.30.0') {
            $issues.Add("CMake 3.30 or newer is required; found $cmakeVersion.")
        }
    }
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    $issues.Add('vswhere was not found; install Visual Studio 2022 Build Tools.')
} else {
    $vsPath = (& $vswhere -latest -version '[17.0,18.0)' -products * `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 Microsoft.VisualStudio.Component.VC.CMake.Project `
        -property installationPath 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($vsPath)) {
        $issues.Add('Visual Studio 2022 with x64 C++ tools and CMake integration was not found.')
    } else {
        $report.visualStudio2022 = $vsPath.Trim()
    }
}

$dotnet = Find-Command 'dotnet'
if (-not $dotnet) {
    $issues.Add('dotnet was not found; install the .NET 10 SDK.')
} else {
    $sdks = @(& $dotnet.Source --list-sdks 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $issues.Add('dotnet --list-sdks failed.')
    } else {
        $dotnet10 = @($sdks | Where-Object { $_ -match '^10\.[0-9]+\.[0-9]+' })
        if ($dotnet10.Count -eq 0) {
            $issues.Add('.NET 10 SDK was not found.')
        } else {
            $report.dotnetSdk = ($dotnet10 -join '; ')
        }
    }
}

$report.ready = $issues.Count -eq 0
$reportJson = $report | ConvertTo-Json -Depth 3
Write-Output $reportJson

if ($WriteGitHubSummary -and -not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
    $summary = @(
        '### NVIDIA runner evidence',
        '',
        "- GPU: $($report.gpu)",
        "- CUDA Toolkit: $($report.cudaToolkit)",
        "- CMake: $($report.cmake)",
        "- Visual Studio 2022: $($report.visualStudio2022)",
        "- .NET 10 SDK: $($report.dotnetSdk)",
        "- Commit: $env:GITHUB_SHA",
        "- Ready: $($report.ready)"
    )
    $summary | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
}

if ($issues.Count -ne 0) {
    throw "CUDA runner prerequisites failed:`n - $($issues -join "`n - ")"
}
