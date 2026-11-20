# Builds CopilotToRCtrl.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$src = Join-Path $PSScriptRoot 'CopilotToRCtrl.cs'
$out = Join-Path $PSScriptRoot 'CopilotToRCtrl.exe'
& $csc /nologo /target:winexe /platform:x64 /optimize+ /r:System.Core.dll /r:Microsoft.CSharp.dll "/out:$out" $src
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
Write-Host "Built $out"
