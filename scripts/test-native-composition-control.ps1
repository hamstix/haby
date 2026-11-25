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
} finally {
    $target = [IO.Path]::GetFullPath($root); $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $target.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or -not ([IO.Path]::GetFileName($target)).StartsWith('haby-292-control-', [StringComparison]::Ordinal)) { throw 'Unsafe fixture cleanup target.' }
    Remove-Item -LiteralPath $target -Recurse -Force
}
Write-Host "Control block checks: $passed passed, $failed failed. No native execution credit."
if ($failed) { throw 'Native composition control checks failed.' }
