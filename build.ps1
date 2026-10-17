param([switch]$Test)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework C# compiler is required (included with .NET Framework 4.x on Windows).' }
$output = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$sources = @('Program.cs', 'GitRepository.cs', 'DiffView.cs', 'DarkScrollBar.cs', 'GroupedFileList.cs', 'RecentRepositories.cs', 'RepositoryPicker.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /target:winexe /optimize+ /utf8output "/out:$output\DiffTool.exe" "/win32icon:$PSScriptRoot\assets\DiffTool.ico" "/resource:$PSScriptRoot\assets\DiffTool.ico,DiffTool.AppIcon" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll $sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Host "Built $output\DiffTool.exe"
if ($Test) {
    & $compiler /nologo /target:exe /utf8output "/out:$output\DiffTool.Tests.exe" "/reference:$output\DiffTool.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll (Join-Path $PSScriptRoot 'tests\Tests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & "$output\DiffTool.Tests.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
