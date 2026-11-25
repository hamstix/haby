import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { execFileSync } from 'node:child_process';
import { selectedRows, smoke, nativeClasses, cliLeaves, select, literalSkeleton } from './postgresql-relevance.mjs';
const plan = JSON.parse(readFileSync(new URL('../tests/Haby.PostgreSql.IntegrationTests/suite-gate-map.json', import.meta.url)));
test('fixed two real facts and native indirect class union retain exact identities', () => {
 assert.equal(smoke.length, 2);
 const rows = selectedRows(plan, [], nativeClasses);
 assert.equal(rows.length, new Set(plan.entries.filter(row => nativeClasses.some(name => row.method.startsWith(`Haby.PostgreSql.IntegrationTests.${name}.`)) || smoke.some(fixed => fixed.method === row.method && fixed.case === row.case)).map(row => `${row.method}#${row.case}`)).size);
 for (const fixed of smoke) assert(rows.some(row => row.method === fixed.method && row.case === fixed.case));
 for (const name of ['DurableDeploymentPlanningTests', 'ApplicationLoginLoggingTests']) assert(rows.some(row => row.method.includes(`.${name}.`)));
});
test('missing smoke, duplicate discovery and unmapped classes fail closed', () => {
 assert.throws(() => selectedRows({entries: plan.entries.filter(row => row.case !== smoke[0].case)}, [], []));
 assert.throws(() => selectedRows({entries:[...plan.entries,plan.entries.find(row=>row.case===smoke[0].case)]}, [], []));
 assert.throws(() => selectedRows(plan, [], ['MissingTests']));
});
test('leaf literal edits can narrow but new calls and shared state cannot', () => {
 assert.equal(literalSkeleton('Call("old", 3);'), literalSkeleton('Call("new", 4);'));
 assert.notEqual(literalSkeleton('Call("old");'), literalSkeleton('Other("old");'));
});

function fixture(action) {
 const root=mkdtempSync(join(tmpdir(),'haby-relevance-'));
 const run=(...args)=>execFileSync('git',['-C',root,...args],{encoding:'utf8',stdio:['pipe','pipe','pipe']}).trim();
 const put=(file,text)=>{mkdirSync(dirname(join(root,file)),{recursive:true});writeFileSync(join(root,file),text);};
 try {
  run('init');run('config','user.name','test');run('config','user.email','test@example.invalid');
  put('tests/Haby.PostgreSql.IntegrationTests/suite-gate-map.json',JSON.stringify(plan));
  for(const file of cliLeaves) put(file,'Call("before");');
  put('tests/Haby.PostgreSql.IntegrationTests/Import.cs','public sealed partial class PostgreSqlPairImportTests { public void Test() {} }');
  put('tests/Haby.PostgreSql.IntegrationTests/OwnedInfrastructureTests.cs','public sealed class OwnedInfrastructureTests { public void Control() {} }');
  run('add','.');run('commit','-m','base');const before=run('rev-parse','HEAD');
  action({root,run,put,before,commit:()=>{run('add','.');run('commit','-m','change');return run('rev-parse','HEAD');}});
 } finally {rmSync(root,{recursive:true,force:true});}
}
test('each audited leaf selects current native closure and fixed smoke; structural edit uses full',()=>{
 for(const file of cliLeaves) fixture(({root,put,before,commit})=>{
  put(file,'Call("after");');const result=select(root,before,commit());
  assert.equal(result.scope,'targeted');assert.equal(result.cases.length,selectedRows(plan,[],nativeClasses).length);
  assert(result.classes.includes('DurableDeploymentPlanningTests'));
 });
 fixture(({root,put,before,commit})=>{put(cliLeaves[0],'NewDependency("after");');const result=select(root,before,commit());assert.equal(result.scope,'full');assert(result.reasons.includes('cli-leaf-structural-change'));});
});
test('profile parser, shared context, sibling command, helper, rename and new native consumers visibly broaden',()=>{
 for(const file of ['src/Haby.Manifest/ProfilePayloadParser.cs','src/habyctl/CliCommandContext.cs','src/habyctl/Commands/Placements/PlacementSelectionCommands.cs','tests/ProfilePersistenceSamples.cs'])
 fixture(({root,put,before,commit})=>{put(file,'changed');assert.equal(select(root,before,commit()).scope,'full');});
 fixture(({root,put,before,commit,run})=>{run('mv',cliLeaves[0],'renamed.cs');assert.equal(select(root,before,commit()).scope,'full');});
 fixture(({root,put,before,commit})=>{put(cliLeaves[0],'Call("after");');put('tests/Haby.PostgreSql.IntegrationTests/New.cs','public sealed class NewNativeTests { string Binary = "HABY_AOT_CLI_PATH"; }');const result=select(root,before,commit());assert.equal(result.scope,'full');assert(result.reasons.includes('unclassified-native-consumer'));});
});
test('test-only actual partial class selects all theory cases while abstract helper edit is full',()=>{
 fixture(({root,put,before,commit})=>{put('tests/Haby.PostgreSql.IntegrationTests/Import.cs','public sealed partial class PostgreSqlPairImportTests { public void Test2() {} }');const result=select(root,before,commit());assert.equal(result.scope,'targeted');assert.equal(result.cases.length,selectedRows(plan,[],['PostgreSqlPairImportTests']).length);});
 fixture(({root,put,before,commit})=>{put('tests/Haby.PostgreSql.IntegrationTests/Import.cs','public abstract partial class ApplicationLoginScenario {}');assert.equal(select(root,before,commit()).scope,'full');});
});

test('infrastructure class plus fixed functional smoke deterministically selects FULL before runtime',()=>{
 fixture(({root,put,before,commit})=>{
  put('tests/Haby.PostgreSql.IntegrationTests/OwnedInfrastructureTests.cs','public sealed class OwnedInfrastructureTests { public void ChangedControl() {} }');
  const result=select(root,before,commit());
  assert.equal(result.scope,'full');
  assert(result.reasons.includes('mixed-suite-targeted-fallback'));
  assert.deepEqual(result.cases,[]);
 });
});
