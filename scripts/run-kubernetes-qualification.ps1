param(
    [ValidateSet('wslc', 'docker')][string]$Runtime = 'wslc',
    [string]$EvidencePath = '.artifacts/kubernetes-qualification.txt'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$run = 'haby179-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
$private = Join-Path ([IO.Path]::GetTempPath()) $run
$image = 'rancher/k3s@sha256:10464930d9bad0c06aef9830e84cd4019c24ed44d5eab594efb7416119097248'
$created = $false
function Invoke-Runtime([string[]]$Arguments) {
    $result = & $Runtime @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Runtime command failed: $($Arguments[0]) (exit $LASTEXITCODE)" }
    return $result
}
New-Item -ItemType Directory -Path $private | Out-Null
if ($IsWindows) {
    & icacls $private /inheritance:r /grant:r "$($env:USERNAME):(OI)(CI)F" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Private directory ACL setup failed.' }
} else {
    & chmod 700 $private
    if ($LASTEXITCODE -ne 0) { throw 'Private directory permissions failed.' }
}
New-Item -ItemType Directory -Force (Split-Path ([IO.Path]::GetFullPath($EvidencePath))) | Out-Null
$record = [Collections.Generic.List[string]]::new()
$record.Add('Source: ' + (& git rev-parse HEAD))
$record.Add('Dirty: ' + [bool](& git status --porcelain))
$record.Add('Runtime: ' + $Runtime)
$record.Add('Image: ' + $image)
try {
    Invoke-Runtime @('run', '-d', '--name', $run, '--label', "haby.qualification=$run", '-p', '127.0.0.1:16443:6443', $image, 'server', '--disable-agent', '--disable', 'traefik', '--disable', 'servicelb', '--disable', 'metrics-server', '--disable', 'coredns', '--disable', 'local-storage', '--egress-selector-mode', 'disabled', '--tls-san', '127.0.0.1', '--write-kubeconfig-mode', '600') | Out-Null
    $created = $true
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $null = & $Runtime exec $run /bin/kubectl --kubeconfig /etc/rancher/k3s/k3s.yaml get --raw=/readyz 2>&1
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 2
    }
    if (!$ready) { throw 'API readiness deadline exceeded.' }
    foreach ($pair in @(@('server-ca.crt', 'ca.crt'), @('client-admin.crt', 'admin.crt'), @('client-admin.key', 'admin.key'))) {
        Invoke-Runtime @('cp', "${run}:/var/lib/rancher/k3s/server/tls/$($pair[0])", (Join-Path $private $pair[1])) | Out-Null
    }
    $output = & dotnet run --project tools/Haby.Kubernetes.Qualification --no-restore -- https://127.0.0.1:16443 $private 2>&1
    $exit = $LASTEXITCODE
    foreach ($line in $output) { $record.Add([string]$line) }
    if ($exit -ne 0) { throw "Harness failed (exit $exit)." }
    $record.Add('Harness: PASS')
} catch {
    $record.Add('Run: FAIL; ' + $_.Exception.Message)
    throw
} finally {
    $record | Set-Content -LiteralPath $EvidencePath
    if ($created) {
        $inspection = (Invoke-Runtime @('inspect', $run) -join "`n") | ConvertFrom-Json
        $label = $inspection[0].Config.Labels.'haby.qualification'
        if (($label -join '').Trim() -ne $run) { throw 'Container ownership mismatch; teardown refused.' }
        Invoke-Runtime @('stop', $run) | Out-Null
        Invoke-Runtime @('remove', $run) | Out-Null
        $record.Add('Run-owned container removed.')
    }
    $resolved = [IO.Path]::GetFullPath($private)
    if ((Split-Path $resolved -Leaf) -ne $run -or !(Test-Path -LiteralPath $resolved)) { throw 'Private directory cleanup guard failed.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
    $record.Add('Run-owned private directory removed.')
    $record | Set-Content -LiteralPath $EvidencePath
    $record | ForEach-Object { Write-Output $_ }
}
