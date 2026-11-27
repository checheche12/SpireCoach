param(
    [Parameter(Mandatory)][string]$Upstream,
    [Parameter(Mandatory)][string]$GameDir,
    [Parameter(Mandatory)][string]$Game114Dir,
    [Parameter(Mandatory)][string]$StableGameDir,
    [string]$CheckpointDir,
    [string]$Dotnet = 'dotnet',
    [string]$Python = 'python',
    [string]$SimRepository,
    [switch]$ReuseModel
)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$out = Join-Path $project 'dist/SpireCoach'
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { "$env:USERPROFILE/.nuget/packages" }
$native = "$nuget/microsoft.ml.onnxruntime/1.22.1"
$managed = "$nuget/microsoft.ml.onnxruntime.managed/1.22.1"
$tensors = "$nuget/system.numerics.tensors/9.0.0"
if ((git -C $Upstream rev-parse HEAD) -ne 'f77831b6ea77b8ece32daf1912154ae081da875c') { throw 'AlphaDefect source revision mismatch' }
$games = [ordered]@{ Stable = $StableGameDir; Beta = $GameDir; Beta114 = $Game114Dir }
$simRevision = 'f65056f92e670daee99a1d60f37d507f4ba7563f'
$simServer = Join-Path $Upstream 'game-v0.111.0/engine/Sts2SimServe'
$simCache = Join-Path $project 'worker/obj/sim-source'
if (!$SimRepository) {
    $SimRepository = Join-Path $simCache 'repo'
    if (!(Test-Path "$SimRepository/.git")) {
        git clone -q --no-checkout https://github.com/iRyougi/sts2-sim.git $SimRepository
        if ($LASTEXITCODE) { throw 'sts2-sim checkout failed' }
    }
}
New-Item -ItemType Directory -Force -Path $simCache | Out-Null
$compatPatch = Join-Path $PSScriptRoot 'live-boss-history.patch'
$mapSource = Join-Path $PSScriptRoot 'VisibleActMap.cs'
$simWork = Join-Path $simCache ($simRevision + '-' + (Get-FileHash $compatPatch).Hash.Substring(0,12) + (Get-FileHash $mapSource).Hash.Substring(0,8))
if (!(Test-Path "$simWork/.patched")) {
    $tmp = Join-Path $simCache ([guid]::NewGuid().ToString('N'))
    git -C $SimRepository archive --format=zip "--output=$tmp.zip" $simRevision
    if ($LASTEXITCODE) { throw 'Pinned sts2-sim source is missing' }
    Expand-Archive -LiteralPath "$tmp.zip" -DestinationPath $tmp
    foreach ($group in 'import-*','obs-*','fidelity-*','regent-*') {
        foreach ($patch in (Get-ChildItem "$simServer/patches/$group.patch" | Sort-Object Name)) {
            git -C $tmp apply --no-index --whitespace=nowarn $patch.FullName
            if ($LASTEXITCODE) { throw "sts2-sim patch failed: $($patch.Name)" }
        }
    }
    git -C $tmp apply --no-index --whitespace=nowarn $compatPatch
    if ($LASTEXITCODE) { throw 'Live boss history adapter patch failed' }
    Copy-Item $mapSource "$tmp/src/Sts2Sim.Core/Map/VisibleActMap.cs"
    New-Item -ItemType File -Path "$tmp/.patched" | Out-Null
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($tmp)) -ne [IO.Path]::GetFullPath($simCache) -or
        [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($simWork)) -ne [IO.Path]::GetFullPath($simCache)) { throw 'Invalid sim cache path' }
    Move-Item -LiteralPath $tmp -Destination $simWork
    Remove-Item -LiteralPath "$tmp.zip"
}
& $Dotnet build "$simServer/Sts2SimServe.csproj" -p:WithObs=true "-p:Sts2SimDir=$simWork" -c Release -v:q
if ($LASTEXITCODE) { throw 'sts2-sim bridge build failed' }
& $Dotnet build "$project/mod/loader/SpireCoach.csproj" -c Release "-p:GameData=$StableGameDir/data_sts2_windows_x86_64" -v:q
if ($LASTEXITCODE -ne 0) { throw 'Mod loader build failed' }
foreach ($branch in $games.Keys) {
    $game = $games[$branch]
    & $Dotnet build "$project/mod/SpireCoach.csproj" -c Release "-p:Upstream=$Upstream" "-p:GameBranch=$branch" "-p:GameData=$game/data_sts2_windows_x86_64" -v:q
    if ($LASTEXITCODE -ne 0) { throw "$branch adapter build failed" }
}
New-Item -ItemType Directory -Force -Path "$out/runtime/search","$out/licenses" | Out-Null
foreach ($file in 'Sts2Sim.Core.dll','Sts2SimServe.dll','System.IO.Hashing.dll') {
    Copy-Item "$simServer/bin/Release/net9.0/$file" "$out/runtime"
}
Copy-Item "$simServer/ObsPort/sim_tables.json" "$out/runtime"
Copy-Item "$Upstream/engine/catalog/no_downside_relics.txt" "$out/runtime/no_downside_relics.txt"
Copy-Item "$simWork/LICENSE" "$out/licenses/sts2-sim-LICENSE.txt"
Copy-Item "$nuget/system.io.hashing/9.0.0/LICENSE.TXT" "$out/licenses/Hashing-LICENSE.txt"
Copy-Item "$nuget/system.io.hashing/9.0.0/THIRD-PARTY-NOTICES.TXT" "$out/licenses/Hashing-ThirdPartyNotices.txt"
foreach ($branch in $games.Keys) {
    $rid = if ($branch -eq 'Stable') { 'win-x64' } else { 'linux-x64' }
    $game = $games[$branch]
    $workerOut = "$project/worker/bin/Release/$branch-$rid"
    & $Dotnet publish "$project/worker/SearchHost.csproj" -c Release --no-self-contained -r $rid "-p:GameBranch=$branch" "-p:Upstream=$Upstream" "-p:GameData=$game/data_sts2_windows_x86_64" -o $workerOut -v:q
    if ($LASTEXITCODE) { throw "$branch search worker build failed" }
    Copy-Item "$workerOut/SpireCoach.SearchHost.dll" "$out/runtime/search/$branch.dll"
    $hostName = if ($rid -eq 'win-x64') { 'SpireCoach.SearchHost.exe' } else { 'SpireCoach.SearchHost' }
    Copy-Item "$workerOut/$hostName" "$out/runtime/search/$hostName"
}
$models = Get-Content -Encoding UTF8 -Raw -LiteralPath "$PSScriptRoot/models.json" | ConvertFrom-Json
if (!$CheckpointDir -and !$ReuseModel) { throw 'Specify -CheckpointDir containing the six released weights' }
foreach ($version in 'v0.113.1','v0.114.0') {
    $modelDir = if ($version -eq 'v0.114.0') { Join-Path $out "models/$version" } else { $out }
    New-Item -ItemType Directory -Force -Path $modelDir | Out-Null
    foreach ($entry in $models.PSObject.Properties) {
        $model = Join-Path $modelDir ($entry.Name + '.onnx')
        $checkpoint = if ($CheckpointDir) { Join-Path $CheckpointDir $entry.Value } else { $entry.Value }
        $verify = @()
        if ($ReuseModel) { $verify += '--verify' }
        & $Python -X utf8 "$project/build/export_model.py" $Upstream $checkpoint $model --game-version $version @verify
        if ($LASTEXITCODE -ne 0) { throw "Model export or verification failed for $version/$($entry.Name)" }
    }
}
Copy-Item -LiteralPath "$project/mod/loader/bin/Release/net9.0/SpireCoach.dll","$project/mod/SpireCoach.json" -Destination $out
foreach ($branch in $games.Keys) {
    Copy-Item -LiteralPath "$project/mod/bin/Release/$branch/net9.0/SpireCoach.$branch.dll" -Destination "$out/runtime"
}
Copy-Item -LiteralPath "$native/runtimes/win-x64/native/onnxruntime.dll","$native/runtimes/win-x64/native/onnxruntime_providers_shared.dll","$managed/lib/net8.0/Microsoft.ML.OnnxRuntime.dll","$tensors/lib/net9.0/System.Numerics.Tensors.dll" -Destination "$out/runtime"
Copy-Item -LiteralPath "$native/runtimes/linux-x64/native/libonnxruntime.so","$native/runtimes/linux-x64/native/libonnxruntime_providers_shared.so" -Destination "$out/runtime"
Copy-Item "$Upstream/LICENSE" "$out/licenses/AlphaDefect-LICENSE.txt"
Copy-Item "$Upstream/NOTICE.md" "$out/licenses/AlphaDefect-NOTICE.md"
Copy-Item "$native/LICENSE" "$out/licenses/ONNXRuntime-LICENSE.txt"
Copy-Item "$native/ThirdPartyNotices.txt" "$out/licenses/ONNXRuntime-ThirdPartyNotices.txt"
Copy-Item "$managed/ThirdPartyNotices.txt" "$out/licenses/ONNXRuntime-Managed-ThirdPartyNotices.txt"
Copy-Item "$tensors/LICENSE.TXT" "$out/licenses/Tensors-LICENSE.txt"
Copy-Item "$tensors/THIRD-PARTY-NOTICES.TXT" "$out/licenses/Tensors-ThirdPartyNotices.txt"
Copy-Item "$project/docs/usage.md" "$out/README.md"
$zip = Join-Path $project 'dist/SpireCoach-x64.zip'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$tempZip = "$zip.$([guid]::NewGuid()).tmp"
try {
    $archive = [IO.Compression.ZipFile]::Open($tempZip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $root = (Get-Item -LiteralPath $out).FullName
        foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) {
            $entry = 'SpireCoach/' + $file.FullName.Substring($root.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $archive.Dispose() }
    Copy-Item -LiteralPath $tempZip -Destination $zip -Force
} finally { if (Test-Path -LiteralPath $tempZip) { Remove-Item -LiteralPath $tempZip } }

$content = Join-Path $project 'workshop/content'
New-Item -ItemType Directory -Force -Path $content | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $out -Recurse -File) {
    $destination = Join-Path $content $file.FullName.Substring($out.Length + 1)
    if ((Test-Path -LiteralPath $destination) -and
        (Get-FileHash -LiteralPath $file.FullName).Hash -eq (Get-FileHash -LiteralPath $destination).Hash) { continue }
    New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force -ErrorAction Stop
}
