param([switch]$BaselineContract,[ValidateSet('all','central','routing','hidden')][string]$Focus='all')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$launcher=[IO.File]::ReadAllText((Join-Path $root 'scripts/run-kubernetes-qualification.ps1'))
if($launcher.Contains("'images','import'") -or $launcher.Contains("'image','save'") -or $launcher.Contains("'publish','examples/Haby.RuntimeStartup.Consumer")){
    throw 'Configured qualification still builds or imports the consumer image instead of consuming a verified digest.'
}
if($BaselineContract){return}
if($Focus -cin @('all','routing')){
    $workflow=[IO.File]::ReadAllText((Join-Path $root '.github/workflows/kubernetes-deployment-kernel.yml'))
    $pathBlock=[regex]::Match($workflow,'(?s)  pull_request:\s+paths:(?<paths>.*?)permissions:')
    $paths=@([regex]::Matches($pathBlock.Groups['paths'].Value,"'([^']+)'")|ForEach-Object {$_.Groups[1].Value})
    foreach($required in @('tests/fixtures/runtime-consumer-images/**','scripts/runtime-consumer-image.ps1','scripts/publish-runtime-consumer-image.ps1','.github/workflows/runtime-consumer-image.yml','**/Directory.Build.*','**/Directory.Packages.props','.editorconfig','global.json')){
        if($required -cnotin $paths){throw ('Configured functional gate misses image input path: '+$required)}
    }
    if($workflow -cmatch '(?m)^  push:' -or $workflow -cnotmatch '(?m)^  workflow_dispatch:' -or $workflow -cnotmatch '(?m)^  pull_request:'){throw 'Configured gate trigger boundary changed.'}
    Write-Output 'PASS configured routing: binding/reader/recipe/shared inputs; PR/dispatch retained; no bootstrap push.'
    if($Focus -ceq 'routing'){return}
}
. (Join-Path $PSScriptRoot 'runtime-consumer-image.ps1')
$inputs=Get-HabyConsumerImageInputs $root
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('haby-image-controls-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $fixture|Out-Null
try{
    $copy=Join-Path $fixture 'source';New-Item -ItemType Directory $copy|Out-Null
    foreach($entry in $inputs.files.GetEnumerator()){$target=Join-Path $copy $entry.Key;New-Item -ItemType Directory -Force ([IO.Path]::GetDirectoryName($target))|Out-Null;[IO.File]::WriteAllText($target,$entry.Value,[Text.UTF8Encoding]::new($false))}
    if((Get-HabyConsumerImageInputs $copy).key -cne $inputs.key){throw 'Canonical context changed input identity.'}
    if($Focus -cin @('all','hidden')){
        $hidden=Join-Path $copy '.editorconfig';$attributes=[IO.File]::GetAttributes($hidden)
        try{
            if($IsWindows){[IO.File]::SetAttributes($hidden,($attributes -bor [IO.FileAttributes]::Hidden))}
            $observed=Get-HabyConsumerImageInputs $copy
            if($observed.key -cne $inputs.key -or !$observed.files.ContainsKey('.editorconfig') -or $observed.files['.editorconfig'] -cne $inputs.files['.editorconfig']){throw 'Hidden input changed bytes, context or identity.'}
        }finally{if($IsWindows){[IO.File]::SetAttributes($hidden,$attributes)}}
        Write-Output 'PASS hidden .editorconfig: identical bytes/context/key; fixture attribute restored.'
        if($Focus -ceq 'hidden'){return}
    }
    [IO.File]::WriteAllText((Join-Path $copy 'unrelated-doc.md'),'unrelated')
    $central=Join-Path $copy 'Directory.Packages.props';$original=[IO.File]::ReadAllText($central)
    [IO.File]::WriteAllText($central,$original.Replace('</ItemGroup>','<PackageVersion Include="Unrelated.Test.Package" Version="1.2.3" /></ItemGroup>'))
    if((Get-HabyConsumerImageInputs $copy).key -cne $inputs.key){throw 'Unrelated documentation/package changed identity.'}
    [IO.File]::WriteAllText($central,$original)
    [IO.File]::WriteAllText($central,$original.Replace('</Project>','<Target Name="Injected" BeforeTargets="CoreCompile"><Message Text="never-executed" /></Target></Project>'))
    $refused=$false;try{Get-HabyConsumerImageInputs $copy|Out-Null}catch{$refused=$_.Exception.Message -ceq 'Unsupported central package structure.'}
    if(!$refused){throw 'Unsupported top-level central Target was silently normalized away.'}
    [IO.File]::WriteAllText($central,$original)
    Write-Output 'PASS central structure: top-level Target refused; unrelated PackageVersion still reuses identity.'
    if($Focus -ceq 'central'){return}
    foreach($relative in @('src/Haby.Configuration.Client/RuntimeStartupClient.cs','src/Haby.Configuration.Contracts/RuntimeBootstrap.cs','src/Haby.Configuration.Contracts/configuration_delivery.proto')){
        $inputPath=Join-Path $copy $relative;$originalInput=[IO.File]::ReadAllText($inputPath)
        [IO.File]::AppendAllText($inputPath,"`n// changed build input`n")
        if((Get-HabyConsumerImageInputs $copy).key -ceq $inputs.key){throw 'Changed client/contract/proto reused old image identity.'}
        [IO.File]::WriteAllText($inputPath,$originalInput)
    }
    [IO.File]::WriteAllText($central,$original.Replace('Version="2.84.0"','Version="2.84.1"'))
    if((Get-HabyConsumerImageInputs $copy).key -ceq $inputs.key){throw 'Relevant package version reused old image identity.'}
    [IO.File]::WriteAllText($central,$original)
    $project=Join-Path $copy 'examples/Haby.RuntimeStartup.Consumer/Haby.RuntimeStartup.Consumer.csproj';$projectText=[IO.File]::ReadAllText($project)
    [IO.File]::WriteAllText($project,$projectText.Replace('</Project>','<Import Project="unreviewed.props" /></Project>'))
    $refused=$false;try{Get-HabyConsumerImageInputs $copy|Out-Null}catch{$refused=$true};if(!$refused){throw 'Unknown build import accepted.'}
    foreach($entry in $inputs.files.Keys){if($entry -match '(?:^\.git/|Host/|PostgreSql/|(?:^|/)(?:bin|obj)/|\.pfx$|\.key$|\.docker/)'){throw 'Private/unrelated build context input.'}}
    $binding=[ordered]@{format=1;package='ghcr.io/hamstix/haby-runtime-consumer';inputKey=$inputs.key;digest=('sha256:'+('a'*64));builtSource=('b'*40);publisherSource=('c'*40);publisherRunId=1;proofArtifactSha256=('d'*64)}
    $path=Join-Path $fixture 'binding.json';$binding|ConvertTo-Json|Set-Content -LiteralPath $path -Encoding utf8
    if((Read-HabyConsumerImageBinding $inputs $path) -cne ('ghcr.io/hamstix/haby-runtime-consumer@sha256:'+('a'*64))){throw 'Exact digest not selected.'}
    $binding.inputKey='e'*64;$binding|ConvertTo-Json|Set-Content -LiteralPath $path -Encoding utf8
    $refused=$false;try{Read-HabyConsumerImageBinding $inputs $path|Out-Null}catch{$refused=$true};if(!$refused){throw 'Stale image accepted.'}
    $states=@(
        @{state='present';prior=$true;first=$false;expected='reuse'},
        @{state='absent';prior=$false;first=$false;expected='build'},
        @{state='package-not-found';prior=$false;first=$true;expected='build'},
        @{state='package-not-found';prior=$false;first=$false;expected='stop'},
        @{state='absent';prior=$true;first=$false;expected='stop'},
        @{state='unauthorized';prior=$false;first=$true;expected='stop'},
        @{state='unavailable';prior=$false;first=$true;expected='stop'},
        @{state='inconsistent';prior=$true;first=$false;expected='stop'},
        @{state='uncertain-push';prior=$false;first=$true;expected='stop'}
    )
    foreach($case in $states){if((Get-HabyConsumerPublicationDecision $case.state $case.prior $case.first) -cne $case.expected){throw 'Publication must fail closed.'}}
    foreach($name in @('ca.crt','admin.crt','admin.key')){if(!$launcher.Contains($name)){throw 'Secure fixture credential copy removed.'}}
    Write-Output 'PASS consumer image: canonical context, relevant invalidation, unrelated reuse, unsupported import, safe context, digest/stale binding; nine publication decisions; secure fixture copies retained.'
}finally{Remove-Item -LiteralPath $fixture -Recurse -Force}
