import base64,copy,json,unittest
from pathlib import Path
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'registry_preload'))
import preload_images as p
R=Path(p.__file__).resolve().parent
def raw(name):return base64.b64decode((R/name.replace('.json','.b64')).read_bytes(),validate=False)
class Controls(unittest.TestCase):
 def setUp(self):
  self.row=json.loads((R/'image-pins.json').read_bytes())[0];self.tag=self.row['tag'].split(':')[1]
  self.index=raw(self.tag+'-index.json');self.manifest=raw(self.tag+'-manifest.json')
  self.hub=json.dumps({'digest':self.row['indexDigest'],'images':[{'os':'linux','architecture':'amd64','digest':self.row['manifestDigest']}]}).encode()
  self.ins={'Id':self.row['configDigest'],'Os':'linux','Architecture':'amd64','RepoDigests':[self.row['mirror']]}
 def test_exact(self):p.validate(self.row,self.hub,self.index,self.manifest);p.inspect_ok(self.row,self.ins)
 def test_index(self):
  with self.assertRaises(ValueError):p.validate(self.row,self.hub,self.index+b' ',self.manifest)
 def test_manifest(self):
  with self.assertRaises(ValueError):p.validate(self.row,self.hub,self.index,self.manifest+b' ')
 def test_hub(self):
  h=json.loads(self.hub);h['digest']='sha256:'+'0'*64
  with self.assertRaises(ValueError):p.validate(self.row,json.dumps(h).encode(),self.index,self.manifest)
 def test_platform(self):
  h=json.loads(self.hub);h['images'][0]['architecture']='arm64'
  with self.assertRaises(ValueError):p.validate(self.row,json.dumps(h).encode(),self.index,self.manifest)
 def test_config(self):
  row=copy.deepcopy(self.row);row['configDigest']='sha256:'+'0'*64
  with self.assertRaises(ValueError):p.validate(row,self.hub,self.index,self.manifest)
 def test_layers(self):
  row=copy.deepcopy(self.row);row['layers'][0]['size']+=1
  with self.assertRaises(ValueError):p.validate(row,self.hub,self.index,self.manifest)
 def test_engine(self):
  for key,value in [('Id','wrong'),('Os','windows'),('Architecture','arm64'),('RepoDigests',[])]:
   with self.subTest(key=key):
    item=copy.deepcopy(self.ins);item[key]=value
    with self.assertRaises(ValueError):p.inspect_ok(self.row,item)
 def scenario(self,mode):
  calls=[];created=[];saved=[]
  def network(url,token=None):
   if '/token' in url:return b'{"token":"unused-fake"}'
   if 'hub.docker' in url:return self.hub if mode!='metadata-fail' else b'{}'
   return self.index if url.endswith(self.row['indexDigest']) else self.manifest
  def run(args,required=True):
   calls.append(args)
   if args[1:3]==['image','inspect']:
    if args[-1]==self.row['mirror'] and mode=='pull-fail':return None
    if args[-1]==self.row['tag'] and not created:
     if mode=='foreign':return json.dumps([dict(self.ins,Id='foreign')]).encode()
     if mode=='cache':return json.dumps([self.ins]).encode()
     return None
    return json.dumps([self.ins]).encode()
   if args[1]=='pull' and mode=='pull-fail':raise RuntimeError('pull failed')
   if args[1]=='tag':created.append(args[-1])
   return b''
  receipt={'createdTags':[],'write':lambda:saved.append(True)}
  return calls,created,saved,lambda:p.preload([self.row],run,network,receipt)
 def test_preload(self):
  calls,created,saved,invoke=self.scenario('success');invoke();self.assertEqual(created,[self.row['tag']]);self.assertEqual(len(saved),2)
 def test_existing_cache(self):
  calls,created,saved,invoke=self.scenario('cache');invoke();self.assertFalse(created);self.assertEqual(len(calls),1)
 def test_foreign_preserved(self):
  calls,created,saved,invoke=self.scenario('foreign')
  with self.assertRaises(ValueError):invoke()
  self.assertFalse(created);self.assertFalse(any(c[1]=='pull' for c in calls))
 def test_pull_failure_no_retag(self):
  calls,created,saved,invoke=self.scenario('pull-fail')
  with self.assertRaises(RuntimeError):invoke()
  self.assertFalse(created)
 def test_metadata_before_mutation(self):
  calls,created,saved,invoke=self.scenario('metadata-fail')
  with self.assertRaises(KeyError):invoke()
  self.assertFalse(calls)
 def test_partial_pull_timeout_journaled(self):self.partial('pull',TimeoutError('partial pull'))
 def test_partial_tag_timeout_journaled(self):self.partial('tag',TimeoutError('partial tag'))
 def test_partial_pull_nonzero_journaled(self):self.partial('pull',RuntimeError('partial pull'))
 def test_partial_tag_nonzero_journaled(self):self.partial('tag',RuntimeError('partial tag'))
 def partial(self,verb,primary):
  snapshots=[];created=[];receipt={'createdTags':[],'write':lambda:snapshots.append(copy.deepcopy(receipt['createdTags']))}
  def run(args,required=True):
   if args[1]==verb:
    self.assertEqual(snapshots[-1][0]['state'],'intent');created.append(True);raise primary
   return json.dumps([self.ins]).encode() if created else None
  with self.assertRaises(type(primary)) as caught:p.mutate(['docker',verb],self.row['tag'],self.row,run,receipt)
  self.assertIs(caught.exception,primary);self.assertEqual(snapshots[-1][0]['state'],'exact-reference-observed')
 def test_prejournal_write_failure_prevents_mutation(self):
  calls=[];receipt={'createdTags':[],'write':lambda:(_ for _ in ()).throw(OSError('disk'))}
  with self.assertRaises(OSError):p.mutate(['docker','tag'],self.row['tag'],self.row,lambda *a:calls.append(a),receipt)
  self.assertFalse(calls)
 def test_secondary_write_preserves_primary(self):
  primary=TimeoutError('daemon');writes=[]
  def save():
   writes.append(True)
   if len(writes)>1:raise OSError('disk')
  def run(args,required=True):
   if args[1]=='tag':raise primary
   return json.dumps([self.ins]).encode()
  with self.assertRaises(TimeoutError) as caught:p.mutate(['docker','tag'],self.row['tag'],self.row,run,{'createdTags':[],'write':save})
  self.assertIs(caught.exception,primary);self.assertTrue(primary.__notes__)
 def test_final_save_preserves_primary(self):
  primary=ValueError('original')
  with self.assertRaises(ValueError) as caught:p.preserve_primary(lambda:(_ for _ in ()).throw(primary),lambda:(_ for _ in ()).throw(OSError('disk')))
  self.assertIs(caught.exception,primary)
 def cleanup_scenario(self,mode):
  entries=[{'tag':tag,'configDigest':self.row['configDigest'],'preExisting':False,'state':'intent','commandSettled':True} for tag in ('independent','blocked')];removed=[];saved=[]
  def run(args,required=True):
   if args[1:3]==['image','inspect']:
    if args[-1] in removed:return None
    return json.dumps([dict(self.ins,Id='foreign' if args[-1]=='blocked' and mode=='foreign' else self.ins['Id'])]).encode()
   if args[1]=='ps':return b'active' if mode=='active' and not removed and entries[1]['state']!='absent' else b''
   if args[1:3]==['image','rm']:
    if args[-1]=='blocked' and mode=='remove-fail':raise RuntimeError('remove')
    removed.append(args[-1])
   return b''
  def save():
   saved.append(True)
   if mode=='save-fail':raise OSError('disk')
  return entries,removed,saved,lambda:p.cleanup({'createdTags':entries},run,save)
 def test_cleanup_foreign_continues(self):
  entries,removed,saved,invoke=self.cleanup_scenario('foreign')
  with self.assertRaises(ExceptionGroup):invoke()
  self.assertEqual(removed,['independent']);self.assertEqual(len(saved),2)
 def test_cleanup_remove_failure_continues(self):
  entries,removed,saved,invoke=self.cleanup_scenario('remove-fail')
  with self.assertRaises(ExceptionGroup):invoke()
  self.assertEqual(removed,['independent'])
 def test_late_daemon_mutation_never_discharges_intent(self):
  entry={'tag':'late','configDigest':self.row['configDigest'],'preExisting':False,'state':'intent','commandSettled':False};daemon={'present':False};saved=[];removed=[]
  def run(args,required=True):
   if args[1:3]==['image','inspect']:return json.dumps([self.ins]).encode() if daemon['present'] else None
   if args[1:3]==['image','rm']:daemon['present']=False;removed.append(args[-1])
   return b''
  data={'createdTags':[entry]}
  with self.assertRaises(ExceptionGroup):p.cleanup(data,run,lambda:saved.append(copy.deepcopy(entry)))
  self.assertEqual(entry['state'],'absence-unsettled');self.assertTrue(entry['unresolvedDaemonOperation'])
  daemon['present']=True # The daemon completes after the first absent observation.
  with self.assertRaises(ExceptionGroup):p.cleanup(data,run,lambda:saved.append(copy.deepcopy(entry)))
  self.assertEqual(removed,['late']);self.assertEqual(entry['state'],'absence-unsettled');self.assertFalse(entry['commandSettled'])
 def test_unsettled_absence_does_not_block_independent_cleanup(self):
  pending={'tag':'late','configDigest':self.row['configDigest'],'preExisting':False,'state':'intent','commandSettled':False}
  settled=dict(pending,tag='independent',commandSettled=True);removed=[]
  def run(args,required=True):
   if args[1:3]==['image','inspect']:return None if args[-1]=='late' or args[-1] in removed else json.dumps([self.ins]).encode()
   if args[1:3]==['image','rm']:removed.append(args[-1])
   return b''
  with self.assertRaises(ExceptionGroup):p.cleanup({'createdTags':[settled,pending]},run,lambda:None)
  self.assertEqual(removed,['independent']);self.assertEqual(settled['state'],'absent');self.assertEqual(pending['state'],'absence-unsettled')
 def test_cleanup_write_failure_continues(self):
  entries,removed,saved,invoke=self.cleanup_scenario('save-fail')
  with self.assertRaises(ExceptionGroup):invoke()
  self.assertEqual(removed,['blocked','independent'])
 def test_cleanup_missing_reconciles(self):
  entry={'tag':'missing','configDigest':self.row['configDigest'],'preExisting':False,'state':'intent','commandSettled':True}
  p.cleanup({'createdTags':[entry]},lambda *args:None,lambda:None);self.assertEqual(entry['state'],'absent')
 def test_atomic_receipt_real_file(self):
  import tempfile
  with tempfile.TemporaryDirectory() as root:
   file=Path(root)/'receipt.json';p.atomic_save(file,{'createdTags':[]});self.assertEqual(json.loads(file.read_bytes()),{'createdTags':[]});self.assertFalse(file.with_suffix('.pending').exists())
 def test_atomic_preexisting_pending_preserved(self):
  import tempfile
  with tempfile.TemporaryDirectory() as root:
   file=Path(root)/'receipt.json';pending=file.with_suffix('.pending');pending.write_bytes(b'foreign')
   with self.assertRaises(FileExistsError):p.atomic_save(file,{})
   self.assertEqual(pending.read_bytes(),b'foreign');self.assertFalse(file.exists())
 def test_real_journal_partial_pull_timeout(self):self.real_journal_partial('pull')
 def test_real_journal_partial_tag_timeout(self):self.real_journal_partial('tag')
 def real_journal_partial(self,verb):
  import tempfile
  with tempfile.TemporaryDirectory() as root:
   path=Path(root)/'receipt.json';data={'createdTags':[]};created=[]
   receipt={'createdTags':data['createdTags'],'write':lambda:p.atomic_save(path,data)}
   def run(args,required=True):
    if args[1]==verb:
     recorded=json.loads(path.read_bytes());self.assertEqual(recorded['createdTags'][0]['state'],'intent')
     created.append(True);raise TimeoutError('daemon mutated before client timed out')
    return json.dumps([self.ins]).encode() if created else None
   with self.assertRaises(TimeoutError):p.mutate(['docker',verb],self.row['tag'],self.row,run,receipt)
   self.assertEqual(json.loads(path.read_bytes())['createdTags'][0]['state'],'exact-reference-observed')
 def test_cleanup_active_reference_continues(self):
  entries=[{'tag':tag,'configDigest':self.row['configDigest'],'preExisting':False,'state':'intent','commandSettled':True} for tag in ('independent','active')];last=[];removed=[]
  def run(args,required=True):
   if args[1:3]==['image','inspect']:
    last[:]=[args[-1]]
    return None if args[-1] in removed else json.dumps([self.ins]).encode()
   if args[1]=='ps':return b'client' if last==['active'] else b''
   if args[1:3]==['image','rm']:removed.append(args[-1])
   return b''
  with self.assertRaises(ExceptionGroup):p.cleanup({'createdTags':entries},run,lambda:None)
  self.assertEqual(removed,['independent'])
 def test_reaper_grace_waits_for_observed_exit(self):self.reaper_grace('settled')
 def test_reaper_grace_preserves_persistent_active_container(self):self.reaper_grace('persistent')
 def test_reaper_grace_preserves_replaced_reference(self):self.reaper_grace('replaced')
 def test_reaper_grace_query_failure_does_not_remove_reference(self):self.reaper_grace('query-failure')
 def test_reaper_grace_cannot_extend_parent_cleanup_deadline(self):self.reaper_grace('deadline')
 def reaper_grace(self,mode):
  from unittest.mock import patch
  row=json.loads((R/'image-pins.json').read_bytes())[2]
  entry={'tag':p.RYUK,'configDigest':row['configDigest'],'preExisting':False,'commandSettled':True,'state':'intent'}
  clock=[0];queries=[];removed=[];sleeps=[]
  def sleep(seconds):sleeps.append(seconds);clock[0]+=seconds
  def run(args,required=True):
   if args[1:3]==['image','inspect']:
    if removed:return None
    return json.dumps([{'Id':'foreign' if mode=='replaced' and sleeps else row['configDigest']}]).encode()
   if args[1]=='ps':
    queries.append(args)
    if mode=='query-failure' and len(queries)>1:raise OSError('query unavailable')
    return b'active' if mode!='settled' or len(queries)<3 else b''
   if args[1:3]==['image','rm']:
    self.assertEqual(mode,'settled');self.assertGreaterEqual(len(queries),3);removed.append(args[-1]);return b''
   self.fail('Unexpected container mutation')
  with patch.object(p.time,'monotonic',side_effect=lambda:clock[0]),patch.object(p.time,'sleep',side_effect=sleep):
   if mode=='settled':p.cleanup({'createdTags':[entry]},run,lambda:None)
   else:
    with self.assertRaises(ExceptionGroup):p.cleanup({'createdTags':[entry]},run,lambda:None,deadline=2 if mode=='deadline' else 90)
  if mode=='settled':self.assertEqual(removed,[p.RYUK]);self.assertEqual(entry['state'],'absent')
  else:self.assertFalse(removed)
  self.assertEqual(bool(sleeps),mode!='deadline');self.assertLessEqual(clock[0],15)
