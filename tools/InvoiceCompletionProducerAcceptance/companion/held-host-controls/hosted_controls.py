"""Actual Linux acquisition-seam controls; no .NET/producer/eight-host admission."""
import hashlib
import json
import os
from pathlib import Path
import signal
import socket
import stat
import sys
import tempfile
import time
import types
from dataclasses import dataclass
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
COMPANION = HERE.parent
PINS = {
    'held_host_lifetime.py': 'bed2886e551809beeb24517722d1f8101ad66c0b08399672f5e7011cc1ba6a07',
    'owned_front_host.py': '18e2626756b55f0e3921e4c5bd2d5f4d53844d01a2c22a181d981dcdd71e1699',
    'owned_normal_hosts.py': 'e5fee39ac7ced3b1304698c734abfd192660a29e94e7e1dcb366b34f15205988',
    'held_linux_exit.py': 'e7c809d0a1a9c8e8673ed5ef299dd530774d68a48196a739a90f0eaaf5e11515',
    'hosted_companion_resources.py': '809a41aa28d5b11f45a60029c7590014c3f2b25dc225e53058cedfc26e953ee1',
    'tls_listener_admission.py': 'f9c84ed4a8bf6e24614a725838adc36aa0870b997c648d30677608e429a8f5cf',
    'regular_owned_files.py': '3799a862fd53cd240bb4880af3444df1957a7293bd3c473419e7b2f28ced5029',
}
CHILD_SHA256='857bd23e42ad36a5a54fed8bc0b157beb39497df9ece05ebfe34b656e2985091'


@dataclass(slots=True)
class HeldInput:
    fd: int = -1
    closed: bool = False
    uncertain: bool = False
    raw: object = None


@dataclass(slots=True)
class ScopeCustody:
    scope: object = None
    normal: object = None


@dataclass(slots=True)
class PipeCustody:
    scope: object
    pipe: object = None
    initialized: bool = False
    uncertain: bool = False


