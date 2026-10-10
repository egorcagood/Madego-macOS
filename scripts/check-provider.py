#!/usr/bin/env python3
from pathlib import Path
import subprocess
root=Path(__file__).resolve().parents[1]
out=root/'artifacts/provider-compile';out.mkdir(parents=True,exist_ok=True)
subprocess.run(['swiftc','-target','arm64-apple-macosx14.0','-warnings-as-errors','-parse-as-library','-module-name','MadegoPacketTunnel','src/Madego.PacketTunnel/PacketTunnelProvider.swift','src/Madego.PacketTunnel/ProviderMain.swift','-framework','NetworkExtension','-o',str(out/'MadegoPacketTunnel')],cwd=root,check=True)
print('PROVIDER_BOUNDARY_COMPILED; NOT installed; NOT a functional tunnel')
