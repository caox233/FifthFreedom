"""Exercise the production observer in an owned x86 process, never the game.
Usage: python test_native.py --source-root REPO --output NEW_DIRECTORY
An older source root without the observer verifies baseline/rollback behavior.
"""
from pathlib import Path
import argparse,sys,subprocess,json

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,default=Path(__file__).resolve().parents[2])
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args();root=args.source_root.resolve();out=args.output.resolve();out.mkdir(parents=True,exist_ok=False)
    # Use the invoking repository's installed toolchain, not baseline overrides.
    sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'scripts'))
    import build
    cargo,env=build.native_env();rustc=str(Path(cargo).with_name('rustc.exe'))
    hooks=root/'src/native/client/hooks/src';module=hooks/'exception_record.rs'
    integrated=module.is_file() and 'exception_record::init(&log_dir)' in (hooks/'lib.rs').read_text(encoding='utf-8')
    code=Path(__file__).with_name('native_fixture.rs').read_text(encoding='utf-8')
    if integrated:code='#[path = '+json.dumps(module.as_posix())+'] mod exception_record;\n'+code
    source=out/'fixture.rs';source.write_text(code,encoding='utf-8');exe=out/'fixture.exe'
    command=[rustc,'--edition=2021','--target','i686-pc-windows-msvc','-C','opt-level=1','-C','debuginfo=2',str(source),'-o',str(exe)]
    if integrated:command+=['--cfg','observer']
    compiled=subprocess.run(command,env=env,capture_output=True)
    (out/'compile.stdout.txt').write_bytes(compiled.stdout);(out/'compile.stderr.txt').write_bytes(compiled.stderr)
    assert compiled.returncode==0,compiled.stderr.decode(errors='replace')
    results=[]
    for mode in ('handled','unhandled'):
        directory=out/mode;directory.mkdir();cmd=[str(exe),str(directory),mode]
        result=subprocess.run(cmd,capture_output=True,timeout=15)
        (out/(mode+'.stdout.txt')).write_bytes(result.stdout);(out/(mode+'.stderr.txt')).write_bytes(result.stderr)
        records=[]
        for p in directory.glob('exception-observations-*.jsonl'):
            data=p.read_bytes();assert len(data)<=32768 and len(data)%4096==0
            records.extend(json.loads(data[i:i+4096]) for i in range(0,len(data),4096))
        assert result.returncode==(0 if mode=='handled' else 0xc0000005)
        assert result.stdout==(b'HANDLED_CONTINUED count=12\r\n' if b'\r\n' in result.stdout else b'HANDLED_CONTINUED count=12\n') if mode=='handled' else result.stdout==b''
        assert result.stderr==b''
        assert len(records)==((8 if mode=='handled' else 1) if integrated else 0)
        if integrated and mode=='handled':assert sorted(r['Sequence'] for r in records)==list(range(4,12))
        if integrated and mode=='unhandled':assert records[0]['NearNull'] and records[0]['Access']==1 and records[0]['Location'][2]>0
        row={'command':cmd,'mode':mode,'observer_integrated':integrated,'exit':result.returncode,'stdout':result.stdout.decode(),'stderr':result.stderr.decode(),'records':len(records)}
        results.append(row);print(json.dumps(row),flush=True)
    (out/'RESULT.json').write_text(json.dumps({'compile_command':command,'compile_exit':compiled.returncode,'results':results},indent=2),encoding='utf-8')

if __name__=='__main__':main()
