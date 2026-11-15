# Fixed consumer graph only. NuGet resolves dependencies; committed SDK locks are inputs.
$HabyConsumerPackage='ghcr.io/hamstix/haby-runtime-consumer'
function Get-HabyConsumerImageInputs([string]$Root){
    $Root=[IO.Path]::GetFullPath($Root)
    $projects=[ordered]@{
        'examples/Haby.RuntimeStartup.Consumer'=@('../../src/Haby.Configuration.Client/Haby.Configuration.Client.csproj')
        'src/Haby.Configuration.Client'=@('../Haby.Configuration.Contracts/Haby.Configuration.Contracts.csproj')
        'src/Haby.Configuration.Contracts'=@()
    }
    $packages=@('Google.Protobuf','Grpc.Core.Api','Grpc.Net.Client','Grpc.Tools')
    $files=[Collections.Generic.SortedDictionary[string,string]]::new([StringComparer]::Ordinal)
    function Add-Input([string]$Path){
        $full=Join-Path $Root $Path
        if(!(Test-Path -LiteralPath $full -PathType Leaf) -or ((Get-Item -LiteralPath $full).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Consumer input missing or linked.'}
        $files[$Path]=([IO.File]::ReadAllText($full) -replace '\r\n',"`n")
    }
    foreach($parent in @('','src','examples','src/Haby.Configuration.Client','src/Haby.Configuration.Contracts','examples/Haby.RuntimeStartup.Consumer')){
        foreach($name in @('Directory.Build.props','Directory.Build.targets','Directory.Packages.props')){
            $relative=if($parent){$parent+'/'+$name}else{$name}
            if((Test-Path -LiteralPath (Join-Path $Root $relative)) -and $relative -cnotin @('Directory.Build.props','Directory.Packages.props')){throw 'Unsupported consumer build import; review fixed graph.'}
        }
        if($parent -and (Test-Path -LiteralPath (Join-Path $Root ($parent+'/.editorconfig')))){throw 'Unsupported nested analyzer configuration.'}
        foreach($unsupported in @('.globalconfig','Directory.Build.rsp','MSBuild.rsp')){if(Test-Path -LiteralPath (Join-Path $Root (($parent+'/').TrimStart('/')+$unsupported))){throw 'Unsupported implicit build configuration.'}}
    }
    [xml]$shared=[IO.File]::ReadAllText((Join-Path $Root 'Directory.Build.props'))
    if($shared.SelectNodes('//Import|//*[@Condition]|//ItemGroup').Count){throw 'Unsupported shared build configuration.'}
    foreach($property in $shared.Project.PropertyGroup.ChildNodes){if($property.Name -cnotin @('TargetFramework','ImplicitUsings','Nullable','AnalysisLevel','EnableNETAnalyzers')){throw 'Unsupported shared build property.'}}
    foreach($directory in $projects.Keys){
        $project=($directory -split '/')[-1]+'.csproj';$relative=$directory+'/'+$project
        Add-Input $relative
        [xml]$xml=$files[$relative]
        if($xml.Project.Sdk -cne 'Microsoft.NET.Sdk' -or $xml.SelectNodes('//Import|//*[@Condition]').Count){throw 'Unsupported consumer project configuration.'}
        $references=@($xml.SelectNodes('//ProjectReference')|ForEach-Object {$_.Include})
        if(($references -join '|') -cne ($projects[$directory] -join '|')){throw 'Consumer project graph changed; review closure.'}
        foreach($item in $xml.SelectNodes('//ItemGroup/*')){if($item.Name -cnotin @('ProjectReference','PackageReference','Protobuf')){throw 'Unsupported consumer build item.'}}
        foreach($property in $xml.SelectNodes('//PropertyGroup/*')){if($property.Name -cnotin @('OutputType','IsPackable','RestorePackagesWithLockFile')){throw 'Unsupported consumer project property.'}}
        foreach($item in $xml.SelectNodes('//PackageReference')){if($item.Include -cnotin $packages -or $item.Version){throw 'Unsupported consumer package reference.'}}
        foreach($proto in $xml.SelectNodes('//Protobuf')){if($proto.Include -cne 'configuration_delivery.proto' -or $proto.GrpcServices -cne 'Both'){throw 'Unsupported protobuf input.'}}
        $children=Get-ChildItem -LiteralPath (Join-Path $Root $directory) -File -Recurse -Force|Where-Object {$_.FullName -notmatch '[\\/](?:bin|obj)[\\/]'}
        foreach($child in $children){
            $relativeChild=[IO.Path]::GetRelativePath($Root,$child.FullName).Replace('\','/')
            if($child.Extension -cnotin @('.cs','.proto','.md') -and $relativeChild -cnotin @($relative,($directory+'/packages.lock.json'),'examples/Haby.RuntimeStartup.Consumer/Dockerfile','examples/Haby.RuntimeStartup.Consumer/NuGet.Config')){throw 'New consumer file requires build-input review.'}
            if($child.Extension -cin @('.props','.targets','.resx','.razor','.fs','.vb')){throw 'Unsupported implicit consumer input.'}
            if($child.Name -ceq '.editorconfig'){throw 'Unsupported nested analyzer configuration.'}
            if($child.Extension -ceq '.proto' -and [IO.File]::ReadAllText($child.FullName) -match '(?m)^\s*import\s'){throw 'New protobuf import requires closure review.'}
            if($child.Extension -cin @('.cs','.proto')){Add-Input ([IO.Path]::GetRelativePath($Root,$child.FullName).Replace('\','/'))}
        }
        Add-Input ($directory+'/packages.lock.json')
        $lock=$files[$directory+'/packages.lock.json']|ConvertFrom-Json
        if($lock.version -ne 2 -or !$lock.dependencies){throw 'Current SDK dependency lock required.'}
    }
    [xml]$central=[IO.File]::ReadAllText((Join-Path $Root 'Directory.Packages.props'))
    if($central.DocumentType -or $central.DocumentElement.Name -cne 'Project' -or $central.DocumentElement.NamespaceURI -or $central.DocumentElement.Attributes.Count -or
        $central.SelectNodes('/Project/*[not(self::PropertyGroup or self::ItemGroup)]').Count -or
        $central.SelectNodes('/Project/PropertyGroup/ManagePackageVersionsCentrally').Count -ne 1){throw 'Unsupported central package structure.'}
    foreach($group in $central.SelectNodes('/Project/PropertyGroup|/Project/ItemGroup')){if($group.Attributes.Count){throw 'Unsupported central package structure.'}}
    foreach($property in $central.SelectNodes('/Project/PropertyGroup/*')){if($property.Attributes.Count -or $property.SelectNodes('*').Count){throw 'Unsupported central package structure.'}}
    foreach($item in $central.SelectNodes('/Project/ItemGroup/*')){
        if($item.Name -cne 'PackageVersion' -or $item.Attributes.Count -ne 2 -or !$item.HasAttribute('Include') -or !$item.HasAttribute('Version') -or $item.HasChildNodes){throw 'Unsupported central package structure.'}
    }
    if($central.SelectNodes('//Import|//*[@Condition]').Count -or $central.Project.PropertyGroup.ManagePackageVersionsCentrally -cne 'true'){throw 'Unsupported central package configuration.'}
    foreach($property in $central.SelectNodes('//PropertyGroup/*')){if($property.Name -cne 'ManagePackageVersionsCentrally'){throw 'Unsupported central package property.'}}
    foreach($item in $central.SelectNodes('//ItemGroup/*')){if($item.Name -cne 'PackageVersion'){throw 'Unsupported central package item.'}}
    $effective='<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup>'
    foreach($name in $packages){$versions=@($central.SelectNodes('//PackageVersion')|Where-Object Include -CEQ $name);if($versions.Count -ne 1 -or $versions[0].Version -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$'){throw 'One exact consumer package version required.'};$effective+='<PackageVersion Include="'+$name+'" Version="'+$versions[0].Version+'" />'}
    $files['Directory.Packages.props']=$effective+'</ItemGroup></Project>'
    [xml]$nuget=[IO.File]::ReadAllText((Join-Path $Root 'examples/Haby.RuntimeStartup.Consumer/NuGet.Config'))
    $sources=@($nuget.configuration.packageSources.add)
    if($nuget.SelectNodes('/configuration/*').Count -ne 1 -or $nuget.SelectNodes('/configuration/packageSources/clear').Count -ne 1 -or $nuget.SelectNodes('/configuration/packageSources/*').Count -ne 2 -or
        $sources.Count -ne 1 -or $sources[0].key -cne 'nuget.org' -or $sources[0].value -cne 'https://api.nuget.org/v3/index.json'){throw 'Only the explicit public consumer package source is supported.'}
    foreach($path in @('Directory.Build.props','.editorconfig','global.json','examples/Haby.RuntimeStartup.Consumer/Dockerfile','examples/Haby.RuntimeStartup.Consumer/NuGet.Config','scripts/runtime-consumer-image.ps1','scripts/publish-runtime-consumer-image.ps1','.github/workflows/runtime-consumer-image.yml')){Add-Input $path}
    $manifest=@(foreach($entry in $files.GetEnumerator()){[ordered]@{path=$entry.Key;sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($entry.Value))).ToLowerInvariant()}})
    $json=ConvertTo-Json -InputObject ([ordered]@{format=1;platform='linux/amd64';files=$manifest}) -Depth 5 -Compress
    $key=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($json))).ToLowerInvariant()
    return [pscustomobject]@{key=$key;manifest=$json;files=$files}
}
function Read-HabyConsumerImageBinding($Inputs,[string]$Path){
    if(!(Test-Path -LiteralPath $Path -PathType Leaf)){throw 'Verified consumer image binding missing; publish separately.'}
    $binding=Get-Content -LiteralPath $Path -Raw|ConvertFrom-Json
    $fields=@('format','package','inputKey','digest','builtSource','publisherSource','publisherRunId','proofArtifactSha256')
    if(@($binding.PSObject.Properties).Count -ne $fields.Count -or @($binding.PSObject.Properties.Name|Where-Object {$_ -cnotin $fields}).Count){throw 'Closed reviewed image binding required.'}
    if($binding.format -ne 1 -or $binding.package -cne $HabyConsumerPackage -or $binding.inputKey -cne $Inputs.key -or
        $binding.digest -cnotmatch '^sha256:[a-f0-9]{64}$' -or $binding.builtSource -cnotmatch '^[a-f0-9]{40}$' -or
        $binding.publisherSource -cnotmatch '^[a-f0-9]{40}$' -or $binding.publisherRunId -isnot [long] -and $binding.publisherRunId -isnot [int] -or
        $binding.publisherRunId -le 0 -or $binding.proofArtifactSha256 -cnotmatch '^[a-f0-9]{64}$'){throw 'Consumer image binding mismatch or incomplete reviewed proof.'}
    return $HabyConsumerPackage+'@'+$binding.digest
}
function Get-HabyConsumerPublicationDecision([string]$State,[bool]$PriorAccepted,[bool]$FirstPackageConfirmed){
    if($State -ceq 'present' -and $PriorAccepted){return 'reuse'}
    if(!$PriorAccepted -and ($State -ceq 'absent' -or ($State -ceq 'package-not-found' -and $FirstPackageConfirmed))){return 'build'}
    return 'stop'
}
