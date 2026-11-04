#!/usr/bin/env python3
"""TLS CONNECT relay for approved Codex and GitHub endpoints; never decrypts or logs traffic."""
import ipaddress
import select
import socket
import socketserver
import threading

ALLOWED = {'auth.openai.com', 'chatgpt.com', 'api.openai.com', 'github.com', 'api.github.com'}
SLOTS = threading.BoundedSemaphore(32)

class Relay(socketserver.StreamRequestHandler):
    def handle(self):
        if not SLOTS.acquire(blocking=False): return
        try:
            self.connection.settimeout(20)
            request = self.rfile.readline(4097)
            if len(request)>4096: return
            words=request.decode('ascii','strict').strip().split()
            if len(words)!=3 or words[0]!='CONNECT' or words[2] not in ('HTTP/1.1','HTTP/1.0'):
                self.wfile.write(b'HTTP/1.1 403 Forbidden\r\n\r\n');return
            host,sep,port=words[1].rpartition(':')
            if not sep or host.lower() not in ALLOWED or port!='443':
                self.wfile.write(b'HTTP/1.1 403 Forbidden\r\n\r\n');return
            for _ in range(50):
                line=self.rfile.readline(4097)
                if line in (b'\r\n',b'\n'):break
                if not line or len(line)>4096:return
            else:return
            addresses={item[4][0] for item in socket.getaddrinfo(host,443,socket.AF_INET,socket.SOCK_STREAM)}
            if not addresses or any(not ipaddress.ip_address(ip).is_global for ip in addresses):return
            upstream=None
            for ip in addresses:
                try:upstream=socket.create_connection((ip,443),timeout=10);break
                except OSError:continue
            if upstream is None:return
            with upstream:
                self.wfile.write(b'HTTP/1.1 200 Connection established\r\n\r\n');self.wfile.flush()
                peers=[self.connection,upstream]
                while True:
                    ready,_,_=select.select(peers,[],[],65)
                    if not ready:return
                    for src in ready:
                        data=src.recv(32768)
                        if not data:return
                        (upstream if src is self.connection else self.connection).sendall(data)
        except (OSError,ValueError,UnicodeError):pass
        finally:SLOTS.release()
class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address=True
    daemon_threads=True
    request_queue_size=16
if __name__=='__main__':
    with Server(('0.0.0.0',3128),Relay) as server: server.serve_forever()
