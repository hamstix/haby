import {nightly} from './nightly-postgresql.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync,writeFileSync,mkdtempSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {qualificationFixture} from './qualification-test-fixture.mjs';
import {validateEvidence,validatePostgreSqlEvidence} from './final-qualification.mjs';
import {publish,prepare} from './merge-qualification.mjs';
const sha=c=>c.repeat(40);
const identity={repository:'example/project',control:sha('b'),pr:17,head:sha('a'),base:sha('b'),merge:sha('c'),tree:sha('d'),purpose:'merge',scope:'full',classification:{before:sha('b'),after:sha('c'),tree:sha('d'),route:'full',changedEntries:1,documentation:'passed'}};
function focused() {
 const f=qualificationFixture(identity); const content=f.artifacts.get(f.names.postgresql);
 const rows=f.plan.entries.filter(row=>row.gate==='postgresql-integration' && row.suite==='functional');
 for(const [path,delta] of [['coverage.json',{status:'focused',selection:'focused',discovered:1,executed:1}],['measurement.json',{selection:'focused',groups:[{suite:'functional',phase:'complete',testExitCode:0}]}]]) f.put('postgresql',path,{...JSON.parse(content.get(path)),...delta});
 f.put('postgresql','cases.json',rows.map(row=>({...row,outcome:'Passed'})));
 f.put('postgresql','counts.json',{total:1,passed:1,failed:0,skipped:0});
 return {...f,selection:{scope:'targeted',cases:rows}};
}
test('targeted PostgreSQL can satisfy merge validator but never default full release validator',()=>{
 const f=focused();assert.equal(validateEvidence(identity,f.plan,f.artifacts,1,f.selection)[3].status,'targeted-complete');
 assert.throws(()=>validateEvidence(identity,f.plan,f.artifacts,1));
 assert.throws(()=>validatePostgreSqlEvidence(identity,f.plan,f.artifacts,1));
});
test('targeted missing theory/member, cancellation, wrong source and cleanup cannot green',()=>{
 for(const [file,delta] of [['coverage.json',{executed:0}],['measurement.json',{source:sha('e')}],['measurement.json',{testExitCode:130}],['resources.json',{active:1}]]) {
  const f=focused();f.put('postgresql',file,{...JSON.parse(f.artifacts.get(f.names.postgresql).get(file)),...delta});assert.throws(()=>validateEvidence(identity,f.plan,f.artifacts,1,f.selection));
 }
 const f=focused();f.artifacts.get(f.names.postgresql).delete('functional/cases.json');assert.throws(()=>validateEvidence(identity,f.plan,f.artifacts,1,f.selection));
});
test('nightly both suites full validator rejects missing qualification and no successful invocation',()=>{
 const f=qualificationFixture(identity);assert.equal(validatePostgreSqlEvidence(identity,f.plan,f.artifacts,1)[0].status,'complete');
 f.artifacts.get(f.names.postgresql).delete('infrastructure-qualification/cases.json');assert.throws(()=>validatePostgreSqlEvidence(identity,f.plan,f.artifacts,1));
});
test('merge publication checks separate context, artifact invocation and current candidate',async()=>{
 const root=mkdtempSync(join(tmpdir(),'haby-merge-'));
 try {
  const path=join(root,'summary.json');writeFileSync(path,JSON.stringify({...identity,qualification:'merge-only-not-release',outcome:'verified',runId:42,attempt:1}));
  let actual;
  const api={rest:{actions:{getArtifact:async()=>({data:{id:7,name:'merge-control-42-1',workflow_run:{id:42},expired:false,size_in_bytes:100}})},
   pulls:{get:async()=>({data:{state:'open',number:17,head:{sha:identity.head},base:{ref:'main',repo:{full_name:identity.repository}},merge_commit_sha:identity.merge}})},
   git:{getCommit:async()=>({data:{sha:identity.merge,tree:{sha:identity.tree},parents:[{sha:identity.base},{sha:identity.head}]}})},repos:{getBranch:async()=>({data:{commit:{sha:identity.base}}})},
   checks:{get:async()=>({data:{head_sha:identity.head,name:'Merge qualification',external_id:'42:1'}}),update:async x=>{actual=x;}}}};
  await publish(api,identity,42,1,'10',path,{reduction:'success',upload:'success',artifact:'7'});assert.equal(actual.conclusion,'success');
  api.rest.repos.getBranch=async()=>({data:{commit:{sha:sha('e')}}});await assert.rejects(()=>publish(api,identity,42,1,'10',path,{reduction:'success',upload:'success',artifact:'7'}));assert.equal(actual.conclusion,'failure');
 } finally {rmSync(root,{recursive:true,force:true});}
});
test('merge refuses wrong workflow before API; installed Final remains distinct',async()=>{
 await assert.rejects(()=>prepare({}, {repository:'example/project',workflowRef:'example/project/.github/workflows/merge-qualification.yml@refs/heads/topic'}, {},'actor'));
 const merge=readFileSync(new URL('../.github/workflows/merge-qualification.yml',import.meta.url),'utf8');
 assert(merge.includes("name: 'Merge qualification'"));assert(!merge.includes("name: 'Final qualification'"));
 const final=readFileSync(new URL('../.github/workflows/final-qualification.yml',import.meta.url),'utf8');assert(!final.includes('postgresql-selection:'));
 const nightly=readFileSync(new URL('../.github/workflows/nightly-postgresql.yml',import.meta.url),'utf8');assert(nightly.includes('cancel-in-progress: false'));assert(nightly.includes('candidate: ${{ needs.capture.outputs.source }}'));
});

test('nightly cancellation, missing/unknown invocation and malformed identity cannot be green',()=>{
 for(const result of ['failure','cancelled','skipped','missing','in_progress',undefined]) {
  const report=nightly('.', 'unused', identity.merge,42,1,result);
  assert.equal(report.outcome,'failure');assert.equal(report.cleanup,'not-established');assert(report.primary);
 }
 for(const source of ['main','',undefined]) assert.equal(nightly('.','unused',source,42,1,'success').outcome,'failure');
 const workflow=readFileSync(new URL('../.github/workflows/infrastructure-qualification.yml',import.meta.url),'utf8');
 assert(workflow.includes('nightly_full_rehearsal'));assert(workflow.includes('uses: ./.github/workflows/nightly-postgresql.yml'));
 const build=readFileSync(new URL('../.github/workflows/build.yml',import.meta.url),'utf8');
 assert(build.includes("github.event_name != 'push' || inputs.candidate != ''"));
});
