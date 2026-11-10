$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskSdk = (& dotnet --list-sdks | Select-Object -Last 1)
if ($taskSdk -notmatch '^(\S+) \[(.+)\]$') { throw 'Cannot locate .NET SDK' }
$taskCompiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn/bincore/csc.dll'
$taskRuntime = (& dotnet --list-runtimes | Where-Object { $_ -match '^Microsoft.NETCore.App 6\.' } | Select-Object -Last 1)
if ($taskRuntime -notmatch '^Microsoft.NETCore.App (\S+) \[(.+)\]$') { throw 'Requires .NET 6 runtime' }
$taskVersion = $Matches[1]
$taskRuntimePath = Join-Path $Matches[2] $taskVersion
$taskBuild = Join-Path ([IO.Path]::GetTempPath()) ('CargoResourcesTests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskBuild | Out-Null
$taskArgs = @('/target:exe', '/langversion:10', ('/out:"' + $taskBuild + '/tests.dll"'))
foreach ($taskDll in Get-ChildItem -LiteralPath $taskRuntimePath -Filter '*.dll') {
    try { $null = [Reflection.AssemblyName]::GetAssemblyName($taskDll.FullName) }
    catch { continue }
    $taskArgs += ('/reference:"' + $taskDll.FullName + '"')
}
foreach ($taskSource in @('Models.cs', 'CargoResources.cs', 'tests/CargoResources.Tests.cs.txt')) {
    $taskArgs += ('"' + (Join-Path $taskRoot $taskSource) + '"')
}
$taskRsp = Join-Path $taskBuild 'compile.rsp'
[IO.File]::WriteAllLines($taskRsp, $taskArgs)
& dotnet $taskCompiler ('@' + $taskRsp)
if ($LASTEXITCODE -ne 0) { throw 'Resource test compilation failed' }
$taskConfig = @{ runtimeOptions = @{ tfm = 'net6.0'; framework = @{ name = 'Microsoft.NETCore.App'; version = $taskVersion } } }
[IO.File]::WriteAllText((Join-Path $taskBuild 'tests.runtimeconfig.json'), ($taskConfig | ConvertTo-Json -Depth 5))
& dotnet (Join-Path $taskBuild 'tests.dll')
if ($LASTEXITCODE -ne 0) { throw 'Resource tests failed' }
