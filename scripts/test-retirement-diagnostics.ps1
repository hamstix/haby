$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/kubernetes-qualification-diagnostics.ps1"
$records = @(
 'Q243 configured entry start',
 'Q243 configured retirement WaitStart',
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
 'Q243 configured retirement Recipients 16385 1 0 None None',
 'Q243 configured retirement WaitReturned true false false protected Complete',
 'Q243 configured retirement Condition U11Applied protected')) {
 $invalid = Read-QualificationWorkloadEvidence configured ((@('Q243 configured entry start',$bad)) -join "`n") ''
 if ($invalid.Status -ceq 'valid') { throw 'Unallowlisted retirement value escaped.' }
}
'PASS closed retirement diagnostics and hostile-value refusal'
