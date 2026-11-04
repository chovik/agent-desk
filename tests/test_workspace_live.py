import base64,importlib.util,os,pathlib,tempfile,unittest
spec=importlib.util.spec_from_file_location('workspace_files',pathlib.Path(__file__).resolve().parents[1]/'guest/workspace_files.py')
mod=importlib.util.module_from_spec(spec);spec.loader.exec_module(mod)
class LiveWorkspaceTests(unittest.TestCase):
    def call(self,root,op,path):
        frames=[];mod.access({'operation':op,'path':path},str(root),frames.append);return frames
    def test_list_nested_read_and_binary(self):
        with tempfile.TemporaryDirectory() as t:
            p=pathlib.Path(t);(p/'src').mkdir();(p/'src/x.bin').write_bytes(b'a\x00\xff');(p/'.env').write_text('private')
            f=self.call(p,'list','');self.assertEqual(f[0]['entries'],[{'name':'src','kind':'directory','size':0}]);self.assertEqual(f[0]['skipped'],1)
            f=self.call(p,'read','src/x.bin');self.assertEqual(base64.b64decode(f[0]['data']),b'a\x00\xff');self.assertEqual(f[-1],{'type':'file','size':3})
    def test_links_fifo_and_escapes_never_read(self):
        with tempfile.TemporaryDirectory() as t:
            p=pathlib.Path(t);root=p/'workspace';root.mkdir();(p/'secret').write_text('secret');(root/'link').symlink_to(p/'secret');(root/'dirlink').symlink_to(p,target_is_directory=True);os.link(p/'secret',root/'hard');os.mkfifo(root/'pipe')
            for name in ['link','dirlink/secret','hard','pipe','../secret','/etc/passwd','.env','a/../secret','a\\b']:
                f=self.call(root,'read',name);self.assertEqual(f[-1]['type'],'workspace-error');self.assertFalse(any(x['type']=='data' for x in f))
    def test_size_and_listing_limits(self):
        with tempfile.TemporaryDirectory() as t:
            p=pathlib.Path(t);(p/'big').write_bytes(b'x'*(mod.MAX_FILE+1));self.assertEqual(self.call(p,'read','big')[-1]['status'],413)
            for i in range(510):(p/str(i)).touch()
            f=self.call(p,'list','')[0];self.assertEqual(len(f['entries']),500);self.assertTrue(f['truncated'])
    def test_root_symlink_is_rejected(self):
        with tempfile.TemporaryDirectory() as t:
            p=pathlib.Path(t);(p/'link').symlink_to(p,target_is_directory=True);self.assertEqual(self.call(p/'link','list','')[-1]['type'],'workspace-error')
    def test_changed_file_is_not_returned_as_complete(self):
        with tempfile.TemporaryDirectory() as t:
            p=pathlib.Path(t);f=p/'a';f.write_bytes(b'x'*100);out=[]
            def emit(frame):
                out.append(frame)
                if frame['type']=='data':f.write_bytes(b'changed')
            mod.access({'operation':'read','path':'a'},str(p),emit);self.assertEqual(out[-1],{'type':'workspace-error','status':409})
