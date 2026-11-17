param([string]$StatePath,[string]$OutputDirectory='.artifacts/runtime-consumer',[switch]$Prepare)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'runtime-consumer-image.ps1')
$root=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $root
if(& git status --porcelain){throw 'Publisher requires a clean exact checkout.'}
$source=(& git rev-parse HEAD).Trim()
$inputs=Get-HabyConsumerImageInputs $root
New-Item -ItemType Directory -Force $OutputDirectory|Out-Null
$bindingPath=Join-Path $root ('tests/fixtures/runtime-consumer-images/'+$inputs.key+'.json')
$accepted=if(Test-Path -LiteralPath $bindingPath){Read-HabyConsumerImageBinding $inputs $bindingPath}else{$null}
if($Prepare){
    [ordered]@{key=$inputs.key;acceptedImage=$accepted;manifest=($inputs.manifest|ConvertFrom-Json)}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $OutputDirectory 'inputs.json') -Encoding utf8
    if($env:GITHUB_OUTPUT){'key='+$inputs.key|Add-Content -LiteralPath $env:GITHUB_OUTPUT}
    return
}
if(!$IsLinux -or $env:GITHUB_REPOSITORY -cne 'hamstix/haby' -or $env:GITHUB_EVENT_NAME -cne 'push' -or
    $env:GITHUB_REF -cnotin @('refs/heads/main','refs/heads/codex/ci-223-ghcr-image','refs/heads/codex/ci-209-runtime-consumer','refs/heads/codex/feat-245-deployment-ports-probes') -or $env:GITHUB_SHA -cne $source){throw 'Only the exact trusted publisher push may publish.'}
$state=Get-Content -LiteralPath $StatePath -Raw|ConvertFrom-Json
if($state.key -cne $inputs.key -or $state.authenticated -isnot [bool] -or !$state.authenticated -or $state.firstPackageConfirmed -isnot [bool]){throw 'Authenticated registry observation required.'}
$decision=Get-HabyConsumerPublicationDecision $state.state ([bool]$accepted) ([bool]$state.firstPackageConfirmed)
if($decision -ceq 'stop'){throw 'Publication stopped: absence/proof is ambiguous or inconsistent.'}
if($decision -ceq 'reuse'){
    if($accepted -cne ($HabyConsumerPackage+'@'+$state.digest)){throw 'Prior binding/registry inconsistency; never rebuild.'}
    [ordered]@{format=1;package=$HabyConsumerPackage;inputKey=$inputs.key;image=$accepted;status='reused-reviewed-binding';testedSource=$source}|ConvertTo-Json|Set-Content (Join-Path $OutputDirectory 'publication.json') -Encoding utf8
    Write-Output 'Existing reviewed consumer digest reused; no build/push.';return
}
$context=Join-Path ([IO.Path]::GetTempPath()) ('haby-image-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $context|Out-Null
try{
    foreach($entry in $inputs.files.GetEnumerator()){$path=Join-Path $context $entry.Key;New-Item -ItemType Directory -Force ([IO.Path]::GetDirectoryName($path))|Out-Null;[IO.File]::WriteAllText($path,$entry.Value,[Text.UTF8Encoding]::new($false))}
    $metadata=Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'build-metadata.json'
    # ONE build/push. Any error or uncertain response stops; no automatic retry.
    & docker buildx build --platform linux/amd64 --file (Join-Path $context 'examples/Haby.RuntimeStartup.Consumer/Dockerfile') --tag ($HabyConsumerPackage+':inputs-'+$inputs.key) --label ('dev.haby.input-key='+$inputs.key) --label ('org.opencontainers.image.revision='+$source) --push --metadata-file $metadata $context
    if($LASTEXITCODE -ne 0){throw 'Consumer push failed or uncertain; reconcile read-only, never retry automatically.'}
    $digest=(Get-Content -LiteralPath $metadata -Raw|ConvertFrom-Json).'containerimage.digest'
    if($digest -cnotmatch '^sha256:[a-f0-9]{64}$'){throw 'Registry digest unavailable after push; no retry.'}
    [ordered]@{format=1;package=$HabyConsumerPackage;inputKey=$inputs.key;digest=$digest;builtSource=$source;publisherSource=$env:GITHUB_SHA;publisherRunId=[long]$env:GITHUB_RUN_ID;platform='linux/amd64';manifest=($inputs.manifest|ConvertFrom-Json)}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $OutputDirectory 'publication.json') -Encoding utf8
}finally{
    if([IO.Path]::GetDirectoryName($context) -cne [IO.Path]::GetTempPath().TrimEnd([IO.Path]::DirectorySeparatorChar) -or (Split-Path $context -Leaf) -cnotmatch '^haby-image-[a-f0-9]{32}$'){throw 'Build context cleanup ownership mismatch.'}
    Remove-Item -LiteralPath $context -Recurse -Force
}
