#!/usr/bin/env python3
"""Provision a new deployment locally; refuse to rotate/replace existing secrets."""
import argparse
import getpass
import json
import os
from pathlib import Path
import secrets
import subprocess
import uuid

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--username', required=True)
    args=parser.parse_args()
    if not args.username.strip() or len(args.username)>100:
        parser.error('Username must contain 1–100 characters.')
    root=Path(__file__).resolve().parents[1]
    directory=root/'.local/secrets'
    if directory.exists():
        parser.error('.local/secrets already exists. Recover the original vault key; never rotate it implicitly.')
    password=getpass.getpass('Choose portal password (minimum 16 characters): ')
    if len(password)<16 or password!=getpass.getpass('Repeat password: '):
        parser.error('Password too short or confirmation mismatch.')
    dll=root/'.local/build/publish/AgentDesk.Portal.dll'
    if not dll.exists(): parser.error('Run scripts/prepare-images.sh first.')
    result=subprocess.run(['dotnet',str(dll),'--hash-password'],input=password+'\n',text=True,capture_output=True,check=True)
    password=''
    os.umask(0o077)
    directory.mkdir(parents=True,mode=0o700)
    users=[dict(id=uuid.uuid4().hex,username=args.username,passwordHash=result.stdout.strip())]
    (directory/'users.json').write_text(json.dumps(users)+'\n')
    (directory/'vault.key').write_bytes(secrets.token_bytes(32))
    print('Created private .local/secrets/ with password hash and vault key. No plaintext password saved.')
    print('Back up the vault key separately. Do not recreate it on upgrades.')

if __name__=='__main__': main()