class CallerRegistry:
    """Module-owned custody outlives setup exceptions and the sanitized handler."""
    def __init__(self):
        self.scopes=[]; self.pipes=[]; self.inputs=[]; self.modules=[]
        self.directories=[]; self.snapshots=[]; self.cleanup_failed=False
    def directory(self, label):
        path=Path(tempfile.mkdtemp(prefix='held-'+label+'-'))
        self.directories.append(path)
        return path
    def read(self,path,cap,expected=None):
        record=HeldInput(); self.inputs.append(record)
        record.fd=os.open(path,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK)
        before=os.fstat(record.fd)
        require(stat.S_ISREG(before.st_mode) and before.st_nlink==1 and 0<before.st_size<=cap)
        pieces=[]; size=0
        while True:
            block=os.read(record.fd,min(65536,cap+1-size))
            if not block: break
            size+=len(block); require(size<=cap); pieces.append(block)
        after=os.fstat(record.fd)
        require((before.st_dev,before.st_ino,before.st_size,before.st_mtime_ns)==
                (after.st_dev,after.st_ino,after.st_size,after.st_mtime_ns) and size==before.st_size)
        record.raw=b''.join(pieces)
        if expected is not None: require(hashlib.sha256(record.raw).hexdigest()==expected)
        return record.raw
    def snapshot(self,directory,name,raw):
        require('/' not in name and '\\' not in name)
        path=directory/name
        record=HeldInput(); self.inputs.append(record)
        record.fd=os.open(path,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o400)
        self.snapshots.append(path)
        offset=0
        while offset<len(raw):
            count=os.write(record.fd,raw[offset:]); require(count>0); offset+=count
        # The exclusive snapshot is retained alongside its original source fd.
        return path
    def new_scope(self,lifetime):
        record=ScopeCustody(); self.scopes.append(record)
        record.scope=lifetime.HostLifetime(cleanup_seconds=5)
        return record
    def new_pipe(self,scope,original_front):
        record=PipeCustody(scope); self.pipes.append(record)
        record.pipe=original_front.PublicBootstrapPipe.__new__(original_front.PublicBootstrapPipe)
        # Root the exact object BEFORE its original descriptor constructor runs.
        original_front.PublicBootstrapPipe.__init__(record.pipe)
        record.initialized=True
        return record.pipe
    def pending(self):
        for entry in self.scopes:
            s=entry.scope
            if s is None: continue
            if s.birth_uncertain or any(row.birth_attempted and not row.reaped for row in s.children): return True
            if s.parent_pidfd is not None and not s.parent_pidfd.closed: return True
            if any(row.pidfd is not None and not row.pidfd.closed for row in s.children): return True
            normal=entry.normal or s.normal
            if normal is not None and normal.timer_owned: return True
        return any(not entry.initialized or entry.uncertain or not entry.pipe.closed for entry in self.pipes)
    def cleanup(self):
        # Independent attempts retain every original object and error state.
        for entry in reversed(self.scopes):
            if entry.scope is None: continue
            normal=entry.normal or entry.scope.normal
            if normal is not None:
                try: normal.close()
                except BaseException: pass
            try: entry.scope.close()
            except BaseException: pass  # Expected faults remain in the actual scope.
            # Scope settlement does not dispose the original owner's timer.
            # Release only this registered owner's exact timer after all of its
            # original children are actually reaped; never clear a foreign timer.
            if normal is not None and normal.timer_owned:
                s=entry.scope
                if not s.birth_uncertain and all(not row.birth_attempted or row.reaped for row in s.children):
                    try:
                        with s.fence():
                            require(signal.getsignal(signal.SIGALRM)==normal.expire)
                            normal.release_timer()
                    except BaseException: self.cleanup_failed=True
        for entry in reversed(self.pipes):
            s=entry.scope
            if not entry.initialized:
                self.cleanup_failed=True; continue  # Opaque constructor uncertainty.
            if entry.pipe.closed: continue
            if s.birth_uncertain or any(row.birth_attempted and not row.reaped for row in s.children): continue
            if s.pipe is entry.pipe and s.bootstrap_close_uncertain:
                entry.uncertain=True; continue
            if entry.uncertain: continue
            try: entry.pipe.close()
            except BaseException: entry.uncertain=True; self.cleanup_failed=True
        if self.pending(): return False
        try:
            require(signal.getsignal(signal.SIGALRM)==signal.SIG_DFL
                    and signal.getitimer(signal.ITIMER_REAL)==(0.,0.))
        except BaseException:
            self.cleanup_failed=True; return False
        for entry in self.inputs:
            if entry.fd<0 or entry.closed: continue
            if entry.uncertain: self.cleanup_failed=True; continue
            try: os.close(entry.fd); entry.closed=True
            except BaseException: entry.uncertain=True; self.cleanup_failed=True
        if any(entry.uncertain or entry.fd>=0 and not entry.closed for entry in self.inputs): return False
        for path in reversed(self.snapshots):
            try: path.unlink(missing_ok=True)
            except BaseException: self.cleanup_failed=True
        for path in reversed(self.directories):
            # Only exact created private directories/files; no recursive delete.
            try:
                for child in path.iterdir():
                    require(child.is_file() and not child.is_symlink()); child.unlink()
                path.rmdir()
            except BaseException: self.cleanup_failed=True
        if not self.cleanup_failed:
            self.snapshots.clear(); self.directories.clear()
        return not self.cleanup_failed


REGISTRY=CallerRegistry()
FIXTURE_PATH=None
CURRENT_STAGE='qualification'
CURRENT_CONTROL=None


def stage(value):
    require(value in ('qualification','parent-admission','child-owner-binding','bootstrap-acquisition',
                     'child-acquisition','bootstrap-write-close','owned-listener','fault','control-cleanup','caller-cleanup'))
    global CURRENT_STAGE
    CURRENT_STAGE=value


