"""Offline checks: no cluster, credentials or network required."""
import importlib.util
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('render',ROOT/'scripts/render-portal.py')
render=importlib.util.module_from_spec(spec);spec.loader.exec_module(render)

class PackagingTests(unittest.TestCase):
    def images(self):
        return {key:'registry.example.com/demo/'+key.lower()+'@sha256:'+'a'*64
                for key in ['PORTAL_IMAGE','CODEX_RUNNER_IMAGE','RUNNER_IMAGE']}

    def test_secure_origin_and_complete_render(self):
        values=render.validate(self.images(),'https://agent.example.com',False,'model-test')
        text=render.render(ROOT,values)
        self.assertNotIn('${',text)
        self.assertIn("value: 'false'",text)
        self.assertIn("value: 'https://agent.example.com'",text)
        self.assertIn('automountServiceAccountToken: false',text)
        self.assertNotIn('kind: Secret',text)
        self.assertIn('proxy-sha256:',text)
        self.assertIn('port: 8443',text)

    def test_http_only_explicit_loopback(self):
        values=render.validate(self.images(),'',True,'model-test')
        self.assertEqual('true',values['ALLOW_INSECURE_LAB_COOKIE'])
        self.assertEqual('',values['PUBLIC_ORIGIN'])

    def test_insecure_cannot_have_public_origin(self):
        with self.assertRaises(ValueError): render.validate(self.images(),'https://agent.example.com',True,'model')

    def test_invalid_origin_rejected(self):
        for origin in ['', 'http://agent.example.com','https://user@agent.example.com','https://agent.example.com/','https://agent.example.com?q=x',"https://x'\nINJECT"]:
            with self.subTest(origin=origin),self.assertRaises(ValueError): render.validate(self.images(),origin,False,'model')

    def test_unpinned_images_rejected(self):
        for value in ['image:latest','ko://example/worker','image@sha256:bad',"image@sha256:"+'a'*64+'\n']:
            images=self.images();images['RUNNER_IMAGE']=value
            with self.subTest(value=value),self.assertRaises(ValueError): render.validate(images,'',True,'model')

    def test_model_cannot_inject_yaml(self):
        with self.assertRaises(ValueError): render.validate(self.images(),'',True,"x'\nfoo")

    def test_proxy_networks_are_explicit_and_never_trust_all(self):
        for network in ['0.0.0.0/0','::/0','bad']:
            with self.subTest(network=network),self.assertRaises(ValueError):
                render.validate(self.images(),'https://agent.example.com',False,'model',[network])
        values=render.validate(self.images(),'https://agent.example.com',False,'model',['192.0.2.0/24'])
        self.assertEqual('192.0.2.0/24',values['TRUSTED_PROXY_NETWORKS'])

    def test_guest_base_not_private_registry(self):
        text=(ROOT/'deploy/portal/CodexRunner.Dockerfile').read_text()
        self.assertIn('FROM ${AX_RUNNER_IMAGE}',text)
        self.assertNotIn('localhost:',text)

    def test_prepare_has_full_pinned_native_bundle(self):
        pins=(ROOT/'third_party/codex/bundle.sha256').read_text()
        for item in ['bin/codex','bin/codex-code-mode-host','codex-path/rg','codex-resources/bwrap','codex-package.json']:
            self.assertIn(item,pins)
        text=(ROOT/'scripts/prepare-images.sh').read_text()
        self.assertIn('npm pack --ignore-scripts',text)
        self.assertIn('third_party/codex/bundle.sha256',text)

if __name__=='__main__': unittest.main()

