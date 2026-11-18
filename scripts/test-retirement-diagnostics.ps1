param([string]$MemberTrxPath)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/kubernetes-qualification-diagnostics.ps1"
$records = @(
 'Q243 configured entry start',
 'Q243 configured retirement WaitStart',
 'Q243 configured retirement WaitMembers 1 1 0 false Unknown',
 'Q243 configured retirement Member 0 Pending status-invalid Active true false Unknown',
 'Q243 configured retirement WaitMembers 1 1 1 false Unknown',
 'Q243 configured retirement Member 0 Pending rollout-pending Expired true false Unknown',
 'Q243 configured retirement Member 0 Refused attempt-superseded Active true false Unknown',
 'Q243 configured retirement Member 0 Refused runtime-authority-unavailable Active true false Unknown',
 'Q243 configured retirement Member 0 Unavailable api-access-denied Active true false Unknown',
 'Q243 configured retirement Member 0 Unavailable observation-unavailable Active true true Unknown',
 'Q243 configured retirement WaitMembers 0 0 0 true Unknown',
 'Q243 configured retirement MembersOverflow',
 'Q243 configured retirement HeldReadStart',
 'Q243 configured retirement HeldReadReturned',
 'Q243 configured retirement HeldReadThrew Api',
 'Q243 configured retirement Condition U09Ready true',
 'Q243 configured retirement Condition U09NotApplied true',
 'Q243 configured retirement Condition U09OldRecipients true',
 'Q243 configured retirement Condition U09HeldUid true',
 'Q243 configured retirement Condition U09Terminating false',
 'Q243 configured retirement RecipientWaitExpired',
 'Q243 configured retirement DrainStart',
 'Q243 configured retirement DrainRemaining 2',
 'Q243 configured retirement DrainExhausted',
 'Q243 configured retirement DrainOwnershipRefused',
 'Q243 configured retirement DrainCleanup Canceled',
 'Q243 configured retirement DrainComplete',
 'Q243 configured retirement Recipients 2 1 0 Container None',
 'Q243 configured retirement Closure CatalogHash Unknown',
 'Q243 configured retirement Closure PrerequisiteIncomplete RetirementProgressChanged',
 'Q243 configured retirement WaitReturned true false false prerequisite-incomplete Complete',
 'Q243 configured retirement Condition U11Applied false',
 'Q243 configured retirement Primary Condition',
 'Q243 configured retirement OwnedFinalizer false',
 'Q243 configured retirement NamespaceCleanup Canceled')
$result = Read-QualificationWorkloadEvidence configured ($records -join "`n") ''
if ($result.Status -cne 'valid' -or -not $result.HasFailure) { throw 'Closed retirement records were not retained.' }
foreach ($bad in @('Q243 configured retirement Closure protected protected',
 'Q243 configured retirement WaitMembers 17 1 0 false Unknown',
 'Q243 configured retirement WaitMembers 0 1 0 false Unknown',
 'Q243 configured retirement Member 16 Pending rollout-pending Active true false Unknown',
 'Q243 configured retirement Member 0 Pending protected Active true false Unknown',
 'Q243 configured retirement Member 0 Pending rollout-pending Active true false true',
 'Q243 configured retirement DrainRemaining 65',
 'Q243 configured retirement DrainRemaining protected',
 'Q243 configured retirement Recipients 16385 1 0 None None',
 'Q243 configured retirement WaitReturned true false false protected Complete',
 'Q243 configured retirement Condition U11Applied protected')) {
 $invalid = Read-QualificationWorkloadEvidence configured ((@('Q243 configured entry start',$bad)) -join "`n") ''
 if ($invalid.Status -ceq 'valid') { throw 'Unallowlisted retirement value escaped.' }
}
'PASS closed retirement diagnostics and hostile-value refusal'

if ($MemberTrxPath) {
 [xml]$trx = Get-Content -LiteralPath $MemberTrxPath -Raw
 $actual = @($trx.SelectNodes('//*[local-name()="StdOut"]') | ForEach-Object { $_.InnerText -split "`r?`n" } |
     Where-Object { $_ -cmatch '^Q243 configured retirement (WaitReturned |WaitMembers |Member |MembersOverflow$)' })
 if ($actual.Count -lt 18) { throw 'Actual formatter member records missing.' }
 $roundtrip = Read-QualificationWorkloadEvidence configured ((@('Q243 configured entry start') + $actual) -join "`n") ''
 if ($roundtrip.Status -cne 'valid' -or $roundtrip.Records.Count -ne $actual.Count + 1) { throw 'Actual formatter records lost at sanitizer boundary.' }
 foreach ($line in $actual) {
  if (('Diagnostic workload: ' + $line.Substring(5)) -cnotin $roundtrip.Records) { throw 'Actual formatter member record changed.' }
 }
 foreach ($required in @(' Pending status-invalid ', ' Pending rollout-pending Expired ', ' Refused attempt-superseded ',
     ' Refused runtime-authority-unavailable ', ' Unavailable api-access-denied ', ' Member 0 Ready ready ', ' WaitMembers 0 0 0 ')) {
  if (-not ($actual | Where-Object { $_.Contains($required, [StringComparison]::Ordinal) })) { throw 'Actual member branch missing from roundtrip.' }
 }
 'PASS actual formatter-to-sanitizer complete member roundtrip'
}
