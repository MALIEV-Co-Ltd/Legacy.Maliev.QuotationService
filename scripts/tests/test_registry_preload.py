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
class RegistryBindings(unittest.TestCase):
 def setUp(self):self.rows=json.loads((R/'image-pins.json').read_bytes())
 def test_closed_three_sources(self):
  self.assertEqual(len(self.rows),3)
  for row in self.rows:p.binding(row)
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
if __name__=='__main__':unittest.main()