class RegistryBindings(unittest.TestCase):
 def setUp(self):self.rows=json.loads((R/'image-pins.json').read_bytes())
 def test_closed_three_sources(self):
  self.assertEqual(len(self.rows),3)
  for row in self.rows:p.binding(row)
 def test_superseded_fixture_identities_are_rejected(self):
  previous={'postgres:18-alpine':'77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873',
   'redis:7-alpine':'858f009f9709ce576febc734aa78b8f6d624b82571f9ddb6bda4377c833b3499'}
  for row in self.rows[:2]:
   with self.subTest(tag=row['tag']):
    old=copy.deepcopy(row);old['indexDigest']='sha256:'+previous[row['tag']]
    old['mirror']=row['mirror'].split('@')[0]+'@'+old['indexDigest']
    with self.assertRaisesRegex(ValueError,'Repository or immutable index substitution'):p.binding(old)
 def test_all_original_raw_provenance(self):
  for row in self.rows:
   name={'postgres:18-alpine':'18-alpine','redis:7-alpine':'redis7',p.RYUK:'ryuk014'}[row['tag']]
   p.validate(row,raw(name+'-hub.json'),raw(name+'-index.json'),raw(name+'-manifest.json'))
 def test_cross_repository_substitution(self):
  for row in self.rows:
   for change in ({'mirror':self.rows[0]['mirror']},{'tag':'postgres:18.1-alpine'},{'indexDigest':'sha256:'+'0'*64},{'extra':True}):
    altered=dict(row,**change)
    if altered==row:continue
    with self.assertRaises(ValueError):p.binding(altered)
 def test_shape_rejection_before_network_or_daemon(self):
  calls=[]
  rows=[self.rows[0],dict(self.rows[1],mirror=self.rows[0]['mirror'])]
  with self.assertRaises(ValueError):p.preload(rows,lambda *a:calls.append(a),lambda *a:calls.append(a),{})
  self.assertFalse(calls)
 def test_duplicate_rejected_before_network(self):
  calls=[]
  with self.assertRaises(ValueError):p.preload([self.rows[0]]*2,lambda *a:calls.append(a),lambda *a:calls.append(a),{})
  self.assertFalse(calls)
 def test_registry_token_and_metadata_are_exact(self):
  seen=[];calls=[]
  def network(url,token=None):
   seen.append((url,token))
   if '/token' in url:return b'{"token":"fake"}'
   for row in self.rows:
    source=p.binding(row);name={'postgres:18-alpine':'18-alpine','redis:7-alpine':'redis7',p.RYUK:'ryuk014'}[row['tag']]
    if url=='https://hub.docker.com/v2/repositories/'+source[0]+'/tags/'+source[1]:return raw(name+'-hub.json')
    for kind,key in [('index','indexDigest'),('manifest','manifestDigest')]:
     if url=='https://'+source[2]+'/v2/'+source[3]+'/manifests/'+row[key]:return raw(name+'-'+kind+'.json')
   raise AssertionError('Unexpected network URL')
  def run(args,required=True):
   calls.append(args)
   row=next(r for r in self.rows if args[-1]==r['tag'])
   return json.dumps([{'Id':row['configDigest'],'Os':'linux','Architecture':'amd64','RepoDigests':[row['mirror']]}]).encode()
  p.preload(self.rows,run,network,{'createdTags':[],'write':lambda:None})
  self.assertEqual(len(seen),12);self.assertEqual(len(calls),3)
  self.assertIn(('https://ghcr.io/token?service=ghcr.io&scope=repository:testcontainers/ryuk:pull',None),seen)
  self.assertIn(('https://public.ecr.aws/token/?service=public.ecr.aws&scope=repository:docker/library/redis:pull',None),seen)
 def test_wrong_repository_same_digest_rejected(self):
  row=self.rows[1]
  with self.assertRaises(ValueError):p.inspect_ok(row,{'Id':row['configDigest'],'Os':'linux','Architecture':'amd64','RepoDigests':['foreign.invalid/repo@'+row['indexDigest']]})
 def test_official_original_cache_is_reused(self):
  for row in self.rows[:2]:
   p.inspect_ok(row,{'Id':row['configDigest'],'Os':'linux','Architecture':'amd64','RepoDigests':[row['tag'].split(':')[0]+'@'+row['indexDigest']]})
 def test_wrong_ryuk_environment_fails_before_preload(self):
  import tempfile,os
  from unittest.mock import patch
  with tempfile.TemporaryDirectory() as root:
   with patch.dict(os.environ,{'RUNNER_TEMP':root,'TESTCONTAINERS_RYUK_CONTAINER_IMAGE':'wrong'}),patch.object(p,'preload') as preload,patch('sys.argv',['preload_images.py']):
    with self.assertRaisesRegex(ValueError,'Exact pinned Ryuk'):p.main()
    preload.assert_not_called();self.assertFalse((Path(root)/'quotation-registry-preload.json').exists())
 def test_ryuk_uses_digest_lookup_without_retag(self):
  row=self.rows[2];present=[];calls=[];saved=[]
  def network(url,token=None):
   if '/token' in url:return b'{"token":"fake"}'
   if 'hub.docker' in url:return raw('ryuk014-hub.json')
   return raw('ryuk014-index.json' if url.endswith(row['indexDigest']) else 'ryuk014-manifest.json')
  def run(args,required=True):
   calls.append(args)
   if args[1]=='pull':present.append(True);return b''
   if args[1:3]==['image','inspect']:
    self.assertEqual(args[-1],p.RYUK)
    return json.dumps([{'Id':row['configDigest'],'Os':'linux','Architecture':'amd64','RepoDigests':[row['mirror']]}]).encode() if present else None
   raise AssertionError('No retag expected')
  p.preload([row],run,network,{'createdTags':[],'write':lambda:saved.append(True)})
  self.assertEqual(sum(c[1]=='pull' for c in calls),1);self.assertFalse(any(c[1]=='tag' for c in calls));self.assertTrue(saved)
