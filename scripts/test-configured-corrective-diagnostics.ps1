$ErrorActionPreference='Stop'
. "$PSScriptRoot/kubernetes-qualification-diagnostics.ps1"
$phases=@('Setup','Preflight','InitialPlanCommit','ExecuteResource','BadSubmitWait','CorrectivePlanCommit','SubmitWait','Retirement','OidProof')
$results=@('Preflight','InitialPlan','InitialCommit','ResourceProof','BadSubmit','BadWait','CorrectivePlan','CorrectiveCommit','CorrectiveSubmit','RetirementProof','OriginalEffects','CopiedProof')
foreach($phase in $phases){
    foreach($state in @('Complete','Failed')){
        $line="Q243 configured corrective $phase $state"
        $parsed=Read-QualificationWorkloadEvidence configured ("Q243 configured entry start`n"+$line) 'protected-secret-error'
        if($parsed.Status -cne 'valid' -or $parsed.HasFailure -ne ($state -ceq 'Failed')){throw 'Fixed corrective phase classification failed.'}
        if(($parsed.Records -join "`n") -match 'protected-secret'){throw 'Protected text survived.'}
    }
}
foreach($result in $results){
    $parsed=Read-QualificationWorkloadEvidence configured ("Q243 configured entry start`nQ243 configured corrective OidProof Rejected "+$result) ''
    if($parsed.Status -cne 'valid' -or !$parsed.HasFailure){throw 'Fixed corrective result classification failed.'}
}
foreach($bad in @('Q243 configured corrective protected-secret Complete','Q243 configured corrective Setup protected-secret',
    'Q243 configured corrective Setup Rejected protected-secret','Q243 configured corrective Setup Complete extra',
    'Q243 configured corrective Setup Rejected','Q243 configured corrective Setup Failed protected-secret')){
    if((Read-QualificationWorkloadEvidence configured ("Q243 configured entry start`n"+$bad) '').Status -cne 'invalid'){throw 'Open corrective diagnostic accepted.'}
}
if((Read-QualificationWorkloadEvidence kernel "Q243 kernel entry start`nQ243 kernel corrective Setup Complete" '').Status -cne 'invalid'){throw 'Corrective evidence accepted in wrong contour.'}
$success=@($phases | ForEach-Object {"Q243 configured corrective $_ Complete"})
$worst=@($success)+@('Q243 configured corrective OidProof Rejected CopiedProof','Q243 configured corrective OidProof Failed')
if($worst.Count -ne 11 -or 241+$worst.Count -gt 256){throw 'Corrective bounded suffix exceeds prior negative envelope.'}
$prefix=("Q243 configured entry start`n"*245)
if((Read-QualificationWorkloadEvidence configured ($prefix+($worst -join "`n")) '').Status -cne 'valid'){throw '256 record boundary changed.'}
if((Read-QualificationWorkloadEvidence configured ($prefix+($worst -join "`n")+"`nQ243 configured entry start") '').Status -cne 'oversized'){throw '257 record boundary changed.'}
'PASS fixed corrective phases/results, redaction, closed parser, wrong contour and unchanged256 bound'
