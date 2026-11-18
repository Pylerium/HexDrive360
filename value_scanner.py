#!/usr/bin/env python3
import socket
import argparse

JRPC_PORT = 730

def send_cmd(ip, cmd):
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.settimeout(5)
    s.connect((ip, JRPC_PORT))

    banner = s.recv(1024).decode(errors="ignore").strip()
    print(f"[BANNER] {banner}")

    s.sendall((cmd + "\r\n").encode())

    try:
        resp = s.recv(4096).decode(errors="ignore").strip()
        if resp:
            print(resp)
    except:
        pass

    s.close()

def main():
    parser = argparse.ArgumentParser("JRPC2 CLI")
    parser.add_argument("--ip", required=True)

    sub = parser.add_subparsers(dest="cmd", required=True)

    n = sub.add_parser("notify")
    n.add_argument("message")
    n.add_argument("--reconnectport", type=int, default=1337)

    args = parser.parse_args()

    if args.cmd == "notify":
        # IMPORTANT: message FIRST, port SECOND
        send_cmd(args.ip, f"notify {args.message} {args.reconnectport}")

if __name__ == "__main__":
    main()
