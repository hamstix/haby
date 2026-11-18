$ErrorActionPreference='Stop'
. "$PSScriptRoot/kubernetes-qualification-diagnostics.ps1"
foreach($phase in @('Overall','Functional','Delivery','Drain','NamespaceCleanup','ReleaseCleanup','NativeCli')){
    foreach($state in @('Start','Complete','Failed')){
        $record="Q243 configured budget $phase $state 30000 15000 70000 Phase"
        $parsed=Read-QualificationWorkloadEvidence configured ("Q243 configured entry start`n"+$record) 'protected-unknown-output'
        if($parsed.Status -cne 'valid' -or $parsed.HasFailure -ne ($state -ceq 'Failed') -or
            ($parsed.Records -join "`n") -match 'protected-'){throw 'Configured budget reporting lost finite state or leaked output.'}
    }
}
foreach($record in @('Q243 configured budget Drain Failed -1 0 0 Phase',
    'Q243 configured budget Drain Failed 3600001 0 0 Phase',
    'Q243 configured budget Drain Failed 1 240001 0 Phase',
    'Q243 configured budget Drain Failed 1 0 0 protected-secret',
    'Q243 configured budget protected-secret Failed 1 0 0 Phase')){
    if((Read-QualificationWorkloadEvidence configured ("Q243 configured entry start`n"+$record) '').Status -cne 'invalid'){throw 'Invalid budget record accepted.'}
}
if((Read-QualificationWorkloadEvidence kernel "Q243 kernel entry start`nQ243 kernel budget Drain Start 0 45000 240000 None" '').Status -cne 'invalid'){throw 'Configured budget accepted in another contour.'}
foreach($reason in @('Output','Termination')){
    $parsed=Read-QualificationWorkloadEvidence configured ("Q243 configured entry start`nQ243 configured cli-tail "+$reason) ''
    if($parsed.Status -cne 'valid' -or !$parsed.HasFailure){throw 'CLI tail failure was not separate from primary deadline.'}
}
if((Read-QualificationWorkloadEvidence configured "Q243 configured entry start`nQ243 configured cli-tail protected-secret" '').Status -cne 'invalid'){throw 'Open CLI tail category accepted.'}
'PASS configured lifecycle finite phase/remaining/cancellation reporting and redaction'
