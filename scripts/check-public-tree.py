#!/usr/bin/env python3
"""Publication hygiene check, not a replacement for independent secret scanning."""
from pathlib import Path
import re
import subprocess
import sys

ROOT=Path(__file__).resolve().parents[1]
BLOCKED_PARTS={'.platform','.local','.qa','.openai','node_modules','__pycache__','patches'}
PATTERNS={
    'private key': re.compile(rb'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'),
    'GitHub token': re.compile(rb'\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})'),
    'provider key': re.compile(rb'\bsk-(?:proj-)?[A-Za-z0-9_-]{35,}'),
    'machine path': re.compile(rb'/(?:root/projects|mnt/HC_Volume_|tmp/agent-desk-platform-)'),
    'IP-based deployment hostname': re.compile(rb'\b[a-z0-9.-]*\d{1,3}-\d{1,3}-\d{1,3}-\d{1,3}\.sslip\.io'),
}

def problems(path,data):
    found=[]
    if path.is_symlink(): found.append('symlink')
    if BLOCKED_PARTS.intersection(path.parts) or path.suffix in {'.db','.key','.pem','.pfx','.tgz'}:
        found.append('runtime/credential file')
    if data.startswith(b'SQLite format 3\0'): found.append('SQLite database')
    for label,pattern in PATTERNS.items():
        if pattern.search(data): found.append(label)
    return found

def main():
    names=subprocess.check_output(['git','ls-files','-z','--cached','--others','--exclude-standard'],cwd=ROOT).decode().split('\0')
    failures=[]
    for name in sorted(set(filter(None,names))):
        path=ROOT/name
        if not path.exists(): failures.append((name,['tracked file missing']));continue
        issues=problems(Path(name),path.read_bytes())
        if path.is_symlink(): issues.append('symlink')
        if issues: failures.append((name,issues))
        if name.endswith('.md'):
            for target in re.findall(r'\]\(([^\s)]+)',path.read_text()):
                if '://' in target or target.startswith(('#','mailto:')): continue
                local=target.split('#')[0]
                if local and not (path.parent/local).exists(): failures.append((name,['broken local link: '+local]))
    for name,issues in failures: print(name+': '+', '.join(issues),file=sys.stderr)
    if failures: return 1
    print('Public tree hygiene passed (candidate files + local Markdown links).')
    return 0

if __name__=='__main__': sys.exit(main())