def execute_held_module(name,raw,directory):
    require(name not in sys.modules)
    path=REGISTRY.snapshot(directory,name+'.py',raw)
    module=types.ModuleType(name); module.__file__=str(path)
    REGISTRY.modules.append(module); sys.modules[name]=module
    exec(compile(raw,str(path),'exec'),module.__dict__)
    return module


def close_bootstrap_writer(scope,pipe):
    stage('bootstrap-write-close')
    entries=[entry for entry in REGISTRY.pipes if entry.pipe is pipe and entry.scope is scope]
    require(len(entries)==1 and entries[0].initialized and not entries[0].uncertain)
    with scope.fence():
        try:
            # Outcome is uncertain until successful syscall AND original field
            # handoff finish. An exception never permits reuse of the raw integer.
            entries[0].uncertain=True
            scope.bootstrap_close_uncertain=True
            os.close(pipe.write_fd)
            pipe.write_fd=None
            entries[0].uncertain=False
            scope.bootstrap_close_uncertain=False
        except BaseException:
            raise RuntimeError('Synthetic bootstrap close refused') from None


def require(value):
    if not value: raise RuntimeError('Synthetic lifetime control refused')


def qualify():
    stage('qualification')
    require(sys.platform == 'linux' and os.environ.get('GITHUB_ACTIONS') == 'true')
    require(os.environ.get('GITHUB_RUN_ID','').isdecimal() and os.environ.get('GITHUB_RUN_ATTEMPT','').isdecimal())
    require(signal.getsignal(signal.SIGALRM) == signal.SIG_DFL and signal.getitimer(signal.ITIMER_REAL) == (0.,0.))
    raw_sources={name:REGISTRY.read(COMPANION/name,65536,digest) for name,digest in PINS.items()}
    fixture_bytes=REGISTRY.read(HERE/'native_child.py',65536,CHILD_SHA256)
    git_directory=Path('.git')
    require(git_directory.is_dir() and not git_directory.is_symlink())
    actual_head=REGISTRY.read(git_directory/'HEAD',1024).decode('ascii').strip()
    require(len(actual_head)==40 and all(c in '0123456789abcdef' for c in actual_head))
    require(actual_head==os.environ.get('REVIEWED_SOURCE_HEAD'))
    directory=REGISTRY.directory('source')
    # Execute the SAME held qualified bytes, in dependency order. No mutable
    # companion sys.path import or pathname reopen supplies executable code.
    order=('hosted_companion_resources','regular_owned_files','tls_listener_admission',
           'owned_normal_hosts','owned_front_host','held_linux_exit','held_host_lifetime')
    for name in order:
        execute_held_module(name,raw_sources[name+'.py'],directory)
    global FIXTURE_PATH
    FIXTURE_PATH=REGISTRY.snapshot(directory,'native_child.py',fixture_bytes)
    return actual_head


def unused_port():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        probe.bind(('127.0.0.1',0))
        return probe.getsockname()[1]


def listener(scope, owner, process, port):
    stage('owned-listener')
    import hosted_companion_resources as kernel
    import tls_listener_admission as original_listener
    end = time.monotonic() + 5
    while time.monotonic() < end:
        scope.observe_original(owner)
        try:
            tables = [kernel.bounded_file(path,2*1024**2).decode('ascii') for path in ('/proc/net/tcp','/proc/net/tcp6')]
            inode = kernel.listening_inode(tables,'127.0.0.1',port)
            links=[]
            for index,fd in enumerate(Path('/proc/'+str(process.pid)+'/fd').iterdir()):
                require(index<4096)
                try: links.append(os.readlink(fd))
                except FileNotFoundError: continue
            original_listener.require_listener_identity(scope.driver.generation(process.pid)[0],
                                                        scope._row(process).identity.start_ticks,inode,links)
            original_listener.require_only_owned_listener(tables,links,inode)
            scope.observe_original(owner)
            scope.bind_listener(owner,process,inode)
            return inode
        except (OSError,kernel.AdmissionError): time.sleep(.01)
    raise RuntimeError('Synthetic owned listener deadline')


