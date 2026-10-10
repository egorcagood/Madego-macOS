#!/usr/bin/env python3
"""Pinned vendor SDK archives, isolated under work. Never installs system packages."""
from pathlib import Path
import hashlib,json,tarfile,urllib.request
root=Path(__file__).resolve().parents[1]
archives=[
    ('dotnet','https://builds.dotnet.microsoft.com/dotnet/Sdk/9.0.318/dotnet-sdk-9.0.318-osx-arm64.tar.gz','sha512','4305c929005f47a933f33c253b6f47f8b21fd6096c7e359e78495e0de7c8ed1c6700aec2785a983d76ac7c99a66b1422abedb4056259b00e748ffbe36f243532'),
    ('go','https://go.dev/dl/go1.27.1.darwin-arm64.tar.gz','sha256','ee215d57e0ec269c60cc9ceca68e6bda321ba9ee5afe24f4b0988703c2d87d12')]
for name,url,algorithm,expected in archives:
    out=root/'work/toolchains';out.mkdir(parents=True,exist_ok=True);archive=out/(name+'.tar.gz')
    if not archive.exists():urllib.request.urlretrieve(url,archive)
    actual=hashlib.new(algorithm,archive.read_bytes()).hexdigest()
    if actual!=expected:raise SystemExit('SDK hash mismatch: '+name)
    target=out/('dotnet' if name=='dotnet' else 'go-parent')
    if not target.exists():
        target.mkdir();
        with tarfile.open(archive) as bundle:bundle.extractall(target,filter='data')
    print(name+' VERIFIED '+actual)
