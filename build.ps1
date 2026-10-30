param([switch]$Test)
$ErrorActionPreference = 'Stop'
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$taskCsc = Join-Path $taskFramework 'csc.exe'
$taskWpf = Join-Path $taskFramework 'WPF'
$taskOut = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force -Path $taskOut | Out-Null
$taskReferences = @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll') | ForEach-Object { '/reference:' + (Join-Path $taskFramework $_) }
$taskReferences += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $taskWpf $_) }
$taskSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$taskExe = Join-Path $taskOut 'TailnetMonitor.exe'
$taskOptions = @('/nologo','/optimize+','/platform:x64','/target:winexe',('/out:' + $taskExe),('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),('/win32icon:' + (Join-Path $PSScriptRoot 'icon.ico')),('/resource:' + (Join-Path $PSScriptRoot 'src\Main.xaml') + ',Main.xaml'))
& $taskCsc @taskOptions @taskReferences @taskSources
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
$taskSize = (Get-Item -LiteralPath $taskExe).Length
if ($taskSize -ge 5000000) { throw "EXE exceeds 5 MB: $taskSize bytes" }
Write-Output "Built: $taskExe ($taskSize bytes)"
if ($Test) {
    $taskTestExe = Join-Path $taskOut 'MonitorTests.exe'
    & $taskCsc /nologo /optimize+ /platform:x64 /target:exe "/out:$taskTestExe" @taskReferences "/reference:$taskExe" (Join-Path $PSScriptRoot 'tests\Tests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & $taskTestExe
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