class MetadataThrottling(unittest.TestCase):
 def scenario(self,codes,retry_after=None,remaining=100):
  import urllib.error,io
  from unittest.mock import patch
  errors=[];calls=[];waits=[]
  class Response:
   def __enter__(self):return self
   def __exit__(self,*args):calls.append('closed-success')
   def read(self):return b'verified-response'
  class Opener:
   def open(self,request,timeout):
    calls.append((request.full_url,timeout))
    code=codes.pop(0)
    if code==200:return Response()
    error=urllib.error.HTTPError(request.full_url,code,'throttle',{} if retry_after is None else {'Retry-After':retry_after},io.BytesIO(b''))
    errors.append(error);raise error
  def invoke():
   with patch.object(p.urllib.request,'build_opener',return_value=Opener()),patch.object(p.time,'monotonic',return_value=0),patch.object(p.time,'sleep',side_effect=waits.append):
    return p.fetch('https://public.ecr.aws/v2/docker/library/redis/manifests/immutable',deadline=remaining)
  return errors,calls,waits,invoke
 def test_429_then_success_bounded_and_closed(self):
  errors,calls,waits,invoke=self.scenario([429,200],'3')
  self.assertEqual(invoke(),b'verified-response');self.assertEqual(waits,[3]);self.assertTrue(errors[0].fp.closed)
  self.assertEqual(sum(isinstance(c,tuple) for c in calls),2);self.assertIn('closed-success',calls)
 def test_repeated_429_stops_at_three(self):
  errors,calls,waits,invoke=self.scenario([429,429,429,200])
  with self.assertRaisesRegex(Exception,'429'):invoke()
  self.assertEqual(len(errors),3);self.assertEqual(waits,[2,4]);self.assertTrue(all(e.fp.closed for e in errors))
 def test_other_status_is_not_retried(self):
  for code in (401,403,404,500):
   errors,calls,waits,invoke=self.scenario([code,200])
   with self.assertRaises(Exception):invoke()
   self.assertEqual(len(errors),1);self.assertFalse(waits);self.assertTrue(errors[0].fp.closed)
 def test_long_server_delay_is_not_shortened(self):
  errors,calls,waits,invoke=self.scenario([429,200],'120')
  with self.assertRaises(Exception):invoke()
  self.assertFalse(waits);self.assertEqual(len(errors),1);self.assertTrue(errors[0].fp.closed)
 def test_invalid_or_date_retry_after_is_not_ignored(self):
  for value in ('invalid','Wed, 21 Oct 2015 07:28:00 GMT'):
   errors,calls,waits,invoke=self.scenario([429,200],value)
   with self.assertRaises(Exception):invoke()
   self.assertFalse(waits);self.assertTrue(errors[0].fp.closed)
 def test_deadline_prevents_sleep_or_new_request(self):
  errors,calls,waits,invoke=self.scenario([429,200],'3',remaining=3)
  with self.assertRaises(Exception):invoke()
  self.assertFalse(waits);self.assertEqual(len(errors),1)
  errors,calls,waits,invoke=self.scenario([200],remaining=1)
  with self.assertRaises(TimeoutError):invoke()
  self.assertFalse(calls)
class DockerDiagnostics(unittest.TestCase):
 def setUp(self):self.image=json.loads((R/'image-pins.json').read_bytes())[0]['mirror']
 def test_known_category_and_explicit_status(self):
  payload=b'HTTP 429: toomanyrequests';error=p.command_failure(['docker','pull',self.image],1,payload,len(payload))
  self.assertEqual(error.diagnostic['category'],'registry-rate-limit');self.assertEqual(error.diagnostic['explicitHttpStatus'],429)
 def test_unknown_secret_canary_never_disclosed(self):
  secret=b'CANARY-DO-NOT-DISCLOSE password=secret https://host/path?token=secret'
  error=p.command_failure(['docker','pull','foreign?token=secret'],1,secret,len(secret))
  encoded=json.dumps(error.diagnostic)+str(error)
  self.assertNotIn('CANARY',encoded);self.assertNotIn('password',encoded);self.assertNotIn('token=',encoded)
  self.assertEqual(error.diagnostic['category'],'unknown');self.assertIsNone(error.diagnostic['imageReference'])
 def test_prefix_hash_and_truncation_are_explicit(self):
  import hashlib
  prefix=b'x'*4096;error=p.command_failure(['docker','pull',self.image],1,prefix,9000)
  self.assertTrue(error.diagnostic['truncated']);self.assertEqual(error.diagnostic['capturedBytes'],4096)
  self.assertEqual(error.diagnostic['stderrBytes'],9000);self.assertEqual(error.diagnostic['hashScope'],'captured-prefix')
  self.assertEqual(error.diagnostic['sha256'],hashlib.sha256(prefix).hexdigest())
  with self.assertRaises(ValueError):p.command_failure(['docker','pull',self.image],1,b'x'*4097,4097)
 def test_mutation_preserves_diagnostic_primary_and_unsettled_intent(self):
  row=json.loads((R/'image-pins.json').read_bytes())[0];error=p.command_failure(['docker','pull',self.image],1,b'pull access denied',18)
  receipt={'createdTags':[],'write':lambda:None}
  def run(args,required=True):
   if args[1]=='pull':raise error
   return None
  with self.assertRaises(p.DockerCommandFailure) as caught:p.mutate(['docker','pull',self.image],self.image,row,run,receipt)
  self.assertIs(caught.exception,error);entry=receipt['createdTags'][0]
  self.assertEqual(entry['failureDiagnostic'],error.diagnostic);self.assertFalse(entry['commandSettled']);self.assertEqual(entry['state'],'absence-unsettled')
 def test_actual_main_bounds_diagnostic_prefix(self):self.main_boundary(False)
 def test_actual_main_timeout_preserves_primary(self):self.main_boundary(True)
 def main_boundary(self,timeout):
  import tempfile,os,subprocess
  from unittest.mock import patch
  handles=[];row=json.loads((R/'image-pins.json').read_bytes())[0];primary=subprocess.TimeoutExpired(['docker','pull',self.image],1)
  def invoke(rows,run,network,receipt):p.mutate(['docker','pull',self.image],self.image,row,run,receipt)
  def command(args,timeout,hard_deadline=None):
   if args[1]=='pull':
    if failure_timeout:raise primary
    result=subprocess.CompletedProcess(args,1,b'',b'x'*4096)
    result.owned_cli_resource={'pid':41,'startTicks':123,'executable':'/usr/bin/docker','identityObserved':True,'exitConfirmed':True,'unresolvedCustody':False}
    return result,9032
   return subprocess.CompletedProcess(args,1,b'',b'No such image'),13
  failure_timeout=timeout
  with tempfile.TemporaryDirectory() as root:
   with patch.dict(os.environ,{'RUNNER_TEMP':root,'TESTCONTAINERS_RYUK_CONTAINER_IMAGE':p.RYUK}),patch.object(p,'preload',side_effect=invoke),patch.object(p,'run_docker_cli',side_effect=command),patch('sys.argv',['preload_images.py']):
    with self.assertRaises(subprocess.TimeoutExpired if timeout else p.DockerCommandFailure) as caught:p.main()
   if timeout:self.assertIs(caught.exception,primary)
   entry=json.loads((Path(root)/'quotation-registry-preload.json').read_bytes())['createdTags'][0]
   self.assertFalse(entry['commandSettled']);self.assertEqual(entry['state'],'absence-unsettled')
   self.assertNotIn('CANARY',(Path(root)/'quotation-registry-preload.json').read_text())
   if not timeout:
    self.assertEqual(entry['failureDiagnostic']['capturedBytes'],4096);self.assertTrue(entry['failureDiagnostic']['truncated'])
    self.assertEqual(entry['cliResource']['startTicks'],123);self.assertEqual(entry['cliResource']['executable'],'/usr/bin/docker')
