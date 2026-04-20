#!/usr/bin/env python3
"""
Test UPnP SOAP volume and mute control on a Samsung Tizen display.
Uses the RenderingControl:1 service on port 9197.

Usage:
    python upnp_volume.py <ip_address> get
    python upnp_volume.py <ip_address> set <0-100>
    python upnp_volume.py <ip_address> up [steps]
    python upnp_volume.py <ip_address> down [steps]
    python upnp_volume.py <ip_address> mute
    python upnp_volume.py <ip_address> unmute
    python upnp_volume.py <ip_address> status

Examples:
    python upnp_volume.py 10.0.133.21 get          # print current volume
    python upnp_volume.py 10.0.133.21 set 25        # set volume to 25
    python upnp_volume.py 10.0.133.21 up 5          # raise volume by 5
    python upnp_volume.py 10.0.133.21 down           # lower volume by 1
    python upnp_volume.py 10.0.133.21 mute           # mute audio
    python upnp_volume.py 10.0.133.21 unmute         # unmute audio
    python upnp_volume.py 10.0.133.21 status         # print volume + mute state

No additional dependencies required (uses urllib from the standard library).
"""

import re
import sys
import xml.etree.ElementTree as ET
from urllib.request import Request, urlopen
from urllib.error import URLError, HTTPError

RENDERING_CONTROL_URN = "urn:schemas-upnp-org:service:RenderingControl:1"
DEFAULT_PORT = 9197
CONTROL_PATH = "/upnp/control/RenderingControl1"
TIMEOUT_S = 5


def build_soap_envelope(action: str, inner_xml: str) -> str:
    return (
        '<?xml version="1.0" encoding="utf-8"?>'
        '<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" '
        's:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">'
        "<s:Body>"
        f'<u:{action} xmlns:u="{RENDERING_CONTROL_URN}">{inner_xml}</u:{action}>'
        "</s:Body></s:Envelope>"
    )


def soap_request(host: str, port: int, action: str, inner_xml: str) -> str:
    """Send a SOAP request and return the response body."""
    url = f"http://{host}:{port}{CONTROL_PATH}"
    body = build_soap_envelope(action, inner_xml).encode("utf-8")
    req = Request(url, data=body, method="POST")
    req.add_header("Content-Type", 'text/xml; charset="utf-8"')
    req.add_header("SOAPAction", f'"{RENDERING_CONTROL_URN}#{action}"')

    try:
        with urlopen(req, timeout=TIMEOUT_S) as resp:
            return resp.read().decode("utf-8")
    except HTTPError as e:
        error_body = e.read().decode("utf-8", errors="replace")
        print(f"HTTP {e.code} from {url}: {error_body}", file=sys.stderr)
        sys.exit(1)
    except URLError as e:
        print(f"Connection failed to {url}: {e.reason}", file=sys.stderr)
        sys.exit(1)


def parse_element(xml_text: str, tag: str) -> str | None:
    """Extract text content of the first matching element (any namespace)."""
    match = re.search(rf"<[^>]*?{tag}[^>]*?>(.*?)</[^>]*?{tag}>", xml_text, re.IGNORECASE)
    return match.group(1) if match else None


# ---------- Commands ----------


def get_volume(host: str, port: int) -> int:
    body = "<InstanceID>0</InstanceID><Channel>Master</Channel>"
    resp = soap_request(host, port, "GetVolume", body)
    val = parse_element(resp, "CurrentVolume")
    if val is None:
        print("Could not parse CurrentVolume from response", file=sys.stderr)
        sys.exit(1)
    return int(val)


def set_volume(host: str, port: int, level: int) -> None:
    level = max(0, min(100, level))
    body = (
        "<InstanceID>0</InstanceID>"
        "<Channel>Master</Channel>"
        f"<DesiredVolume>{level}</DesiredVolume>"
    )
    soap_request(host, port, "SetVolume", body)


def get_mute(host: str, port: int) -> bool:
    body = "<InstanceID>0</InstanceID><Channel>Master</Channel>"
    resp = soap_request(host, port, "GetMute", body)
    val = parse_element(resp, "CurrentMute")
    if val is None:
        print("Could not parse CurrentMute from response", file=sys.stderr)
        sys.exit(1)
    return val != "0"


def set_mute(host: str, port: int, mute: bool) -> None:
    body = (
        "<InstanceID>0</InstanceID>"
        "<Channel>Master</Channel>"
        f"<DesiredMute>{'1' if mute else '0'}</DesiredMute>"
    )
    soap_request(host, port, "SetMute", body)


# ---------- CLI ----------


def usage():
    print(__doc__.strip())
    sys.exit(1)


def main():
    if len(sys.argv) < 3:
        usage()

    host = sys.argv[1]
    command = sys.argv[2].lower()
    port = DEFAULT_PORT

    if command == "get":
        vol = get_volume(host, port)
        print(f"Volume: {vol}")

    elif command == "set":
        if len(sys.argv) < 4:
            print("Error: 'set' requires a volume level (0-100)", file=sys.stderr)
            sys.exit(1)
        level = int(sys.argv[3])
        set_volume(host, port, level)
        print(f"Volume set to {max(0, min(100, level))}")

    elif command == "up":
        steps = int(sys.argv[3]) if len(sys.argv) > 3 else 1
        current = get_volume(host, port)
        new_level = min(100, current + steps)
        set_volume(host, port, new_level)
        print(f"Volume: {current} -> {new_level}")

    elif command == "down":
        steps = int(sys.argv[3]) if len(sys.argv) > 3 else 1
        current = get_volume(host, port)
        new_level = max(0, current - steps)
        set_volume(host, port, new_level)
        print(f"Volume: {current} -> {new_level}")

    elif command == "mute":
        set_mute(host, port, True)
        print("Muted")

    elif command == "unmute":
        set_mute(host, port, False)
        print("Unmuted")

    elif command == "status":
        vol = get_volume(host, port)
        muted = get_mute(host, port)
        print(f"Volume: {vol}")
        print(f"Muted:  {muted}")

    else:
        print(f"Unknown command: {command}", file=sys.stderr)
        usage()


if __name__ == "__main__":
    main()
