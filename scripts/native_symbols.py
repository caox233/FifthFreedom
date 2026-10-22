"""Archive exact native symbols separately from the player package. No upload."""
import hashlib,json,struct,shutil,datetime
from pathlib import Path

def pe_identity(path):
    data=Path(path).read_bytes();u=lambda p:struct.unpack_from('<I',data,p)[0]
    pe=u(0x3c)
    if data[:2]!=b'MZ' or data[pe:pe+4]!=b'PE\0\0':raise ValueError('Invalid PE')
    count=struct.unpack_from('<H',data,pe+6)[0];size=struct.unpack_from('<H',data,pe+20)[0]
    optional=pe+24;magic=struct.unpack_from('<H',data,optional)[0]
    directory=optional+(96 if magic==0x10b else 112 if magic==0x20b else 0)
    debug_rva=u(directory+6*8);debug_size=u(directory+6*8+4)
    def offset(rva):
        for i in range(count):
            at=optional+size+i*40;va=u(at+12);raw_size=u(at+16)
            if va<=rva<va+raw_size:return u(at+20)+rva-va
        raise ValueError('PE debug RVA is not file backed')
    start=offset(debug_rva)
    for at in range(start,start+debug_size,28):
        if u(at+12)!=2:continue
        p=u(at+24)
        if data[p:p+4]==b'RSDS':
            return {'time_date_stamp':u(pe+8),'size_of_image':u(optional+56),
                    'pdb_guid':data[p+4:p+20].hex(),'pdb_age':u(p+20)}
    raise ValueError('No RSDS identity; cannot archive unbound symbols')

def pdb_identity(path):
    data=Path(path).read_bytes();u=lambda p:struct.unpack_from('<I',data,p)[0]
    if not data.startswith(b'Microsoft C/C++ MSF 7.00'):raise ValueError('Unsupported PDB')
    block=u(32);size=u(44);block_map=u(52)
    if block not in (512,1024,2048,4096):raise ValueError('Invalid PDB block size')
    blocks=[u(block_map*block+i*4) for i in range((size+block-1)//block)]
    directory=b''.join(data[n*block:(n+1)*block] for n in blocks)[:size]
    get=lambda p:struct.unpack_from('<I',directory,p)[0]
    count=get(0);lengths=[get(4+i*4) for i in range(count)];at=4+count*4
    for i,length in enumerate(lengths):
        n=0 if length==0xffffffff else (length+block-1)//block
        ids=[get(at+j*4) for j in range(n)];at+=n*4
        if i==1:
            info=b''.join(data[k*block:(k+1)*block] for k in ids)[:length]
            return {'pdb_guid':info[12:28].hex(),'pdb_age':struct.unpack_from('<I',info,8)[0]}
    raise ValueError('Missing PDB info stream')

def archive(root,release,components):
    stamp=datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
    destination=Path(root)/'symbols'/(release+'-'+stamp);destination.mkdir(parents=True)
    rows={}
    for role in ('main','gameplay','server'):
        binary=Path(components[role]);pdb=binary.with_suffix('.pdb')
        pe=pe_identity(binary);identity=pdb_identity(pdb)
        if any(pe[k]!=v for k,v in identity.items()):raise ValueError('PDB identity mismatch: '+role)
        output=destination/(role+'.pdb');shutil.copyfile(pdb,output)
        rows[role]={**pe,'binary_sha256':hashlib.sha256(binary.read_bytes()).hexdigest(),
                    'pdb_sha256':hashlib.sha256(output.read_bytes()).hexdigest(),'pdb':output.name}
    (destination/'MODULES.json').write_text(json.dumps({'release_id':release,'modules':rows},indent=2)+'\n',encoding='utf-8')
    return str(destination)
