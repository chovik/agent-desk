import base64,importlib.util,io,json,pathlib,subprocess,tempfile,unittest
from unittest.mock import patch
spec=importlib.util.spec_from_file_location('repository',pathlib.Path(__file__).parents[1]/'guest/repository.py');m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
class RepositoryTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory();self.root=pathlib.Path(self.tmp.name);self.source=self.root/'source';self.source.mkdir();self.branch='agent-desk/'+'a'*32
        self.git('init');self.git('config','user.name','Fixture');self.git('config','user.email','fixture@example.test');(self.source/'file.txt').write_text('before\n');self.git('add','.');self.git('commit','-m','base');self.base=self.git('rev-parse','HEAD').strip();self.git('branch',self.branch)
        self.bundle=self.root/'seed.bundle';self.git('bundle','create',str(self.bundle),'refs/heads/'+self.branch);self.config={'branch':self.branch,'baseSha':self.base,'checkCommand':'python3 -c "assert open(\'file.txt\').read() == \'after\\n\'"'}
        self.target=self.root/'repo';self.patcher=patch.object(m,'ROOT',self.target);self.patcher.start();self.install()
    def tearDown(self):self.patcher.stop();self.tmp.cleanup()
    def git(self,*args,cwd=None):return subprocess.check_output(['git',*args],cwd=cwd or self.source,stderr=subprocess.DEVNULL,text=True)
    def invoke(self,mode,command=None):
        frames=[]
        with patch.object(m,'emit',frames.append):m.main({**self.config,'mode':mode,**({'checkCommand':command} if command else {})})
        return frames
    def install(self):
        data=self.bundle.read_bytes();frames=[{'type':'chunk','data':base64.b64encode(data).decode()},{'type':'end'}]
        with patch.object(m.sys,'stdin',io.StringIO('\n'.join(map(json.dumps,frames))+'\n')):return self.invoke('install')
    def change(self):
        (self.target/'file.txt').write_text('after\n');self.git('add','.',cwd=self.target);self.git('commit','-m','change',cwd=self.target)
    def test_checkout_pins_commit_and_has_no_remotes_or_credentials(self):
        self.assertEqual(self.git('rev-parse','HEAD',cwd=self.target).strip(),self.base);self.assertEqual(self.git('remote','-v',cwd=self.target),'');self.assertEqual(self.git('branch','--show-current',cwd=self.target).strip(),self.branch)
    def test_resuming_install_does_not_overwrite_changes(self):
        (self.target/'file.txt').write_text('work in progress');self.assertEqual(self.install(),[{'type':'installed'}]);self.assertEqual((self.target/'file.txt').read_text(),'work in progress')
    def test_committed_passed_check_exports_real_incremental_bundle(self):
        self.change();frames=self.invoke('capture');self.assertTrue(frames[0]['ok']);data=b''.join(base64.b64decode(f['data']) for f in frames if f['type']=='chunk');bundle=self.root/'result.bundle';bundle.write_bytes(data);self.git('bundle','verify',str(bundle));self.git('fetch',str(bundle),'HEAD');self.assertEqual(self.git('rev-parse','FETCH_HEAD').strip(),frames[0]['head'])
    def test_uncommitted_files_block_publish_without_running_checks(self):
        (self.target/'new.txt').write_text('untracked');frames=self.invoke('capture','touch should-not-exist');self.assertFalse(frames[0]['ok']);self.assertFalse((self.target/'should-not-exist').exists())
    def test_failed_check_blocks_publish(self):
        self.change();frames=self.invoke('capture','echo failed; exit 7');self.assertFalse(frames[0]['ok']);self.assertIn('failed',frames[0]['checks']);self.assertEqual(len(frames),1)
    def test_check_that_changes_worktree_blocks_publish(self):
        self.change();self.assertFalse(self.invoke('capture','echo changed >> file.txt')[0]['ok'])
    def test_different_branch_blocks_publish(self):
        self.git('checkout','-b','other',cwd=self.target);self.assertFalse(self.invoke('capture')[0]['ok'])
    def test_no_commits_is_not_publishable(self):self.assertFalse(self.invoke('capture','true')[0]['ok'])
    def test_noisy_checks_are_bounded(self):
        self.change();frames=self.invoke('capture',"python3 -c \"print('x'*1000000)\"");self.assertTrue(frames[0]['ok']);self.assertLess(len(frames[0]['checks']),16100);self.assertIn('truncated',frames[0]['checks'])
    def test_symlink_checkout_refused(self):
        with patch.object(m,'ROOT',self.root/'alias'):
            m.ROOT.symlink_to(self.target,target_is_directory=True)
            with self.assertRaises(ValueError):self.invoke('capture')
if __name__=='__main__':unittest.main()