class MutationOutcomeControls(unittest.TestCase):
 def scenario(self,kind,present=False,custody=False):
  import tempfile,subprocess
  row=json.loads((R/'image-pins.json').read_bytes())[0]
  if kind=='rejection':primary=p.command_failure(['docker','pull',row['mirror']],1,b'toomanyrequests',15)
  elif kind=='killed':primary=p.command_failure(['docker','pull',row['mirror']],-9,b'',0)
  elif kind=='timeout':primary=subprocess.TimeoutExpired(['docker','pull'],1)
  else:primary=RuntimeError('mutation interrupted')
  if kind!='unknown':
   primary.owned_cli_resource={'exitConfirmed':True,'unresolvedCustody':custody,'terminalReturnCode':1 if kind=='rejection' else -9 if kind=='killed' else 0 if kind=='cleanup-failure' else -15,'executionOutcome':'completed' if kind in ('rejection','cleanup-failure') else 'interrupted'}
  def run(args,required=True):
   if args[1]=='pull':raise primary
   if args[1:3]==['image','inspect']:return json.dumps([{'Id':row['configDigest']}]).encode() if present else None
   if args[1:3]==['image','rm']:self.fail('No removal expected in this absent-reference scenario')
   return b''
  with tempfile.TemporaryDirectory() as root:
   path=Path(root)/'journal.json';data={'createdTags':[]}
   def save():p.atomic_save(path,data)
   with self.assertRaises(type(primary)) as caught:p.mutate(['docker','pull',row['mirror']],row['mirror'],row,run,{'createdTags':data['createdTags'],'write':save})
   self.assertIs(caught.exception,primary)
   entry=json.loads(path.read_bytes())['createdTags'][0]
   self.assertFalse(entry['commandSettled']);self.assertEqual(entry['daemonSettlementAuthority'],'unproved')
   self.assertIn('external confirmation required',entry['unresolvedLease'])
   self.assertEqual(entry['state'],'exact-reference-observed' if present else 'absence-unsettled')
   if not present:
    with self.assertRaises(ExceptionGroup):p.cleanup(data,run,save)
    after=json.loads(path.read_bytes())['createdTags'][0]
    self.assertEqual(after['cliOutcome'],entry['cliOutcome']);self.assertTrue(after['unresolvedDaemonOperation'])
   return entry['cliOutcome']
 def test_terminal_registry_rejection_retains_daemon_hold(self):
  self.assertEqual(self.scenario('rejection'),{'execution':'returned-failure','terminalReturnCode':1,'exitConfirmed':True,'custodyResolved':True})
 def test_timeout_exit_after_recovery_is_not_normal_completion(self):
  self.assertEqual(self.scenario('timeout')['execution'],'interrupted');self.assertEqual(self.scenario('timeout')['terminalReturnCode'],-15)
 def test_killed_cli_keeps_daemon_hold(self):self.assertEqual(self.scenario('killed')['execution'],'interrupted')
 def test_partial_mutation_keeps_unproved_authority(self):self.assertEqual(self.scenario('rejection',present=True)['execution'],'returned-failure')
 def test_uncertain_cli_cleanup_is_separate_from_exit(self):
  outcome=self.scenario('rejection',custody=True);self.assertTrue(outcome['exitConfirmed']);self.assertFalse(outcome['custodyResolved'])
 def test_completed_command_with_failed_custody_does_not_grant_authority(self):
  outcome=self.scenario('cleanup-failure',custody=True);self.assertEqual(outcome['execution'],'completed-custody-unresolved');self.assertEqual(outcome['terminalReturnCode'],0);self.assertFalse(outcome['custodyResolved'])
 def test_unknown_failure_does_not_invent_terminal_status(self):
  self.assertEqual(self.scenario('unknown'),{'execution':'not-observed','terminalReturnCode':None,'exitConfirmed':None,'custodyResolved':None})
 def test_success_outcome_survives_later_reconciliation_failure(self):
  row=json.loads((R/'image-pins.json').read_bytes())[0];data={'createdTags':[]};primary=RuntimeError('inspect unavailable')
  def run(args,required=True):
   if args[1]=='pull':return b''
   raise primary
  with self.assertRaises(RuntimeError) as caught:p.mutate(['docker','pull',row['mirror']],row['mirror'],row,run,{'createdTags':data['createdTags'],'write':lambda:None})
  self.assertIs(caught.exception,primary);entry=data['createdTags'][0]
  self.assertEqual(entry['cliOutcome']['execution'],'returned-success');self.assertEqual(entry['daemonSettlementAuthority'],'successful-cli-completion')
  self.assertTrue(entry['commandSettled']);self.assertEqual(entry['state'],'intent')