def fixture(directory):
    import held_host_lifetime as lifetime
    entry=REGISTRY.new_scope(lifetime); scope=entry.scope
    stage('parent-admission')
    scope.admit_parent()
    stage('child-owner-binding')
    executable=str(Path(sys.executable).resolve())
    digest=hashlib.sha256(REGISTRY.read(executable,32*1024**2)).hexdigest()
    normal = lifetime.LifetimeNormalHosts(scope,None,{},(),executable,digest,1)
    entry.normal=normal
    return scope,normal


def acquire(scope, normal, directory, front=False):
    import held_host_lifetime as lifetime
    import owned_normal_hosts as original_normal
    import owned_front_host as original_front
    child=FIXTURE_PATH
    require(child is not None)
    port = unused_port()
    stage('bootstrap-acquisition' if front else 'child-acquisition')
    pipe=REGISTRY.new_pipe(scope,original_front) if front else None
    config = {'parentPid':os.getpid(),'port':port,'bootstrapFd':pipe.read_fd if pipe else None,
              'deadline':time.monotonic()+20}
    path = directory/('front.json' if front else 'normal.json')
    with path.open('x',encoding='utf-8') as stream:
        os.chmod(path,0o600)
        json.dump(config,stream)
    environment = {'PATH':os.environ.get('PATH','/usr/bin:/bin'), 'LANG':'C.UTF-8',
                   'PYTHONNOUSERSITE':'1','HELD_SYNTHETIC_CONFIG':str(path)}
    if front:
        # The actual original wrapper is bound. Full front start/profile/.NET
        # admission is deliberately NOT called or claimed by this fixture.
        owner = lifetime.LifetimeFrontHost(scope,None,None,environment,pipe,1)
        stage('child-acquisition')
        process = owner.acquire_child([normal.dotnet,str(child),str(path)],cwd=str(HERE),env=environment,
            stdin=lifetime.subprocess.DEVNULL,stdout=lifetime.subprocess.DEVNULL,stderr=lifetime.subprocess.DEVNULL,
            close_fds=True,pass_fds=(pipe.read_fd,),start_new_session=False)
        owner.process,owner.ticks = process,scope._row(process).identity.start_ticks
        # Physical bootstrap EOF only; this is NOT a File signing frame/receipt.
        require(os.write(pipe.write_fd,b'held-lifetime-synthetic-bootstrap\n') == 34)
        close_bootstrap_writer(scope,pipe); pipe.sealed=True
    else:
        # An unavailable tree is left empty, never fabricated. This ordinary
        # Python acquisition does not call normal.start/source/build admission.
        spec = original_normal.HostSpec('File',str(HERE),os.environ['REVIEWED_SOURCE_HEAD'],'',str(child),
                                        CHILD_SHA256,
                                        '127.0.0.1',port,environment,64*1024**2)
        process = normal.spawn_owned(spec,environment)[1]
        owner = normal
    listener(scope,owner,process,port)
    return owner,process


def physical(scope):
    return {'OriginalChildrenReaped':sum(row.reaped for row in scope.children),
            'OriginalChildPidfdsClosed':all(row.pidfd is not None and row.pidfd.closed for row in scope.children),
            'OriginalParentPidfdClosed':scope.parent_pidfd is not None and scope.parent_pidfd.closed,
            'OriginalBootstrapClosed':scope.pipe is None or scope.bootstrap_closed,
            'ListenerAbsenceVerified':all(row.listener_absence_verified for row in scope.children),
            'OriginalFailureSticky':scope.failed, 'BirthAssociationUnproved':scope.birth_uncertain,
            'OriginalParent':{'BirthIdentityCaptured':scope.parent is not None,
                              'PidfdClosed':scope.parent_pidfd.closed},
            'OriginalChildren':[{'SyntheticOwnerSlot':row.role,'BirthIdentityCaptured':row.identity is not None,
                                 'Reaped':row.reaped,'PidfdClosed':row.pidfd.closed,
                                 'ListenerEnrolled':row.listener is not None,'ListenerAbsenceVerified':row.listener_absence_verified}
                                for row in scope.children],
            'OriginalBootstrapAssociationObserved':scope.bootstrap_binding is not None}


