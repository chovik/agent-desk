"""Read-only, bounded live workspace access. No shell, links, hidden/internal paths."""
import base64, json, os, stat, sys
MAX_FILE = 8 * 1024 * 1024
MAX_ENTRIES = 500

def safe_path(path, root=False):
    return isinstance(path, str) and ((root and path == '') or (0 < len(path) <= 500 and len(path.split('/')) <= 32 and all(part and not part.startswith('.') for part in path.split('/')) and '\\' not in path and not any(ord(c) < 32 or ord(c) == 127 for c in path)))

def access(request, root='/workspace', emit=lambda x: print(json.dumps(x, ensure_ascii=True), flush=True)):
    op, path = request.get('operation'), request.get('path')
    if op not in ('list', 'read') or not safe_path(path, op == 'list'):
        emit({'type':'workspace-error','status':400});return
    fd = None
    try:
        fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        parts = path.split('/') if path else []
        for i, part in enumerate(parts):
            flags = os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK
            if i < len(parts)-1 or op == 'list': flags |= os.O_DIRECTORY
            child = os.open(part, flags, dir_fd=fd);os.close(fd);fd = child
        if op == 'list':
            entries = [];skipped = 0;truncated = False
            with os.scandir(fd) as iterator:
                for i, entry in enumerate(iterator):
                    if i >= 4096 or len(entries) >= MAX_ENTRIES: truncated = True;break
                    name = entry.name
                    if not safe_path(name) or '/' in name: skipped += 1;continue
                    try:
                        info = entry.stat(follow_symlinks=False)
                        kind = 'directory' if stat.S_ISDIR(info.st_mode) else 'file' if stat.S_ISREG(info.st_mode) and info.st_nlink == 1 else 'blocked'
                        entries.append({'name':name,'kind':kind,'size':info.st_size if kind == 'file' else 0})
                    except OSError: skipped += 1
            emit({'type':'directory','path':path,'entries':sorted(entries,key=lambda e:(e['kind']!='directory',e['name'])),'skipped':skipped,'truncated':truncated})
        else:
            info = os.fstat(fd)
            if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1:
                emit({'type':'workspace-error','status':403});return
            if info.st_size > MAX_FILE:
                emit({'type':'workspace-error','status':413});return
            total = 0
            while True:
                data = os.read(fd,65536)
                if not data: break
                total += len(data)
                if total > MAX_FILE: emit({'type':'workspace-error','status':413});return
                emit({'type':'data','data':base64.b64encode(data).decode()})
            after = os.fstat(fd)
            if (info.st_size,info.st_mtime_ns,info.st_ctime_ns,info.st_nlink)!=(after.st_size,after.st_mtime_ns,after.st_ctime_ns,after.st_nlink):
                emit({'type':'workspace-error','status':409});return
            emit({'type':'file','size':total})
    except (FileNotFoundError, NotADirectoryError):emit({'type':'workspace-error','status':404})
    except OSError:emit({'type':'workspace-error','status':403})
    finally:
        if fd is not None: os.close(fd)

if __name__ == '__main__': access(json.loads(sys.stdin.readline(4000)))
