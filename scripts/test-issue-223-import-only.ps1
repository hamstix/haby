$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'issue-223-import-only.ps1') -ControlsOnly
$sentinel='SENTINEL_223_NEVER_PUBLISH'
$cases=@(
    @{input='ctr: failed to resolve reference "localhost/private:tag": content digest sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef not found';required=@('failed to resolve reference','content digest','not found');forbidden=@('localhost','private:tag','0123456789abcdef')},
    @{input="ctr: failed to open /tmp/$sentinel/consumer.tar: permission denied";required=@('failed to open','permission denied');forbidden=@($sentinel,'/tmp','consumer.tar')},
    @{input="rpc error: code = InvalidArgument desc = unsupported media type $sentinel";required=@('rpc error code invalidargument desc unsupported media type');forbidden=@($sentinel)},
    @{input="password=not found $sentinel`nctr: invalid archive format";required=@('invalid archive format');forbidden=@($sentinel,'password','not found')},
    @{input="authorization: Bearer $sentinel`nctr: failed to connect https://$sentinel`:16443/path?value=abc";required=@('ctr failed to connect');forbidden=@($sentinel,'Bearer','https','16443','abc')},
    @{input="ctr: failed to connect 10.20.30.40:12345 $sentinel@example.invalid user@$sentinel [fe80::abcd:1234]";required=@('failed to connect');forbidden=@($sentinel,'10.20','12345','example','fe80','abcd')},
    @{input="::error::$sentinel`n$([char]27)[31m$sentinel`nctr: unrecognized image format";required=@('unrecognized image format');forbidden=@($sentinel,'::error::',[string][char]27)},
    @{input="private_key=$sentinel`ncredential=$sentinel`napi-key=$sentinel`ntoken=$sentinel`nctr: no space left on device";required=@('no space left on device');forbidden=@($sentinel,'private_key','credential','api-key','token')},
    @{input=('failed '*1500)+$sentinel;required=@('failed');forbidden=@($sentinel);truncated=$true},
    @{input='ctr: archive checksum mismatch unexpected eof';required=@('archive checksum mismatch unexpected eof');forbidden=@($sentinel)},
    @{input="password=failed to connect`nctr: unable to extract archive $sentinel";required=@('unable to extract archive');forbidden=@('failed to connect',$sentinel)},
    @{input="ctr: failed to open C:\private\$sentinel\archive.tar`nctr: invalid input $sentinel$sentinel";required=@('failed to open','invalid input');forbidden=@($sentinel,'private','archive.tar')}
)
foreach($case in $cases){
    $safe=ConvertTo-ImportExcerpt $case.input
    foreach($required in $case.required){if(!$safe.text.Contains($required,[StringComparison]::Ordinal)){throw 'Fixed error text was lost.'}}
    foreach($forbidden in $case.forbidden){if($safe.text.Contains($forbidden,[StringComparison]::OrdinalIgnoreCase)){throw 'Synthetic private sentinel escaped.'}}
    if($safe.text.Length -gt 4096 -or $safe.inputCharactersRetained -gt 8192){throw 'Excerpt bound exceeded.'}
    if($case.truncated -and !$safe.inputTruncated){throw 'Input truncation was not reported.'}
}
$empty=ConvertTo-ImportExcerpt $null
if($empty.text -cne '' -or $empty.inputTruncated){throw 'Empty stream was misreported.'}
Write-Output 'Import-only synthetic excerpt controls passed: 13; no runtime execution.'