def controls():
    import held_host_lifetime as lifetime
    results=[]
    root=REGISTRY.directory('controls')
    for name in ('normal-front','partial-parent-census','partial-child-postassociation-descriptors','stop-failure-recovery','expiry-fence'):
        global CURRENT_CONTROL
        CURRENT_CONTROL=name
        directory=root/name; directory.mkdir(mode=0o700); REGISTRY.directories.append(directory)
        scope=None
        try:
            if name == 'partial-parent-census':
                scope=REGISTRY.new_scope(lifetime).scope
                witness={'invoked':False}
                def census(parent,deadline):
                    require(parent==os.getpid() and scope.parent_pidfd is not None)
                    scope.driver.verify(scope.parent_pidfd,scope.parent)
                    witness['invoked']=True
                    raise OSError()
                with patch.object(scope.driver,'census',side_effect=census):
                    stage('parent-admission')
                    try: scope.admit_parent()
                    except lifetime.LifetimeRefused: pass
                    else: raise RuntimeError('Parent fault was not refused')
                original=scope.parent_pidfd
                require(witness['invoked'])
                require(original is not None and scope.baseline is None and scope.failed)
                scope.close()
                require(scope.parent_pidfd is original and original.closed and scope.baseline is None)
            else:
                scope,normal=fixture(directory)
                if name == 'partial-child-postassociation-descriptors':
                    witness={'invoked':False}
                    original_descriptors=scope.driver.descriptors
                    def descriptors(pid):
                        if pid != os.getpid():
                            require(len(scope.children)==1 and scope.children[0].process.pid==pid)
                            row=scope.children[0]
                            require(row.identity is not None and row.pidfd is not None)
                            scope.driver.verify(row.pidfd,row.identity)
                            witness['invoked']=True
                            raise OSError()
                        return original_descriptors(pid)
                    with patch.object(scope.driver,'descriptors',side_effect=descriptors):
                        try: acquire(scope,normal,directory)
                        except lifetime.LifetimeRefused: pass
                        else: raise RuntimeError('Descriptor fault was not refused')
                    require(witness['invoked'] and len(scope.children)==1 and scope.children[0].identity is not None and scope.children[0].pidfd is not None)
                    try: normal.close()
                    except lifetime.LifetimeRefused: pass
                    else: raise RuntimeError('Missing listener was not refused')
                    require(scope.children[0].reaped and scope.parent_pidfd.closed)
                    require(not scope.children[0].listener_absence_verified and scope.failed)
                else:
                    acquire(scope,normal,directory)
                    if name == 'normal-front':
                        acquire(scope,normal,directory,True)
                        normal.close(); scope.assert_backend_release()
                    elif name == 'stop-failure-recovery':
                        witness={'invoked':False}
                        def terminate(row,force):
                            require(row is scope.children[0] and force is False)
                            scope.driver.verify(row.pidfd,row.identity)
                            witness['invoked']=True
                            raise OSError()
                        with patch.object(scope.driver,'terminate',side_effect=terminate):
                            try: normal.close()
                            except lifetime.LifetimeRefused: pass
                            else: raise RuntimeError('Stop fault was not refused')
                        require(witness['invoked'] and not scope.children[0].reaped and not scope.parent_pidfd.closed)
                        normal.close()
                        require(scope.failed and scope.children[0].reaped)
                        try: scope.assert_backend_release()
                        except lifetime.LifetimeRefused: pass
                        else: raise RuntimeError('Original stop fault was erased')
                    else:
                        signal.signal(signal.SIGALRM,normal.expire)
                        normal.timer_owned=True
                        deferred=False
                        try:
                            with scope.fence():
                                signal.setitimer(signal.ITIMER_REAL,.01)
                                time.sleep(.05)
                                require(not scope.closed and not scope.children[0].reaped)
                                deferred=True
                        except lifetime.normal_source.h.AdmissionError: pass
                        require(deferred and scope.closed and normal.closed and not normal.timer_owned)
            observed=physical(scope)
            require(observed['OriginalParentPidfdClosed'] and observed['OriginalChildPidfdsClosed'])
            require(not observed['BirthAssociationUnproved'])
            fault=name in ('partial-parent-census','partial-child-postassociation-descriptors','stop-failure-recovery')
            results.append({'Control':name,'Passed':True,'ExactFaultWitness':witness['invoked'] if fault else None,**observed})
        finally:
            if sys.exc_info()[0] is None: stage('control-cleanup')
            if scope is not None and not scope.closed:
                try: scope.close()
                except lifetime.LifetimeRefused: pass
    return results


