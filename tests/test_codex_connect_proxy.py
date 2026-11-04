"""Targeted relay contract tests; no real provider credentials or network calls."""
import importlib.util
import io
from pathlib import Path
import socket
import unittest
from unittest.mock import MagicMock, patch

spec = importlib.util.spec_from_file_location(
    'codex_relay', Path(__file__).resolve().parents[1] / 'deploy/portal/codex-connect-proxy.py')
relay = importlib.util.module_from_spec(spec)
spec.loader.exec_module(relay)


class RelayTests(unittest.TestCase):
    def handler(self, request):
        handler = object.__new__(relay.Relay)
        handler.connection = MagicMock()
        handler.rfile = io.BytesIO(request)
        handler.wfile = io.BytesIO()
        return handler

    def test_denies_non_allowlisted_destinations_before_dns(self):
        for destination in ('example.com:443', 'auth.openai.com.evil.test:443',
                            '127.0.0.1:443', '169.254.169.254:443',
                            'auth.openai.com:80', 'user@auth.openai.com:443', 'github.com.evil.test:443', 'api.github.com:80'):
            with self.subTest(destination=destination), patch.object(socket, 'getaddrinfo') as dns:
                h = self.handler(f'CONNECT {destination} HTTP/1.1\r\n\r\n'.encode())
                h.handle()
                self.assertIn(b'403', h.wfile.getvalue())
                dns.assert_not_called()

    def test_denies_plain_http(self):
        h = self.handler(b'GET https://auth.openai.com/ HTTP/1.1\r\n\r\n')
        with patch.object(socket, 'getaddrinfo') as dns:
            h.handle()
            self.assertIn(b'403', h.wfile.getvalue())
            dns.assert_not_called()

    def test_rejects_private_or_mixed_dns_answers(self):
        for ips in (['127.0.0.1'], ['10.42.0.1'], ['169.254.169.254'], ['8.8.8.8', '10.0.0.1']):
            with self.subTest(ips=ips):
                h = self.handler(b'CONNECT auth.openai.com:443 HTTP/1.1\r\n\r\n')
                answers = [(socket.AF_INET, socket.SOCK_STREAM, 6, '', (ip, 443)) for ip in ips]
                with patch.object(socket, 'getaddrinfo', return_value=answers), patch.object(socket, 'create_connection') as connect:
                    h.handle()
                    connect.assert_not_called()
                    self.assertNotIn(b'200', h.wfile.getvalue())

    def test_connects_resolved_ip_not_a_second_hostname_lookup(self):
        h = self.handler(b'CONNECT auth.openai.com:443 HTTP/1.1\r\nHost: ignored.invalid\r\n\r\n')
        upstream = MagicMock()
        upstream.recv.return_value = b''
        answers = [(socket.AF_INET, socket.SOCK_STREAM, 6, '', ('8.8.8.8', 443))]
        with patch.object(socket, 'getaddrinfo', return_value=answers), \
             patch.object(socket, 'create_connection', return_value=upstream) as connect, \
             patch.object(relay.select, 'select', return_value=([upstream], [], [])):
            h.handle()
            connect.assert_called_once_with(('8.8.8.8', 443), timeout=10)
            self.assertIn(b'200 Connection established', h.wfile.getvalue())

    def test_oversized_headers_do_not_reach_dns(self):
        for request in (b'x' * 4097, b'CONNECT auth.openai.com:443 HTTP/1.1\r\n' + b'x' * 4097):
            h = self.handler(request)
            with patch.object(socket, 'getaddrinfo') as dns:
                h.handle()
                dns.assert_not_called()


if __name__ == '__main__':
    unittest.main()
