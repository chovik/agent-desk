#!/usr/bin/env python3
"""Render portable manifests offline. Never contacts Kubernetes or reads credentials."""
import argparse
import hashlib
import ipaddress
import json
from pathlib import Path
import re
from urllib.parse import urlparse

def validate(values, origin, insecure, model, proxy_networks=()):
    for key in ['PORTAL_IMAGE','CODEX_RUNNER_IMAGE','RUNNER_IMAGE']:
        if not re.fullmatch(r'[A-Za-z0-9._:/-]+@sha256:[a-f0-9]{64}',values.get(key,'')):
            raise ValueError(f'{key} must be a digest-pinned image reference.')
    if insecure:
        if origin: raise ValueError('Loopback HTTP mode must not have a public origin.')
    else:
        u=urlparse(origin)
        if u.scheme!='https' or not re.fullmatch(r'[a-z0-9.-]+',u.netloc) or u.path or u.query or u.fragment:
            raise ValueError('Provide --origin https://your-host (no path/port), or explicitly choose --loopback-http.')
    if not re.fullmatch(r'[A-Za-z0-9._-]{1,100}',model): raise ValueError('Invalid model name.')
    proxies=[ipaddress.ip_network(n,strict=True) for n in proxy_networks]
    if any(n.prefixlen==0 for n in proxies): raise ValueError('Trust-all proxy networks are forbidden.')
    if insecure and proxies: raise ValueError('Loopback mode does not accept an external proxy network.')
    return {**values,'PUBLIC_ORIGIN':origin,'ALLOW_INSECURE_LAB_COOKIE':str(insecure).lower(),'CODEX_MODEL':model,'TRUSTED_PROXY_NETWORKS':','.join(map(str,proxies))}

def render(root,values):
    values={**values,'PROXY_SHA256':hashlib.sha256((root/'deploy/portal/codex-connect-proxy.py').read_bytes()).hexdigest()}
    parts=[]
    for filename in ['portal.yaml','codex-auth-egress.yaml']:
        text=(root/'deploy/portal'/filename).read_text()
        text=re.sub(r'\$\{([A-Z0-9_]+)\}',lambda m:values[m[1]],text)
        parts.append(text.rstrip())
    return '\n---\n'.join(parts)+'\n'

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--images',type=Path,default=Path('.local/images.json'))
    mode=p.add_mutually_exclusive_group(required=True)
    mode.add_argument('--origin');mode.add_argument('--loopback-http',action='store_true')
    p.add_argument('--model',default='gpt-6.1-sol')
    p.add_argument('--trusted-proxy-network',action='append',default=[],help='Explicit ingress source CIDR; never /0. Repeat for multiple networks.')
    args=p.parse_args()
    try:
        values=validate(json.loads(args.images.read_text()),args.origin or '',args.loopback_http,args.model,args.trusted_proxy_network)
        print(render(Path(__file__).resolve().parents[1],values),end='')
    except (ValueError,KeyError,OSError) as e: p.error(str(e))

if __name__=='__main__': main()