class LiveCaptureControls(unittest.TestCase):
 def observe_identity(self,ticks=(123,123),executable='/usr/bin/docker',oversize=False):
  import io,types
  from unittest.mock import patch
  payloads=[b'41 (docker worker) '+b' '.join([b'S']+[b'0']*18+[str(tick).encode()]) for tick in ticks]
  if oversize:payloads=[b'x'*8193]
  handles=[io.BytesIO(payload) for payload in payloads]
  with patch.object(Path,'open',side_effect=handles),patch('os.readlink',return_value=executable),patch('os.path.isabs',return_value=executable.startswith('/')),patch('os.path.realpath',side_effect=lambda path:path):
   try:return p.observe_cli_identity(types.SimpleNamespace(pid=41))
   finally:self.assertTrue(all(handle.closed for handle in handles))
 def test_identity_reads_kernel_birth_and_executable(self):
  self.assertEqual(self.observe_identity(),{'pid':41,'startTicks':123,'executable':'/usr/bin/docker'})
 def test_identity_rejects_birth_race(self):
  with self.assertRaisesRegex(RuntimeError,'unstable'):self.observe_identity((123,124))
 def test_identity_rejects_deleted_executable(self):
  with self.assertRaisesRegex(RuntimeError,'unstable'):self.observe_identity(executable='/usr/bin/docker (deleted)')
 def test_identity_caps_kernel_metadata(self):
  with self.assertRaisesRegex(RuntimeError,'cap'):self.observe_identity(oversize=True)
 def test_stderr_storage_is_bounded_while_counting_stream(self):
  c=p.DockerCapture()
  for _ in range(1000):c.feed('stderr',b'x'*4096)
  self.assertEqual(len(c.stderr),4096);self.assertEqual(c.stderr_bytes,4096000)
 def test_stdout_pressure_fails_before_growing_past_cap(self):
  c=p.DockerCapture()
  for _ in range(256):c.feed('stdout',b'x'*4096)
  with self.assertRaisesRegex(RuntimeError,'limit'):c.feed('stdout',b'x')
  self.assertEqual(len(c.stdout),1048576)
 def test_capture_and_chunk_faults_preserve_process_primary(self):self.adapter_fault('capture')
 def test_deadline_plus_all_close_faults_preserves_timeout(self):self.adapter_fault('deadline',close_faults=True)
 def test_registration_failure_closes_both_unregistered_pipes(self):self.adapter_fault('register')
 def test_ignored_graceful_stop_uses_exact_handle_kill_and_wait(self):self.adapter_fault('grace-timeout')
 def test_kill_failure_preserves_primary_and_closes_independent_handles(self):self.adapter_fault('kill-failure')
 def test_exhausted_recovery_does_not_renew_budget_or_erase_child(self):self.adapter_fault('budget-expiry')
 def test_changed_child_birth_refuses_signals_and_preserves_primary(self):self.adapter_fault('birth-drift')
 def test_changed_child_executable_refuses_signals_and_preserves_primary(self):self.adapter_fault('exe-drift')
 def test_executable_change_before_kill_refuses_forced_signal(self):self.adapter_fault('kill-drift')
 def test_partial_start_identity_failure_retains_unknown_child(self):self.adapter_fault('identity-fault')
 def test_missing_live_identity_refuses_signals(self):self.adapter_fault('identity-missing')
 def test_exit_during_identity_observation_retains_terminal_handle(self):
  import subprocess,selectors
  from unittest.mock import patch
  class Pipe:
   closed=False
   def fileno(self):return 11
   def close(self):self.closed=True
  class Process:
   pid=41;returncode=7;stdout=Pipe();stderr=Pipe()
   def poll(self):return self.returncode
   def wait(self,timeout):return self.returncode
   def terminate(self):raise AssertionError('Terminal child must not be signalled')
   kill=terminate
  class Selector:
   def register(self,*args):pass
   def get_map(self):return {}
   def close(self):pass
  child=Process()
  with patch('os.name','posix'),patch.object(p.subprocess,'Popen',return_value=child),patch.object(p,'observe_cli_identity',side_effect=FileNotFoundError('Exited executable')),patch.object(selectors,'DefaultSelector',return_value=Selector()),patch('os.set_blocking'):
   result,total=p.run_docker_cli(['docker','inspect'],6)
  self.assertEqual(result.returncode,7);self.assertEqual(total,0)
  self.assertEqual(result.owned_cli_resource['executionOutcome'],'completed');self.assertEqual(result.owned_cli_resource['terminalReturnCode'],7)
  self.assertTrue(result.owned_cli_resource['exitConfirmed']);self.assertFalse(result.owned_cli_resource['unresolvedCustody'])
  self.assertTrue(result.owned_cli_resource['identityUnavailableAfterExit']);self.assertIsNone(result.owned_cli_resource['startTicks'])
  self.assertTrue(child.stdout.closed and child.stderr.closed)
 def adapter_fault(self,mode,close_faults=False):
  import subprocess,types,selectors
  from unittest.mock import patch
  primary=FileNotFoundError('Live identity missing') if mode=='identity-missing' else OSError('actual capture/register failure');closed=[];events=[];settled=[]
  class Pipe:
   def __init__(self,name):self.name=name
   def fileno(self):return 11 if self.name=='stdout' else 12
   def close(self):
    closed.append(self.name)
    if close_faults:raise OSError('close')
  class Process:
   pid=41
   stdout=Pipe('stdout');stderr=Pipe('stderr')
   returncode=None
   def poll(self):return self.returncode
   def terminate(self):
    events.append('terminate')
    if mode not in ('grace-timeout','kill-failure','kill-drift'):self.returncode=0
   def kill(self):
    events.append('kill')
    if mode=='kill-failure':raise OSError('kill')
    self.returncode=-9
   def wait(self,timeout):
    settled.append(timeout)
    if self.returncode is None:raise subprocess.TimeoutExpired(['docker'],timeout)
    return self.returncode
  class Selector:
   def register(self,*args):
    if mode=='register':raise primary
   def get_map(self):return {'owned':True}
   def select(self,timeout):return [(types.SimpleNamespace(fd=11,data='stdout',fileobj=None),1)]
   def close(self):
    closed.append('selector')
    if close_faults:raise OSError('close')
  clocks=[0]+[3]*20 if mode=='deadline' else [0,0]+[6]*20 if mode=='budget-expiry' else [0]*20
  observations=[]
  def observe(process):
   observations.append(process.pid)
   if mode in ('identity-fault','identity-missing'):raise primary
   identity={'pid':41,'startTicks':123,'executable':'/usr/bin/docker'}
   if mode=='birth-drift' and len(observations)>1:identity['startTicks']=124
   if mode=='exe-drift' and len(observations)>1 or mode=='kill-drift' and len(observations)>2:identity['executable']='/foreign/executable'
   return identity
  with patch('os.name','posix'),patch.object(p.subprocess,'Popen',return_value=Process()),patch.object(p,'observe_cli_identity',side_effect=observe,create=True),patch.object(selectors,'DefaultSelector',return_value=Selector()),patch('os.set_blocking'),patch('os.read',side_effect=primary),patch.object(p.time,'monotonic',side_effect=clocks):
   with self.assertRaises(subprocess.TimeoutExpired if mode=='deadline' else OSError) as caught:p.run_docker_cli(['docker','pull',p.RYUK],5)
  if mode!='deadline':self.assertIs(caught.exception,primary)
  else:self.assertTrue(caught.exception.__notes__)
  self.assertEqual(caught.exception.owned_cli_resource['executionOutcome'],'interrupted')
  if mode=='grace-timeout':self.assertEqual(caught.exception.owned_cli_resource['terminalReturnCode'],-9)
  self.assertEqual(closed,['stdout','stderr'] if mode in ('identity-fault','identity-missing') else ['selector','stdout','stderr']);self.assertEqual(events,[] if mode in ('budget-expiry','birth-drift','exe-drift','identity-fault','identity-missing') else ['terminate','kill'] if mode in ('grace-timeout','kill-failure') else ['terminate'])
  if mode in ('birth-drift','exe-drift','kill-drift','identity-fault','identity-missing'):
   resource=caught.exception.owned_cli_resource
   self.assertTrue(resource['unresolvedCustody']);self.assertFalse(resource['exitConfirmed'])
   self.assertEqual(resource['identityObserved'],mode not in ('identity-fault','identity-missing'))
   self.assertEqual(resource['startTicks'],None if mode in ('identity-fault','identity-missing') else 123)
   self.assertEqual(resource['executable'],None if mode in ('identity-fault','identity-missing') else '/usr/bin/docker')
   self.assertTrue(caught.exception.__notes__)
  if mode=='budget-expiry':
   self.assertFalse(settled);self.assertFalse(caught.exception.owned_cli_resource['exitConfirmed']);self.assertTrue(caught.exception.__notes__)
  else:self.assertTrue(settled)
  if mode=='kill-failure':self.assertTrue(caught.exception.__notes__)
 @unittest.skipUnless(__import__('os').name=='posix','Actual selector pipe proof requires Linux hosted runner')
 def test_real_both_pipe_streams_and_stdout_limit(self):
  import subprocess,sys
  from unittest.mock import patch
  original=p.subprocess.Popen;children=[]
  def launch(args,**kwargs):
   child=original([sys.executable,'-c',"import sys;sys.stderr.buffer.write(b'x'*20000);sys.stdout.buffer.write(b'ok')"],**kwargs);children.append(child);return child
  with patch.object(p.subprocess,'Popen',side_effect=launch):
   result,total=p.run_docker_cli(['docker','pull',p.RYUK],6)
  self.assertEqual(result.stdout,b'ok');self.assertEqual(len(result.stderr),4096);self.assertEqual(total,20000)
  self.assertTrue(all(child.poll() is not None and child.stdout.closed and child.stderr.closed for child in children))
 @unittest.skipUnless(__import__('os').name=='posix','Actual short-lived child proof requires Linux hosted runner')
 def test_real_immediate_exit_and_nonzero_output(self):
  self.real_short_child(False,0);self.real_short_child(False,7)
 @unittest.skipUnless(__import__('os').name=='posix','Actual exit-observation race proof requires Linux hosted runner')
 def test_real_exit_during_identity_observation(self):self.real_short_child(True)
 def real_short_child(self,force_race,code=7):
  import sys
  from unittest.mock import patch
  original=p.subprocess.Popen;observe=p.observe_cli_identity;children=[]
  def launch(args,**kwargs):
   child=original([sys.executable,'-c',"import sys;sys.stdout.buffer.write(b'ok');sys.stderr.buffer.write(b'error');sys.exit("+str(code)+")"],**kwargs);children.append(child);return child
  def observation(child):
   if force_race:child.wait(timeout=1)
   return observe(child)
  with patch.object(p.subprocess,'Popen',side_effect=launch),patch.object(p,'observe_cli_identity',side_effect=observation):
   result,total=p.run_docker_cli(['docker','inspect'],6)
  self.assertEqual((result.returncode,result.stdout,result.stderr,total),(code,b'ok',b'error',5))
  self.assertEqual(result.owned_cli_resource['executionOutcome'],'completed');self.assertEqual(result.owned_cli_resource['terminalReturnCode'],code)
  self.assertTrue(result.owned_cli_resource['exitConfirmed']);self.assertFalse(result.owned_cli_resource['unresolvedCustody'])
  if force_race:self.assertTrue(result.owned_cli_resource['identityUnavailableAfterExit'])
  self.assertTrue(all(child.poll() is not None and child.stdout.closed and child.stderr.closed for child in children))
 def test_reserve_required_before_birth(self):
  from unittest.mock import patch
  with patch('os.name','posix'),patch.object(p.time,'monotonic',return_value=0),patch.object(p.subprocess,'Popen') as launch:
   with self.assertRaisesRegex(TimeoutError,'before process birth'):p.run_docker_cli(['docker'],3)
   launch.assert_not_called()
 def test_original_parent_hard_deadline_is_not_extended(self):
  from unittest.mock import patch
  with patch('os.name','posix'),patch.object(p.time,'monotonic',return_value=10),patch.object(p.subprocess,'Popen') as launch:
   with self.assertRaises(TimeoutError):p.run_docker_cli(['docker'],90,hard_deadline=12)
   launch.assert_not_called()
