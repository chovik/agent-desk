import importlib.util, os, pathlib, tempfile, unittest
spec=importlib.util.spec_from_file_location('export_files',pathlib.Path(__file__).resolve().parents[1]/'guest/export_files.py')
mod=importlib.util.module_from_spec(spec);spec.loader.exec_module(mod)
class ExportTests(unittest.TestCase):
    def test_regular_files_only_and_no_secret_paths(self):
        with tempfile.TemporaryDirectory() as temp:
            root=pathlib.Path(temp)/'workspace';root.mkdir();(root/'src').mkdir();(root/'src/a.txt').write_text('hello')
            outside=pathlib.Path(temp)/'secret';outside.write_text('not-exported')
            (root/'link').symlink_to(outside);(root/'dirlink').symlink_to(pathlib.Path(temp),target_is_directory=True)
            os.link(outside,root/'hardlink');os.mkfifo(root/'fifo');(root/'.env').write_text('secret')
            (root/'.agent-desk').mkdir();(root/'.agent-desk/session').write_text('private')
            frames=[];mod.export(str(root),frames.append)
            self.assertEqual([x['path'] for x in frames if x['type']=='file'],['src/a.txt'])
            self.assertEqual(frames[-1]['files'],1);self.assertGreaterEqual(frames[-1]['skipped'],6)
    def test_size_limit_and_empty_file(self):
        with tempfile.TemporaryDirectory() as temp:
            root=pathlib.Path(temp);(root/'big').write_bytes(b'x'*(mod.MAX_FILE+1));(root/'empty').touch()
            frames=[];mod.export(temp,frames.append)
            self.assertEqual(frames[0]['path'],'empty');self.assertEqual(frames[0]['data'],'');self.assertEqual(frames[-1]['skipped'],1)
    def test_root_symlink_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            p=pathlib.Path(temp);(p/'link').symlink_to(p,target_is_directory=True)
            with self.assertRaises(OSError):mod.export(str(p/'link'),lambda _:None)
