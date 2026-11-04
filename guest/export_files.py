"""Bounded read-only workspace export. No symlinks, hardlinks, special files or hidden paths."""
import base64, json, os, stat

MAX_FILE = 1024 * 1024
MAX_TOTAL = 8 * MAX_FILE
MAX_FILES = 256
EXCLUDED = {'node_modules', 'bin', 'obj', '__pycache__', 'vendor'}

def export(root='/workspace', emit=lambda x: print(json.dumps(x, ensure_ascii=True), flush=True)):
    total = count = skipped = visited = 0
    def walk(fd, prefix='', depth=0):
        nonlocal total, count, skipped, visited
        if depth > 12: skipped += 1; return
        with os.scandir(fd) as entries:
            for entry in entries:
                visited += 1
                if visited > 4096 or count >= MAX_FILES: skipped += 1; return
                name = entry.name
                if name.startswith('.') or name in EXCLUDED or len(prefix + name) > 500:
                    skipped += 1; continue
                child = None
                try:
                    # open relative to an already opened directory; never follow links.
                    child = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=fd)
                    info = os.fstat(child)
                    if stat.S_ISDIR(info.st_mode): walk(child, prefix + name + '/', depth + 1)
                    elif stat.S_ISREG(info.st_mode) and info.st_nlink == 1 and info.st_size <= MAX_FILE:
                        data = bytearray()
                        while len(data) <= MAX_FILE:
                            chunk = os.read(child, min(65536, MAX_FILE + 1 - len(data)))
                            if not chunk: break
                            data.extend(chunk)
                        after = os.fstat(child)
                        if len(data) > MAX_FILE or total + len(data) > MAX_TOTAL or (info.st_size,info.st_mtime_ns)!=(after.st_size,after.st_mtime_ns):
                            skipped += 1; continue
                        total += len(data); count += 1
                        emit({'type':'file', 'path':prefix + name, 'data':base64.b64encode(data).decode()})
                    else: skipped += 1
                except OSError: skipped += 1
                finally:
                    if child is not None: os.close(child)
    fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try: walk(fd)
    finally: os.close(fd)
    emit({'type':'summary','files':count,'bytes':total,'skipped':skipped})

if __name__ == '__main__': export()
