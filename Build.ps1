param([switch]$Console)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The Windows .NET Framework C# compiler is missing.'
}
$target = if ($Console) { 'exe' } else { 'winexe' }
$outputName = if ($Console) { 'PeripheralBattery-Diagnostics.exe' } else { 'PeripheralBattery.exe' }
$output = Join-Path $PSScriptRoot $outputName
& $compiler /nologo "/target:$target" /platform:anycpu /optimize+ "/out:$output" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll (Join-Path $PSScriptRoot 'BatteryTray.cs') (Join-Path $PSScriptRoot 'DeviceReader.cs') (Join-Path $PSScriptRoot 'ReceiverBattery.cs')
if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE." }
Write-Output $output


