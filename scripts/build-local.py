#!/usr/bin/env python3
from pathlib import Path
from datetime import datetime,timezone
import argparse,os,subprocess,shutil,hashlib,json
root=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser();parser.add_argument('--dmg',action='store_true');args=parser.parse_args()
sdk=os.environ.get('MADEGO_DOTNET',str(root/'work/toolchains/dotnet/dotnet'))
version=(root/'VERSION').read_text().strip();stamp=datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')
out=root/'artifacts'/('local-'+stamp);out.mkdir(parents=True)
app=out/'Madego.app';contents=app/'Contents';(contents/'MacOS').mkdir(parents=True);core=contents/'Resources/Core';core.mkdir(parents=True)
env=dict(os.environ,DOTNET_CLI_TELEMETRY_OPTOUT='1',DOTNET_NOLOGO='1',GOTELEMETRY='off')
def run(command):subprocess.run(command,cwd=root,env=env,check=True)
run(['swift','build','-c','release','--arch','arm64'])
binary=subprocess.check_output(['swift','build','-c','release','--arch','arm64','--show-bin-path'],cwd=root,text=True).strip()
shutil.copy2(Path(binary)/'Madego',contents/'MacOS/Madego');shutil.copy2(root/'packaging/App-Info.plist',contents/'Info.plist')
run([sdk,'publish','src/Madego.Broker/Madego.Broker.csproj','-c','Release','-r','osx-arm64','--self-contained','true','-p:PublishTrimmed=false','-p:PublishAot=false','-p:PublishSingleFile=false','-p:DebugType=None','-o',str(core)])
run(['swiftc','-target','arm64-apple-macosx14.0','-warnings-as-errors','-emit-library','-module-name','MadegoKeychain','src/Madego.Platform/KeychainBridge.swift','-o',str(core/'libMadegoKeychain.dylib'),'-framework','Security'])
baselineHelper=root/'windows-baseline/src/Madyar.Link/runtime/macos-arm64/MadyarLink'
if baselineHelper.exists():
    expected='79dab825e2c1cc55c3b86206a351bab6143bedc39eef6f4b4cae4f1cffa5ae77'
    if hashlib.sha256(baselineHelper.read_bytes()).hexdigest()!=expected:raise SystemExit('Frozen helper hash mismatch')
    shutil.copy2(baselineHelper,core/'MadyarLink')
else:
    go=os.environ.get('MADEGO_GO',str(root/'work/toolchains/go-parent/go/bin/go'))
    subprocess.run([go,'build','-trimpath','-ldflags=-s -w','-o',str(core/'MadyarLink'),'.'],cwd=root/'shared-source/src/Madyar.Link',env=dict(env,CGO_ENABLED='0',GOOS='darwin',GOARCH='arm64'),check=True)
notices=contents/'Resources/Notices';notices.mkdir(parents=True)
for name in ['LICENSE','THIRD_PARTY_NOTICES.md']:shutil.copy2(root/name,notices/name)
shutil.copytree(root/'shared-source/src/Madyar.Link/LICENSES',notices/'Go-Link')
shutil.copytree(root/'shared-source/src/EgorVpn/assets/licenses',notices/'Managed')
shutil.copytree(root/'shared-source/src/EgorVpn/Routing/data',notices/'Routing')
# Sign native libraries/executables from inside out. Ad-hoc local signing is not Developer ID.
for p in sorted(core.rglob('*')):
    if p.is_file() and p.suffix in ('.dylib','.so'):run(['codesign','--force','--sign','-',str(p)])
run(['codesign','--force','--sign','-',str(core/'MadyarLink')])
run(['codesign','--force','--sign','-','--entitlements','packaging/Broker.entitlements',str(core/'Madego.Broker')])
run(['codesign','--force','--sign','-',str(app)])
run(['codesign','--verify','--deep','--strict',str(app)])
files={str(p.relative_to(app)):hashlib.sha256(p.read_bytes()).hexdigest() for p in app.rglob('*') if p.is_file()}
manifest={'version':version,'architecture':'arm64','minimum_macos':'14.0','build_utc':stamp,'app':str(app),'signing':'ad-hoc local development only','vpn_available':False,'notarized':False,'files':files}
if args.dmg:
    dmg=out/('Madego-'+version+'-macOS-arm64-local.dmg')
    run(['hdiutil','create','-volname','Madego Local','-srcfolder',str(app),'-format','UDZO','-ov',str(dmg)])
    manifest['dmg']={'path':str(dmg),'sha256':hashlib.sha256(dmg.read_bytes()).hexdigest(),'bytes':dmg.stat().st_size}
(out/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
print('LOCAL_APP='+str(app));print('BUILD_MANIFEST='+str(out/'manifest.json'))
