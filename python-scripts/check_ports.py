#!/usr/bin/env python3
"""
Quick connectivity check for Samsung Tizen displays.
Tests TCP port reachability and SSL certificate on port 8002.

Usage:
    python check_ports.py <ip_address>
    python check_ports.py 10.0.133.21
"""

import socket
import ssl
import sys


def check_tcp(host, port, timeout=3):
    """Returns True if TCP port is open."""
    try:
        with socket.create_connection((host, port), timeout=timeout):
            return True
    except (socket.timeout, ConnectionRefusedError, OSError):
        return False


def check_ssl(host, port=8002, timeout=5):
    """Attempts SSL handshake and prints certificate info."""
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE

    try:
        with socket.create_connection((host, port), timeout=timeout) as sock:
            with ctx.wrap_socket(sock, server_hostname=host) as ssock:
                cert = ssock.getpeercert(binary_form=False)
                print(f"  SSL version : {ssock.version()}")
                if cert:
                    print(f"  Certificate : {cert}")
                else:
                    print("  Certificate : self-signed (no details in CERT_NONE mode)")
                return True
    except Exception as e:
        print(f"  SSL error   : {e}")
        return False


def main():
    if len(sys.argv) < 2:
        print(__doc__.strip())
        sys.exit(1)

    host = sys.argv[1]
    ports = [8001, 8002]

    print(f"Checking Samsung display at {host}\n")

    for port in ports:
        status = "OPEN" if check_tcp(host, port) else "CLOSED"
        print(f"  Port {port}: {status}")

    print(f"\nSSL check on {host}:8002:")
    check_ssl(host)


if __name__ == "__main__":
    main()