class SafeProviderMetadata(unittest.TestCase):
 def test_main_retains_safe_http_metadata_before_any_docker_mutation(self):
  import io,os,tempfile,urllib.error
  from unittest.mock import patch
  error=urllib.error.HTTPError('https://host/path?token=CANARY',403,'private reason CANARY',
   {'Retry-After':'5','RateLimit-Reset':'30','Authorization':'Bearer CANARY'},io.BytesIO(b'CANARY'))
  class Opener:
   def open(self,request,timeout):raise error
  def fake_preload(rows,run,fetch,receipt):return fetch('https://host/path?token=CANARY','CANARY')
  with tempfile.TemporaryDirectory(prefix='quotation-safe-metadata-control-') as directory:
   with patch.dict(os.environ,{'RUNNER_TEMP':directory,'TESTCONTAINERS_RYUK_CONTAINER_IMAGE':p.RYUK}),patch.object(sys,'argv',['preload_images.py']),patch.object(p,'preload',side_effect=fake_preload),patch.object(p.urllib.request,'build_opener',return_value=Opener()),patch.object(p,'run_docker_cli') as docker:
    with self.assertRaises(urllib.error.HTTPError) as caught:p.main()
    self.assertIs(caught.exception,error);docker.assert_not_called()
   data=json.loads((Path(directory)/'quotation-registry-preload.json').read_bytes())
   self.assertEqual(data['createdTags'],[]);self.assertEqual(data['metadataFailureDiagnostic']['httpStatus'],403)
   self.assertEqual(data['metadataFailureDiagnostic']['retryAfterSeconds'],5)
   self.assertNotIn('CANARY',json.dumps(data));self.assertTrue(error.fp.closed)
 def test_cli_code_status_and_retry_are_observed_without_raw_echo(self):
  payload=b'Error response from daemon: toomanyrequests: secret-canary token=private\nRetry-After: 120\nRateLimit-Reset: 30\nX-RateLimit-Reset: 1791936000'
  error=p.command_failure(['docker','pull',p.RYUK],1,payload,len(payload))
  meta=error.diagnostic['safeProviderMetadata']
  self.assertEqual(meta['errorCode'],'TOOMANYREQUESTS');self.assertEqual(meta['retryAfterSeconds'],120)
  self.assertEqual(meta['rateLimitResetValue'],30);self.assertEqual(meta['xRateLimitResetValue'],1791936000)
  self.assertIsNone(meta['httpStatus']);self.assertEqual(meta['origin'],'captured-cli-stderr')
  encoded=json.dumps(error.diagnostic)+str(error)
  self.assertNotIn('secret-canary',encoded);self.assertNotIn('token=',encoded);self.assertNotIn('private',encoded)
  self.assertEqual(meta['rateLimitNature'],'unclassified');self.assertEqual(meta['resetUnits'],'not-inferred')
 def test_missing_values_stay_unknown(self):
  meta=p.safe_provider_metadata('arbitrary sensitive provider wording')
  for key in ('errorCode','httpStatus','retryAfterSeconds','retryAfterHttpDate','rateLimitResetValue','xRateLimitResetValue'):self.assertIsNone(meta[key])
 def test_code_inside_credentials_or_url_is_not_provider_code(self):
  for value in ('https://host/TOOMANYREQUESTS?token=private','username=TOOMANYREQUESTS password=private','prefixThrottlingExceptionSuffix'):
   self.assertIsNone(p.safe_provider_metadata(value)['errorCode'])
 def test_header_allowlist_drops_credentials_and_unknown_values(self):
  headers={'Retry-After':'4','X-RateLimit-Reset':'1791936000','x-amzn-ErrorType':'ThrottlingException',
   'Authorization':'Bearer CANARY','Set-Cookie':'CANARY','WWW-Authenticate':'token=CANARY','Request-ID':'customer-CANARY'}
  meta=p.safe_provider_metadata(headers=headers,status=429,origin='metadata-http-response')
  self.assertEqual(meta['httpStatus'],429);self.assertEqual(meta['errorCode'],'ThrottlingException');self.assertEqual(meta['retryAfterSeconds'],4)
  self.assertNotIn('CANARY',json.dumps(meta));self.assertNotIn('Authorization',json.dumps(meta))
 def test_invalid_or_unbounded_retry_values_are_not_retained(self):
  for value in ('-1','0.5','1e20','9'*100,'2147483648','secret CANARY'):
   meta=p.safe_provider_metadata(headers={'Retry-After':value,'RateLimit-Reset':value,'X-RateLimit-Reset':value})
   self.assertIsNone(meta['retryAfterSeconds']);self.assertIsNone(meta['rateLimitResetValue']);self.assertIsNone(meta['xRateLimitResetValue'])
   self.assertNotIn('CANARY',json.dumps(meta))
 def test_canonical_http_date_retained_but_not_used_to_change_retry(self):
  meta=p.safe_provider_metadata(headers={'Retry-After':'Wed, 21 Oct 2015 07:28:00 GMT'})
  self.assertEqual(meta['retryAfterHttpDate'],'Wed, 21 Oct 2015 07:28:00 GMT');self.assertIsNone(meta['retryAfterSeconds'])
  errors,calls,waits,invoke=MetadataThrottling().scenario([429,200],'Wed, 21 Oct 2015 07:28:00 GMT')
  with self.assertRaises(Exception):invoke()
  self.assertFalse(waits);self.assertTrue(errors[0].fp.closed)
 def test_http_failure_preserves_primary_and_closes_response(self):
  errors,calls,waits,invoke=MetadataThrottling().scenario([403,200],'5')
  with self.assertRaises(Exception) as caught:invoke()
  self.assertIs(caught.exception,errors[0]);self.assertEqual(errors[0].safe_registry_diagnostic['httpStatus'],403)
  self.assertEqual(errors[0].safe_registry_diagnostic['retryAfterSeconds'],5)
  self.assertFalse(waits);self.assertTrue(errors[0].fp.closed)
 def test_failed_pull_still_has_one_attempt_and_unsettled_intent(self):
  row=json.loads((R/'image-pins.json').read_bytes())[0];calls=[]
  payload=b'TOOMANYREQUESTS: rate exceeded';error=p.command_failure(['docker','pull',row['mirror']],1,payload,len(payload))
  def run(args,required=True):
   calls.append(args)
   if args[1]=='pull':raise error
   return None
  receipt={'createdTags':[],'write':lambda:None}
  with self.assertRaises(p.DockerCommandFailure) as caught:p.mutate(['docker','pull',row['mirror']],row['mirror'],row,run,receipt)
  self.assertIs(caught.exception,error);self.assertEqual(sum(a[1]=='pull' for a in calls),1)
  self.assertFalse(receipt['createdTags'][0]['commandSettled']);self.assertTrue(receipt['createdTags'][0].get('unresolvedLease'))

class SafeMetadataFailureCustody(unittest.TestCase):
 def test_unicode_code_lookalikes_remain_unknown_without_raw_echo(self):
  for code in ('ThrottlİngException','ThrottlıngException','MANIFEſT_UNKNOWN'):
   with self.subTest(code=code):
    meta=p.safe_provider_metadata('Error response from daemon: '+code+': CANARY')
    self.assertIsNone(meta['errorCode']);self.assertNotIn('CANARY',json.dumps(meta));self.assertNotIn(code,json.dumps(meta))
 def test_legitimate_ascii_code_case_is_preserved(self):
  for code,expected in (('tHrOtTlInGeXcEpTiOn','ThrottlingException'),('manifest_unknown','MANIFEST_UNKNOWN'),('TOOMANYREQUESTS','TOOMANYREQUESTS')):
   self.assertEqual(p.safe_provider_metadata('Error response from daemon: '+code+': detail')['errorCode'],expected)
 def test_extraction_and_note_failures_keep_primary_and_close_response(self):
  import io,urllib.error
  from unittest.mock import patch
  class Error(urllib.error.HTTPError):
   def add_note(self,note):raise RuntimeError('note failure')
  error=Error('https://host/metadata',403,'primary',{},io.BytesIO(b''))
  class Opener:
   def open(self,request,timeout):raise error
  with patch.object(p.urllib.request,'build_opener',return_value=Opener()),patch.object(p,'safe_provider_metadata',side_effect=ValueError('extraction failure')):
   with self.assertRaises(urllib.error.HTTPError) as caught:p.fetch('https://host/metadata')
  self.assertIs(caught.exception,error);self.assertTrue(error.fp.closed)
 def main_failure(self,kind):
  import io,os,tempfile,urllib.error
  from unittest.mock import patch
  class Error(urllib.error.HTTPError):
   def add_note(self,note):raise RuntimeError('note failure')
  class GetterError(Error):
   @property
   def safe_registry_diagnostic(self):raise ValueError('getter failure')
  error=(GetterError if kind=='getter' else Error)('https://host/metadata',403,'primary',{},io.BytesIO(b''))
  class Opener:
   def open(self,request,timeout):raise error
  calls=[];original=p.atomic_save
  def save(path,data):
   calls.append(True)
   if kind=='save' and len(calls)>1:raise OSError('receipt failure')
   return original(path,data)
  def preload(rows,run,fetch,receipt):return fetch('https://host/metadata')
  with tempfile.TemporaryDirectory(prefix='quotation-safe-metadata-failure-') as directory:
   with patch.dict(os.environ,{'RUNNER_TEMP':directory,'TESTCONTAINERS_RYUK_CONTAINER_IMAGE':p.RYUK}),patch.object(sys,'argv',['preload_images.py']),patch.object(p,'preload',side_effect=preload),patch.object(p.urllib.request,'build_opener',return_value=Opener()),patch.object(p,'atomic_save',side_effect=save),patch.object(p,'run_docker_cli') as docker:
    with self.assertRaises(urllib.error.HTTPError) as caught:p.main()
    self.assertIs(caught.exception,error);self.assertTrue(error.fp.closed);docker.assert_not_called()
   self.assertEqual(json.loads((Path(directory)/'quotation-registry-preload.json').read_bytes())['createdTags'],[])
  return calls
 def test_getter_and_note_failures_keep_main_primary_and_close_response(self):
  self.assertGreaterEqual(len(self.main_failure('getter')),2)
 def test_permanent_save_and_note_failures_keep_main_primary_and_close_response(self):
  self.assertEqual(len(self.main_failure('save')),3)
 def test_extraction_failure_keeps_existing_429_retry_and_response_close(self):
  from unittest.mock import patch
  errors,calls,waits,invoke=MetadataThrottling().scenario([429,200],'4')
  with patch.object(p,'safe_provider_metadata',side_effect=ValueError('extraction failure')):
   self.assertEqual(invoke(),b'verified-response')
  self.assertEqual(waits,[4]);self.assertTrue(errors[0].fp.closed)

