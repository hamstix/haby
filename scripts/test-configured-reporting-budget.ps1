$ErrorActionPreference='Stop'
. "$PSScriptRoot/kubernetes-qualification-diagnostics.ps1"
$env:HABY_TEST_SOURCE_ROOT=Split-Path $PSScriptRoot -Parent
$lines=@(& dotnet run --project "$PSScriptRoot/../tools/Haby.Kubernetes.Qualification" --no-build --no-restore -- --configured-reporting-controls)
if($LASTEXITCODE -ne 0){throw 'Configured source-bound reporting controls failed.'}
$sections=@{};$name=''
foreach($line in $lines){
    if($line -cmatch '^CASE (full|negative)$'){$name=$Matches[1];$sections[$name]=[Collections.Generic.List[string]]::new()}
    elseif($name){$sections[$name].Add($line)}else{throw 'Unexpected reporting-control prefix.'}
}
if($sections.Count -ne 2){throw 'Missing full caller envelope.'}
foreach($name in @('full','negative')){
    $text=$sections[$name] -join "`n"
    $parsed=Read-QualificationWorkloadEvidence configured $text ''
    $count=@($sections[$name] | Where-Object {$_ -cmatch '^(Q243 |PASS configured-)'}).Count
    $bytes=[Text.Encoding]::UTF8.GetByteCount($text)
    Write-Output "Configured envelope $name`: producer-records=$count utf8-bytes=$bytes characters=$($text.Length) parser=$($parsed.Status)"
    if($parsed.Status -cne 'valid' -or $parsed.HasFailure -ne ($name -ceq 'negative')){throw 'Whole configured caller envelope exceeds diagnostic budget or loses failure.'}
    if($parsed.Records.Count -gt 256 -or $bytes -gt 1048576){throw 'Configured envelope budget widened.'}
    if(($parsed.Records -join "`n") -match 'protected-secret'){throw 'Protected text survived.'}
    $actual=@($parsed.Records | Where-Object {$_ -cmatch '^PASS configured-[CRABU][0-9]{2}-'})
    $ledger=Get-Content "$PSScriptRoot/../tests/Haby.PostgreSql.IntegrationTests/suite-gate-map.json" -Raw | ConvertFrom-Json
    $expected=@($ledger.entries | Where-Object { $_.gate -ceq 'kubernetes-configured-deployment' -and $_.suite -ceq 'api-consumer' })
    $cases=@(foreach($pass in $actual){[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("Haby.Kubernetes.Qualification.ConfiguredDeploymentCases.Run`n"+$pass.Substring(16)))).ToLowerInvariant()})
    if($cases.Count -ne 40 -or @($cases | Select-Object -Unique).Count -ne 40 -or
        @($expected | Where-Object {$_.case -cnotin $cases}).Count -or $expected.Count -ne 40){throw 'Source caller envelope lost or duplicated mandatory case coverage.'}
    foreach($proof in @('PASS configured-owned-namespace-absent','PASS configured-owned-catalog-cleanup','Diagnostic workload: configured retirement DrainComplete')){
        if($proof -cnotin $parsed.Records){throw 'Whole configured envelope lost mandatory cleanup proof.'}
    }
    if($name -ceq 'negative'){
        foreach($proof in @('Diagnostic workload: configured retirement Closure PrerequisiteIncomplete UpdateInventoryMissing',
            'Diagnostic workload: configured retirement Recipients 1 1 1 Container Shape',
            'Diagnostic workload: configured retirement Primary Canceled','Diagnostic workload: configured retirement ReleaseCleanup Canceled',
            'Diagnostic workload: configured retirement NamespaceCleanup Canceled','Diagnostic workload: configured cli-tail Output',
            'Diagnostic workload: configured cli-tail Termination')){if($proof -cnotin $parsed.Records){throw 'Repeated success hid first failure or partial cleanup.'}}
    }
}
# Aggregate limit applies to BOTH streams together, neither independently.
foreach($streams in @(@(("Q243 configured entry start`n"*256),''),@(("Q243 configured entry start`n"*128),("Q243 configured entry start`n"*128)))){
    if((Read-QualificationWorkloadEvidence configured $streams[0] $streams[1]).Status -cne 'valid'){throw '256 record boundary changed.'}
    if((Read-QualificationWorkloadEvidence configured ($streams[0]+"Q243 configured entry start`n") $streams[1]).Status -cne 'oversized'){throw '257 combined record boundary changed.'}
}
$prefix="Q243 configured entry start`n"
$padding='x'*(1048576-$prefix.Length)
if((Read-QualificationWorkloadEvidence configured ($prefix+$padding) '').Status -cne 'valid' -or
    (Read-QualificationWorkloadEvidence configured $prefix ($padding+'x')).Status -cne 'oversized'){throw 'Combined character limit changed.'}
'PASS complete source-bound configured caller reporting envelope, ledger, partial cleanup and unchanged limits'
