#!/usr/bin/env python3
from pathlib import Path
import hashlib,json,os,subprocess
from datetime import datetime,timezone
root=Path(__file__).resolve().parents[1]
sdk=os.environ.get('MADEGO_DOTNET',str(root/'work/toolchains/dotnet/dotnet'))
env=dict(os.environ,DOTNET_CLI_TELEMETRY_OPTOUT='1',DOTNET_NOLOGO='1')
out=root/'verification'/('run-'+datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ'));out.mkdir(parents=True,exist_ok=True)
def run(name,args):
    v=subprocess.run(args,cwd=root,env=env,text=True,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=240)
    (out/(name+'.log')).write_text(v.stdout)
    print(name, 'PASS' if v.returncode==0 else 'FAIL',v.stdout[-500:],flush=True)
    return {'test':name,'exit':v.returncode,'log':str(out/(name+'.log'))}
shared=json.loads((root/'build-metadata/shared-source.json').read_text())['files']
for p,h in shared.items():
    if hashlib.sha256((root/'shared-source'/p).read_bytes()).hexdigest()!=h:raise SystemExit('Shared source mismatch: '+p)
baseline=root/'build-metadata/windows-baseline.json'
if baseline.exists():
    files=json.loads(baseline.read_text())['files']
    for p,h in files.items():
        if hashlib.sha256((root/'windows-baseline'/p).read_bytes()).hexdigest()!=h:raise SystemExit('Frozen baseline mismatch: '+p)
    print('FROZEN_BASELINE_VERIFIED='+str(len(files)),flush=True)
results=[run('CoreChecks',[sdk,'run','--project','tests/CoreChecks','-c','Release'])]
native=root/'artifacts/native/libMadegoKeychain.dylib';native.parent.mkdir(parents=True,exist_ok=True)
results.append(run('KeychainCompile',['swiftc','-target','arm64-apple-macosx14.0','-warnings-as-errors','-emit-library','src/Madego.Platform/KeychainBridge.swift','-o',str(native),'-framework','Security']))
results.append(run('BrokerChecks',[sdk,'run','--project','tests/BrokerChecks','-c','Release','--','--native',str(native)]))
helper=root/'windows-baseline/src/Madyar.Link/runtime/macos-arm64/MadyarLink'
if not helper.exists():
    go=os.environ.get('MADEGO_GO',str(root/'work/toolchains/go-parent/go/bin/go'));helper=root/'artifacts/Link/MadyarLink';helper.parent.mkdir(parents=True,exist_ok=True)
    subprocess.run([go,'build','-trimpath','-ldflags=-s -w','-o',str(helper),'.'],cwd=root/'shared-source/src/Madyar.Link',env=dict(env,CGO_ENABLED='0',GOTELEMETRY='off'),check=True)
for name in ['RoutingPolicyChecks','RoutingBaselineChecks','RoutingConfigChecks','RoutingApplicationChecks','EngineChecks','NodeOrderingChecks','PeerChatChecks','PeerMessagingChecks','PeerLinkChecks']:
    command=[sdk,'run','--project',str(root/'tests/Baseline'/name),'-c','Release']
    if name=='PeerLinkChecks':command+=['--','--helper',str(helper)]
    results.append(run(name,command))
results.append(run('SwiftUI',['swift','build','-c','release','--arch','arm64']))
(out/'results.json').write_text(json.dumps(results,indent=2)+'\n')
raise SystemExit(any(x['exit'] for x in results))