class LateTerminalIdentityRepair(unittest.TestCase):
 def release_fixture_child(self,child,polls):
  if not child.stdin.closed:
   self.assertTrue(polls);self.assertIsNone(polls[0])
   child.stdin.write(b'x');child.stdin.flush();child.stdin.close()
 def settle_fixture_children(self,records,deadline,primary):
  import time
  errors=[]
  for record in records:
   child=record['child']
   for name in ('stdin','stdout','stderr'):
    try:getattr(child,name).close()
    except BaseException as error:errors.append(error)
   try:
    retained_wait=record['wait'] or child.wait
    retained_wait(timeout=max(0,deadline-time.monotonic()))
    if child.poll() is None:raise RuntimeError('Fixture child exit remains unproved')
   except BaseException as error:errors.append(error)
  if errors:
   if primary is None:raise BaseExceptionGroup('Late-terminal test cleanup failed',errors)
   for error in errors:
    try:primary.add_note('Secondary late-terminal test cleanup failure: '+type(error).__name__)
    except BaseException:
     try:sys.stderr.write('Late-terminal fixture cleanup unresolved\n')
     except BaseException:pass
  return errors
 @unittest.skipUnless(__import__('os').name=='posix','Actual late-terminal join requires Linux hosted runner')
 def test_real_same_child_late_join_preserves_terminal_status_and_both_pipes(self):
  from unittest.mock import patch
  import subprocess,time
  original=p.subprocess.Popen;children=[];waits=[];polls=[];primary=None
  deadline=time.monotonic()+6
  script="import select,sys,time;ready,_,_=select.select([sys.stdin.buffer],[],[],max(0,"+repr(deadline-2)+"-time.monotonic()));released=bool(ready) and sys.stdin.buffer.read(1)==b'x';sys.stdout.buffer.write(b'out' if released else b'');sys.stderr.buffer.write(b'error' if released else b'');sys.exit(7 if released else 9)"
  def launch(args,**kwargs):
   child=original([sys.executable,'-c',script],stdin=subprocess.PIPE,**kwargs)
   record={'child':child,'wait':None};children.append(record)
   retained_wait=child.wait;retained_poll=child.poll
   record['wait']=retained_wait
   def poll():value=retained_poll();polls.append(value);return value
   def wait(timeout):
    waits.append(timeout)
    self.release_fixture_child(child,polls)
    return retained_wait(timeout=timeout)
   child.wait=wait;child.poll=poll;return child
  try:
   with patch.object(p.subprocess,'Popen',side_effect=launch),patch.object(p,'observe_cli_identity',side_effect=FileNotFoundError('Forced unavailable executable observation')):
    result,total=p.run_docker_cli(['docker','image','inspect',p.RYUK],6,hard_deadline=deadline)
   self.assertEqual(len(children),1);self.assertEqual(result.returncode,7);self.assertEqual(result.stdout,b'out');self.assertEqual(result.stderr,b'error');self.assertEqual(total,5)
   self.assertTrue(polls);self.assertIsNone(polls[0]);self.assertTrue(waits);self.assertTrue(0<waits[0]<=1)
   resource=result.owned_cli_resource
   self.assertTrue(resource['identityUnavailableAfterExit']);self.assertFalse(resource['identityObserved']);self.assertIsNone(resource['startTicks']);self.assertIsNone(resource['executable'])
   self.assertTrue(resource['exitConfirmed']);self.assertFalse(resource['unresolvedCustody'])
  except BaseException as error:primary=error;raise
  finally:
   self.settle_fixture_children(children,deadline,primary)
  self.assertTrue(all(record['child'].poll()==7 and record['child'].stdin.closed and record['child'].stdout.closed and record['child'].stderr.closed for record in children))
 def fixture_control(self,write_failure=False,close_failure=False,join_failure=False):
  import types
  events=[];primary=OSError('fixture release failure')
  class Pipe:
   closed=False
   def __init__(self,name):self.name=name
   def write(self,value):
    events.append('release')
    if write_failure:raise primary
   def flush(self):pass
   def close(self):
    events.append(self.name)
    if close_failure:raise OSError('fixture close failure')
    self.closed=True
  child=types.SimpleNamespace(stdin=Pipe('stdin'),stdout=Pipe('stdout'),stderr=Pipe('stderr'),returncode=None)
  def wait(timeout):
   events.append('join')
   if join_failure:raise RuntimeError('fixture join failure')
   child.returncode=9;return 9
  child.wait=wait;child.poll=lambda:child.returncode
  return child,{'child':child,'wait':wait},events,primary
 def test_release_failure_preserves_primary_and_finally_closes_and_joins_fixture(self):
  import time
  child,record,events,primary=self.fixture_control(write_failure=True)
  with self.assertRaises(OSError) as caught:
   try:self.release_fixture_child(child,[None])
   except BaseException as error:
    try:raise
    finally:self.settle_fixture_children([record],time.monotonic()+1,error)
  self.assertIs(caught.exception,primary);self.assertEqual(events,['release','stdin','stdout','stderr','join'])
  self.assertTrue(child.stdin.closed and child.stdout.closed and child.stderr.closed);self.assertEqual(child.poll(),9)
 def test_parent_failure_survives_all_close_join_and_note_failures_with_unresolved_report(self):
  import io,time
  from unittest.mock import patch
  class Primary(OSError):
   def add_note(self,note):raise RuntimeError('fixture note failure')
  primary=Primary('actual parent failure');child,record,events,unused=self.fixture_control(close_failure=True,join_failure=True)
  report=io.StringIO();errors=[]
  with patch.object(sys,'stderr',report),self.assertRaises(Primary) as caught:
   try:raise primary
   finally:errors.extend(self.settle_fixture_children([record],time.monotonic()+1,primary))
  self.assertIs(caught.exception,primary);self.assertEqual(events,['stdin','stdout','stderr','join']);self.assertEqual(len(errors),4)
  self.assertIn('cleanup unresolved',report.getvalue());self.assertIsNone(child.poll())
 def test_cleanup_failure_without_parent_failure_is_reported_as_group(self):
  import time
  child,record,events,unused=self.fixture_control(close_failure=True)
  with self.assertRaises(BaseExceptionGroup) as caught:self.settle_fixture_children([record],time.monotonic()+1,None)
  self.assertEqual(len(caught.exception.exceptions),3);self.assertEqual(events,['stdin','stdout','stderr','join']);self.assertEqual(child.poll(),9)
 def invoke(self,mode='natural',code=0):
  import subprocess,selectors,types
  from unittest.mock import patch
  primary=FileNotFoundError('Owned executable observation disappeared')
  clock=[0];observations=[]
  def now():
   observations.append(True)
   return 3 if mode=='expired-before' and len(observations)>1 else clock[0]
  class Pipe:
   def __init__(self,fd):self.fd=fd;self.closed=False
   def fileno(self):return self.fd
   def close(self):self.closed=True
  class Process:
   pid=41;returncode=None
   def __init__(self):self.stdout=Pipe(11);self.stderr=Pipe(12);self.waits=[];self.signals=[]
   def poll(self):return self.returncode
   def wait(self,timeout):
    self.waits.append(timeout)
    if mode=='live':raise subprocess.TimeoutExpired(['docker','image','inspect'],timeout)
    self.returncode=code
    if mode=='expired-during':clock[0]=3.1
    return code
   def terminate(self):self.signals.append('terminate');raise AssertionError('Unobserved executable must not be signalled')
   def kill(self):self.signals.append('kill');raise AssertionError('Unobserved executable must not be signalled')
  class Selector:
   def __init__(self):self.keys={};self.closed=False
   def register(self,stream,event,name):self.keys[stream.fd]=types.SimpleNamespace(fd=stream.fd,fileobj=stream,data=name)
   def get_map(self):return self.keys
   def select(self,timeout):return [(key,None) for key in list(self.keys.values())]
   def unregister(self,stream):self.keys.pop(stream.fd)
   def close(self):self.closed=True
  child=Process();selector=Selector();chunks={11:[b'out',b''],12:[b'denied: CANARY',b'']}
  def read(fd,size):return chunks[fd].pop(0)
  with patch('os.name','posix'),patch.object(p.subprocess,'Popen',return_value=child) as launch,patch.object(p,'observe_cli_identity',side_effect=primary),patch.object(selectors,'DefaultSelector',return_value=selector),patch('os.set_blocking'),patch('os.read',side_effect=read),patch.object(p.time,'monotonic',side_effect=now):
   if mode in ('live','expired-before','expired-during'):
    with self.assertRaises(FileNotFoundError) as caught:p.run_docker_cli(['docker','image','inspect',p.RYUK],6,hard_deadline=6)
    self.assertIs(caught.exception,primary);result=None;total=None;resource=primary.owned_cli_resource
   else:
    result,total=p.run_docker_cli(['docker','image','inspect',p.RYUK],6,hard_deadline=6);resource=result.owned_cli_resource
   launch.assert_called_once()
  self.assertEqual(child.signals,[]);self.assertTrue(child.stdout.closed and child.stderr.closed)
  self.assertFalse(resource['identityObserved']);self.assertIsNone(resource['startTicks']);self.assertIsNone(resource['executable'])
  self.assertEqual(resource['hardDeadlineMonotonic'],6);self.assertEqual(resource['workDeadlineMonotonic'],3);self.assertEqual(resource['recoveryReserveSeconds'],3)
  return child,selector,resource,result,total
 def test_same_owned_child_bounded_terminal_join_reuses_fast_exit_classification(self):
  child,selector,resource,result,total=self.invoke()
  self.assertEqual(result.returncode,0);self.assertEqual(result.stdout,b'out');self.assertEqual(result.stderr,b'denied: CANARY');self.assertEqual(total,len(result.stderr))
  self.assertTrue(resource['identityUnavailableAfterExit']);self.assertTrue(resource['exitConfirmed']);self.assertFalse(resource['identityDrift']);self.assertFalse(resource['unresolvedCustody'])
  self.assertEqual(resource['executionOutcome'],'completed');self.assertTrue(selector.closed);self.assertLessEqual(child.waits[0],1)
 def test_nonzero_terminal_status_and_capture_do_not_become_required_or_optional_success(self):
  import os,tempfile
  from unittest.mock import patch
  child,selector,resource,result,total=self.invoke(code=7)
  self.assertEqual(result.returncode,7);self.assertEqual(result.stdout,b'out');self.assertEqual(result.stderr,b'denied: CANARY');self.assertEqual(total,len(result.stderr))
  self.assertEqual(resource['terminalReturnCode'],7)
  for required in (False,True):
   with self.subTest(required=required),tempfile.TemporaryDirectory(prefix='quotation-late-terminal-control-') as directory:
    def preload(rows,run,fetch,receipt):return run(['docker','image','inspect',p.RYUK],required)
    with patch.dict(os.environ,{'RUNNER_TEMP':directory,'TESTCONTAINERS_RYUK_CONTAINER_IMAGE':p.RYUK}),patch.object(sys,'argv',['preload_images.py']),patch.object(p,'preload',side_effect=preload),patch.object(p,'run_docker_cli',return_value=(result,total)):
     with self.assertRaises(p.DockerCommandFailure) as caught:p.main()
     self.assertEqual(caught.exception.diagnostic['returnCode'],7);self.assertFalse(caught.exception.diagnostic['rawDisclosed'])
 def test_live_unknown_child_is_not_accepted_or_signalled(self):
  child,selector,resource,result,total=self.invoke('live')
  self.assertIsNone(result);self.assertFalse(resource['exitConfirmed']);self.assertTrue(resource['unresolvedCustody']);self.assertIsNone(resource['terminalReturnCode'])
 def test_expired_work_deadline_before_join_never_accepts_preparation(self):
  child,selector,resource,result,total=self.invoke('expired-before')
  self.assertIsNone(result);self.assertTrue(resource['unresolvedCustody'])
 def test_work_deadline_expiry_during_natural_join_never_accepts_preparation(self):
  child,selector,resource,result,total=self.invoke('expired-during')
  self.assertIsNone(result);self.assertTrue(resource['unresolvedCustody']);self.assertEqual(resource['terminalReturnCode'],0)


