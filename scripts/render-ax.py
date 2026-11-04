#!/usr/bin/env python3
"""Render pinned upstream AX manifests offline; no apply and no credential generation."""
import argparse
from pathlib import Path
import re
import subprocess

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--server-image',required=True)
    p.add_argument('--runner-image',required=True)
    args=p.parse_args()
    for value in [args.server_image,args.runner_image]:
        if not re.fullmatch(r'[A-Za-z0-9._:/-]+@sha256:[a-f0-9]{64}',value):p.error('Use digest-pinned image references.')
    root=Path(__file__).resolve().parents[1]
    source=root/'.platform/ax'
    expected=dict(line.split('=',1) for line in (root/'deploy/platform/versions.env').read_text().splitlines() if line and not line.startswith('#'))['AX_REF']
    if subprocess.check_output(['git','-C',str(source),'rev-parse','HEAD'],text=True).strip()!=expected:p.error('AX source pin mismatch.')
    if subprocess.check_output(['git','-C',str(source),'status','--porcelain','--untracked-files=no'],text=True).strip():p.error('AX source has local edits; review/render them separately.')
    values={'AX_SYSTEM_NAMESPACE':'ax-system','AX_SERVER_IMAGE':args.server_image,'AX_DEFAULT_TASK_IMAGE':args.runner_image,'AX_SNAPSHOTS_BUCKET':'gs://ate-snapshots/ax/'}
    # E2E template has explicit identity settings and no developer-specific bucket.
    text=(source/'test/e2e/manifests/ax-server.yaml').read_text()
    for key,value in values.items():text=text.replace('${'+key+'}',value)
    if '${' in text:p.error('Unresolved upstream placeholder; review the pinned schema.')
    postgres=(source/'deploy/postgres.yaml').read_text()
    print('# Derived from google/ax '+expected+' (Apache-2.0).\n'+postgres+'\n---\n'+text)

if __name__=='__main__':main()
