param([string]$WorkflowPath = (Join-Path $PSScriptRoot '../.github/workflows/build.yml'))
$ErrorActionPreference = 'Stop'
# Execute real inline control blocks with owned fake outputs, not native runtime credit.
$workflow = Get-Content -LiteralPath $WorkflowPath -Raw
function Read-Block([string]$Name) {
    $start = $workflow.IndexOf("      - name: $Name", [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Missing workflow step: $Name" }
    $start = $workflow.IndexOf('        run: |', $start, [StringComparison]::Ordinal)
    $end = $workflow.IndexOf('      - name:', $start + 1, [StringComparison]::Ordinal)
    if ($end -lt 0) { $end = $workflow.Length }
    $lines = $workflow.Substring($start, $end - $start) -split '\r?\n'
    return (($lines | Select-Object -Skip 1 | Where-Object { $_.StartsWith('          ', [StringComparison]::Ordinal) } | ForEach-Object { $_.Substring(10) }) -join "`n")
}
$publish = Read-Block 'Publish Native AOT executable (warnings as errors)'
$compiler = Read-Block 'Record actual patched Native AOT compiler selection'
$test = Read-Block 'Test published binary against disposable loopback hosts'
$export = (Read-Block 'Verify published-native case and parameter gate inventory').Split('$trx =')[0]
$control = Get-Content (Join-Path $PSScriptRoot '../.github/workflows/merge-qualification.yml') -Raw
if (-not $control.Contains('uses: ./.github/workflows/build.yml') -or -not $control.Contains('ref: ${{ github.workflow_sha }}')) { throw 'Installed local reusable control binding changed.' }
function Invoke-Block([string]$Body, [string]$Rid) {
    $exe = if ($Rid -ceq 'win-x64') { 'habyctl.exe' } else { 'habyctl' }
    $body = $Body.Replace('${{ env.HABY_NATIVE_RID }}', $Rid).Replace('${{ env.HABY_NATIVE_EXECUTABLE }}', $exe)
    $body = $body.Replace('$IsLinux', $(if ($Rid -ceq 'linux-x64') { '$true' } else { '$false' }))
    $body = $body.Replace('$IsWindows', $(if ($Rid -ceq 'win-x64') { '$true' } else { '$false' }))
    & ([scriptblock]::Create($body))
}
function global:git { '1111111111111111111111111111111111111111' }
function global:dotnet {
    if ($args[0] -ceq '--version') { '10.0.404'; return }
    if ($args[0] -ceq 'publish') {
        $project = [string]$args[1]; $sample = $project.Contains('Haby.Cli.Composition')
        $global:Published.Add($project)
        if ($sample -and $global:Scenario -ceq 'publish-failure') { $global:LASTEXITCODE = 17; return }
        $output = [string]$args[[Array]::IndexOf($args, '-o') + 1]
        New-Item -ItemType Directory -Force $output | Out-Null
        $name = if ($project.Contains('/src/habyctl/')) { 'habyctl' } elseif ($sample) { 'Haby.Cli.Composition' } else { 'Haby.RuntimeStartup.Consumer' }
        if ($global:TestRid -ceq 'win-x64') { $name += '.exe' }
        if (-not ($sample -and $global:Scenario -ceq 'missing-binary')) { Set-Content (Join-Path $output $name) 'owned-test-output' }
        $obj = Join-Path (Split-Path $project -Parent) 'obj'; New-Item -ItemType Directory -Force $obj | Out-Null
        $version = if ($sample -and $global:Scenario -ceq 'bad-compiler') { '9.0.0' } else { '10.0.15' }
        @{ libraries = @{ "Microsoft.DotNet.ILCompiler/$version" = @{}; "runtime.$global:TestRid.Microsoft.DotNet.ILCompiler/$version" = @{} } } |
            ConvertTo-Json -Depth 5 | Set-Content (Join-Path $obj 'project.assets.json')
    }
    $global:LASTEXITCODE = 0
}
$root = Join-Path ([IO.Path]::GetTempPath()) ('haby-292-control-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $root | Out-Null
$passed = 0; $failed = 0
try {
    foreach ($rid in @('win-x64', 'linux-x64')) {
        foreach ($scenario in @('absent', 'present', 'publish-failure', 'missing-binary', 'bad-compiler')) {
            $fixture = Join-Path $root "$rid-$scenario"; New-Item -ItemType Directory $fixture | Out-Null
            Push-Location $fixture
            try {
                $global:TestRid = $rid; $global:Scenario = $scenario; $global:Published = [Collections.Generic.List[string]]::new()
                $env:HABY_AOT_C_COMPILER = '/owned-test/clang'
                Remove-Item Env:HABY_AOT_COMPOSITION_PATH -ErrorAction SilentlyContinue
                if ($scenario -cne 'absent') {
                    New-Item -ItemType Directory -Force 'examples/Haby.Cli.Composition' | Out-Null
                    Set-Content 'examples/Haby.Cli.Composition/Haby.Cli.Composition.csproj' '<Project />'
                }
                $failure = $null
                try {
                    Invoke-Block $publish $rid
                    Invoke-Block $compiler $rid
                    Invoke-Block $test $rid
                    $first = $env:HABY_AOT_COMPOSITION_PATH
                    Remove-Item Env:HABY_AOT_COMPOSITION_PATH -ErrorAction SilentlyContinue
                    Invoke-Block $export $rid
                } catch { $failure = $_ }
                if ($scenario -cin @('publish-failure', 'missing-binary', 'bad-compiler')) {
                    if ($null -eq $failure) { throw 'Required sample failure did not fail the control.' }
                    $expected = switch ($scenario) { 'publish-failure' { 'CLI composition Native AOT publish failed' }; 'missing-binary' { 'because it does not exist' }; 'bad-compiler' { 'CLI composition compiler does not meet' } }
                    if (-not $failure.ToString().Contains($expected)) { throw "Unexpected control failure in $scenario" }
                } else {
                    if ($null -ne $failure) { throw $failure }
                    $record = Get-Content ".artifacts/native-results/$rid/compiler.json" -Raw | ConvertFrom-Json
                    if ($record.packs.Count -ne 2 -or $record.rid -cne $rid) { throw 'Legacy compiler inventory changed.' }
                    if ($scenario -ceq 'present') {
                        if ($global:Published.Count -ne 3 -or -not $first -or $first -cne $env:HABY_AOT_COMPOSITION_PATH -or -not (Test-Path -LiteralPath $first)) { throw 'Required published sample path is missing.' }
                        if ($record.cliCompositionCompiler.runtimeCompiler -cne "runtime.$rid.Microsoft.DotNet.ILCompiler/10.0.15") { throw 'Sample compiler/RID evidence is missing.' }
                    } elseif ($global:Published.Count -ne 2 -or $env:HABY_AOT_COMPOSITION_PATH -or $record.cliCompositionCompiler) { throw 'Legacy absent-sample control changed.' }
                }
                $passed++; Write-Host "PASS $rid $scenario"
            } catch { $failed++; Write-Host "FAIL $rid $scenario : $($_.Exception.Message)" }
            finally { Pop-Location }
        }
    }
    # Resolve the actual always-upload path block against owned files, not a copied allowlist.
    $upload = [regex]::Match($workflow, '(?ms)^      - name: Publish allowlisted PostgreSQL timing and case inventory\r?\n(?<body>.*?)(?=^      - name:|\z)').Groups['body'].Value
    if (-not $upload.Contains('        if: always()') -or -not $upload.Contains('        uses: actions/upload-artifact@v4')) { throw 'PostgreSQL evidence upload control changed.' }
    $pathBlock = [regex]::Match($upload, '(?m)^          path: \|\r?\n(?<paths>(?:^            [^\r\n]+\r?\n)+)').Groups['paths'].Value
    $paths = @($pathBlock -split '\r?\n' | Where-Object { $_.Trim() } | ForEach-Object { $_.Trim() })
    function Select-UploadFiles([string]$Fixture, [string[]]$Patterns) {
        @($Patterns | ForEach-Object {
            Get-ChildItem -Path (Join-Path $Fixture $_) -File -ErrorAction SilentlyContinue | ForEach-Object {
                [IO.Path]::GetRelativePath($Fixture, $_.FullName).Replace('\', '/')
            }
        } | Sort-Object -Unique)
    }
    $prefix = '.artifacts/postgresql-measurement/safe/functional/'
    $id = '00000000000000000000000000000001'
    $identityName = "${prefix}composition-$id.identity.json"
    $exceptionName = "${prefix}composition-$id.exceptions.jsonl"
    $legacy = @('.artifacts/postgresql-measurement/safe/counts.json', "${prefix}cases.json")
    $present = Join-Path $root 'retention-present'
    $identity = @{ Source = ('a' * 40); SampleSha256 = ('b' * 64); PersistenceSha256 = ('c' * 64); ApplicationSha256 = ('d' * 64); TestSource = ('a' * 40); TestSha256 = ('e' * 64) }
    $exception = @{ Identity = $identity; Stage = 3; Type = 'System.InvalidOperationException'; Frames = @('Haby.TestDiagnostics.CompositionExceptionDiagnostics.Record') }
    $fixtures = @{ $identityName = ($identity | ConvertTo-Json -Compress); $exceptionName = ($exception | ConvertTo-Json -Depth 4 -Compress) }
    foreach ($name in $legacy) { $fixtures[$name] = '{}' }
    $excluded = @('.artifacts/postgresql-measurement/private/raw-stderr.log',
        ".artifacts/postgresql-measurement/private/composition-$id.identity.json",
        "${prefix}nested/composition-$id.exceptions.jsonl", "${prefix}composition-$id.raw-stderr.log",
        "${prefix}composition-$id.identity.json.private", "${prefix}not-composition-$id.identity.json",
        ".artifacts/postgresql-measurement/safe/infrastructure-qualification/composition-$id.exceptions.jsonl")
    foreach ($name in $excluded) { $fixtures[$name] = 'protected-control-fixture-marker' }
    foreach ($name in $fixtures.Keys) {
        $file = Join-Path $present $name; New-Item -ItemType Directory -Force (Split-Path $file -Parent) | Out-Null
        Set-Content -LiteralPath $file -Value $fixtures[$name] -Encoding utf8NoBOM
    }
    $selected = @(Select-UploadFiles $present $paths)
    if (Compare-Object -CaseSensitive @($legacy + $identityName + $exceptionName) $selected) { throw 'Real upload block did not retain exactly the bounded composition evidence and legacy files.' }
    foreach ($name in $selected) {
        if ((Get-Content -LiteralPath (Join-Path $present $name) -Raw).Contains('protected-control-fixture-marker')) { throw 'Excluded protected fixture content entered the upload selection.' }
    }
    $approved = @("${prefix}composition-*.identity.json", "${prefix}composition-*.exceptions.jsonl")
    if (Compare-Object -CaseSensitive $approved @($paths | Where-Object { $_.Contains('*') })) { throw 'Upload wildcard scope is not exactly the two approved functional evidence patterns.' }
    foreach ($name in @($identityName, $exceptionName)) {
        if ([IO.Path]::GetFileName($name) -cnotmatch '^composition-[a-f0-9]{32}\.(identity\.json|exceptions\.jsonl)$') { throw 'Fixture diagnostic file name is unsafe.' }
        $text = Get-Content -LiteralPath (Join-Path $present $name) -Raw
        if ($text.Length -gt 10000 -or $text -match 'Message|ToString|raw-stderr|private|credential') { throw 'Fixture evidence content exceeded the safe contract.' }
    }
    $record = Get-Content -LiteralPath (Join-Path $present $exceptionName) -Raw | ConvertFrom-Json
    if ($record.Frames.Count -gt 24 -or $record.Type.Length -gt 256 -or @($record.Frames | Where-Object { $_.Length -gt 256 -or $_ -match '[/\\:]' }).Count) { throw 'Fixture type/frame bounds are unsafe.' }
    $passed++; Write-Host 'PASS actual upload block includes bounded identity/sentinel files and excludes private, raw and nested decoys'
    $absent = Join-Path $root 'retention-absent'
    foreach ($name in $legacy) { $file = Join-Path $absent $name; New-Item -ItemType Directory -Force (Split-Path $file -Parent) | Out-Null; Set-Content -LiteralPath $file '{}' }
    if (Compare-Object -CaseSensitive $legacy @(Select-UploadFiles $absent $paths)) { throw 'Legacy absent-evidence artifact selection changed.' }
    $passed++; Write-Host 'PASS legacy absence preserves the original artifact selection'
} finally {
    $target = [IO.Path]::GetFullPath($root); $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $target.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or -not ([IO.Path]::GetFileName($target)).StartsWith('haby-292-control-', [StringComparison]::Ordinal)) { throw 'Unsafe fixture cleanup target.' }
    Remove-Item -LiteralPath $target -Recurse -Force
}
Write-Host "Control block checks: $passed passed, $failed failed. No native execution credit."
if ($failed) { throw 'Native composition control checks failed.' }