class MetadataRequestContext(unittest.TestCase):
 def test_actual_approved_requests_join_alias_host_and_stage_without_secrets(self):
  import io,urllib.error
  from unittest.mock import patch
  rows=json.loads((R/'image-pins.json').read_bytes())
  expected=[('postgres:18-alpine','public.ecr.aws','docker/library/postgres','library/postgres','18-alpine'),
   ('redis:7-alpine','public.ecr.aws','docker/library/redis','library/redis','7-alpine'),
   ('ryuk:0.14.0','ghcr.io','testcontainers/ryuk','testcontainers/ryuk','0.14.0')]
  for row,(alias,registry,repository,hub,tag) in zip(rows,expected):
   token=('https://public.ecr.aws/token/?service=public.ecr.aws&scope=repository:'+repository+':pull' if registry=='public.ecr.aws' else 'https://ghcr.io/token?service=ghcr.io&scope=repository:'+repository+':pull')
   requests=[(token,registry,'token'),('https://hub.docker.com/v2/repositories/'+hub+'/tags/'+tag,'hub.docker.com','provenance'),
    ('https://'+registry+'/v2/'+repository+'/manifests/'+row['indexDigest'],registry,'index'),
    ('https://'+registry+'/v2/'+repository+'/manifests/'+row['manifestDigest'],registry,'manifest')]
   for url,host,stage in requests:
    with self.subTest(alias=alias,stage=stage):
     error=urllib.error.HTTPError(url,403,'CANARY',{'Authorization':'Bearer CANARY','Set-Cookie':'CANARY'},io.BytesIO(b'CANARY'))
     class Opener:
      def open(self,request,timeout):raise error
     with patch.object(p.urllib.request,'build_opener',return_value=Opener()):
      with self.assertRaises(urllib.error.HTTPError) as caught:p.fetch(url,'CANARY')
     self.assertIs(caught.exception,error);self.assertTrue(error.fp.closed)
     self.assertEqual(error.safe_registry_diagnostic.get('requestContext'),{'imageAlias':alias,'hostname':host,'requestStage':stage,'localAttempt':1})
     encoded=json.dumps(error.safe_registry_diagnostic)
     self.assertNotIn('CANARY',encoded);self.assertNotIn('https://',encoded);self.assertNotIn('scope=',encoded)
 def test_unapproved_or_decorated_url_remains_unknown_and_does_not_leak(self):
  import io,urllib.error
  from unittest.mock import patch
  row=json.loads((R/'image-pins.json').read_bytes())[0]
  approved='https://public.ecr.aws/v2/docker/library/postgres/manifests/'+row['indexDigest']
  for url in [approved+'?token=CANARY',approved+'#CANARY',approved.replace('public.ecr.aws','CANARY@public.ecr.aws'),
   approved.replace('public.ecr.aws','public.ecr.aws:443'),approved.replace(row['indexDigest'],'sha256:'+'0'*64),'https://CANARY.invalid/path']:
   with self.subTest(url=url):
    error=urllib.error.HTTPError(url,403,'CANARY',{},io.BytesIO(b'CANARY'))
    class Opener:
     def open(self,request,timeout):raise error
    with patch.object(p.urllib.request,'build_opener',return_value=Opener()):
     with self.assertRaises(urllib.error.HTTPError):p.fetch(url,'CANARY')
    self.assertEqual(error.safe_registry_diagnostic.get('requestContext'),{'imageAlias':None,'hostname':None,'requestStage':None,'localAttempt':1})
    self.assertNotIn('CANARY',json.dumps(error.safe_registry_diagnostic));self.assertTrue(error.fp.closed)
  self.assertNotIn('requestContext',p.safe_provider_metadata(status=429))
  self.assertNotIn('requestContext',p.command_failure(['docker','pull',p.RYUK],1,b'unknown',7).diagnostic['safeProviderMetadata'])
 def test_local_retry_ordinals_preserve_three_attempts_primary_and_response_close(self):
  import io,urllib.error
  from unittest.mock import patch
  row=json.loads((R/'image-pins.json').read_bytes())[0]
  url='https://public.ecr.aws/v2/docker/library/postgres/manifests/'+row['indexDigest']
  errors=[urllib.error.HTTPError(url,429,'CANARY',{},io.BytesIO(b'CANARY')) for _ in range(3)]
  calls=[]
  class Opener:
   def open(self,request,timeout):
    calls.append(request.full_url);raise errors[len(calls)-1]
  with patch.object(p.urllib.request,'build_opener',return_value=Opener()),patch.object(p.time,'monotonic',return_value=0),patch.object(p.time,'sleep') as sleep:
   with self.assertRaises(urllib.error.HTTPError) as caught:p.fetch(url,deadline=30)
  self.assertIs(caught.exception,errors[-1]);self.assertEqual(calls,[url]*3)
  self.assertEqual([c.args[0] for c in sleep.call_args_list],[2,4])
  for attempt,error in enumerate(errors,1):
   self.assertTrue(error.fp.closed);self.assertEqual(error.safe_registry_diagnostic['requestContext']['localAttempt'],attempt)
   self.assertIsNone(error.safe_registry_diagnostic['retryAfterSeconds']);self.assertEqual(error.safe_registry_diagnostic['rateLimitNature'],'unclassified')
 def test_main_receipt_retains_actual_context_before_any_docker_mutation(self):
  import io,os,tempfile,urllib.error
  from unittest.mock import patch
  row=json.loads((R/'image-pins.json').read_bytes())[0]
  url='https://public.ecr.aws/v2/docker/library/postgres/manifests/'+row['indexDigest']
  error=urllib.error.HTTPError(url,403,'CANARY',{'Authorization':'CANARY'},io.BytesIO(b'CANARY'))
  class Opener:
   def open(self,request,timeout):raise error
  def preload(rows,run,network,receipt):return network(url,'CANARY')
  with tempfile.TemporaryDirectory(prefix='quotation-request-context-control-') as directory:
   with patch.dict(os.environ,{'RUNNER_TEMP':directory,'TESTCONTAINERS_RYUK_CONTAINER_IMAGE':p.RYUK}),patch.object(sys,'argv',['preload_images.py']),patch.object(p,'preload',side_effect=preload),patch.object(p.urllib.request,'build_opener',return_value=Opener()),patch.object(p,'run_docker_cli') as docker:
    with self.assertRaises(urllib.error.HTTPError) as caught:p.main()
    self.assertIs(caught.exception,error);docker.assert_not_called()
   receipt=json.loads((Path(directory)/'quotation-registry-preload.json').read_bytes())
   self.assertEqual(receipt['createdTags'],[]);self.assertFalse(receipt['containersStarted'])
   self.assertEqual(receipt['metadataFailureDiagnostic']['requestContext'],{'imageAlias':'postgres:18-alpine','hostname':'public.ecr.aws','requestStage':'index','localAttempt':1})
   self.assertNotIn('CANARY',json.dumps(receipt));self.assertTrue(error.fp.closed)
 def test_context_extraction_failure_preserves_primary_metadata_and_close(self):
  import io,urllib.error
  from unittest.mock import patch
  error=urllib.error.HTTPError('https://host/metadata',403,'CANARY',{},io.BytesIO(b'CANARY'))
  class Opener:
   def open(self,request,timeout):raise error
  with patch.object(p.urllib.request,'build_opener',return_value=Opener()),patch.object(p,'metadata_request_context',side_effect=ValueError('CANARY')):
   with self.assertRaises(urllib.error.HTTPError) as caught:p.fetch('https://host/metadata')
  self.assertIs(caught.exception,error);self.assertTrue(error.fp.closed)
  self.assertEqual(error.safe_registry_diagnostic['httpStatus'],403);self.assertEqual(error.safe_registry_diagnostic['requestContext'],{'imageAlias':None,'hostname':None,'requestStage':None,'localAttempt':1})
  self.assertNotIn('CANARY',json.dumps(error.safe_registry_diagnostic));self.assertNotIn('CANARY',' '.join(error.__notes__))

if __name__=='__main__':unittest.main()