def main():
    output=Path('TestResults/HeldHostNative/native-lifetime.json')
    output.parent.mkdir(parents=True,exist_ok=True)
    receipt={'SchemaVersion':1,'Scope':'SyntheticOriginalWrapperAcquisitionSeams',
             'GithubRunId':os.environ['GITHUB_RUN_ID'],'GithubAttempt':os.environ['GITHUB_RUN_ATTEMPT'],
             'GithubHead':None,'GithubEventSha':os.environ.get('GITHUB_SHA'),'SourceSha256':PINS,
             'ChildSourceSha256':CHILD_SHA256,'Controls':[],
             'NativeLifetimeAccepted':False,'CreatedOutputReaderTasks':0,
             'ProducerDllAdmissionAccepted':False,'NativeDotnetStartObserved':False,'FileSigningBootstrapAccepted':False,
             'AllFdCensusAccepted':False,'EscapedDescendantsAccepted':False,
             'KernelResourceCapsObserved':False,'GenuineEightHostFinancialAccepted':False}
    failed=False
    try:
        receipt['GithubHead']=qualify()
        receipt['Controls']=controls()
        receipt['NativeLifetimeAccepted']=len(receipt['Controls'])==5
    except BaseException:
        failed=True
        receipt['FailureKind']='SyntheticLifetimeControlRefused'
        receipt['FailureStage']=CURRENT_STAGE
        receipt['FailureControl']=CURRENT_CONTROL
    finally:
        stage('caller-cleanup')
        try: cleaned=REGISTRY.cleanup()
        except BaseException:
            REGISTRY.cleanup_failed=True; cleaned=False
        receipt['CallerRegistryPhysicalCleanupComplete']=cleaned
        receipt['CallerRegistryQuarantined']=not cleaned or REGISTRY.pending()
        receipt['OriginalExpiryTimerRestored']=signal.getsignal(signal.SIGALRM)==signal.SIG_DFL and signal.getitimer(signal.ITIMER_REAL)==(0.,0.)
        if not cleaned:
            failed=True; receipt['NativeLifetimeAccepted']=False
            receipt['FailureKind']='SyntheticLifetimeControlRefused'
            receipt.setdefault('FailureStage','caller-cleanup')
            receipt.setdefault('FailureControl',CURRENT_CONTROL)
        output.write_text(json.dumps(receipt,sort_keys=True,indent=2)+'\n',encoding='utf-8')
    require(not failed and receipt['NativeLifetimeAccepted'])


if __name__ == '__main__':
    try: main()
    except BaseException:
        # Fixed category only. Registry retains originals through sanitized
        # handling; child deadline/parent-death signal never becomes success.
        print('HeldHostNative: refused')
        sys.exit(1)
