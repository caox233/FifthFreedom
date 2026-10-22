"""Symbols must match the PE's CodeView identity; never accept a stale PDB."""
from pathlib import Path
import sys,tempfile,unittest,json
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'scripts'))
import native_symbols as symbols

class NativeSymbolsTests(unittest.TestCase):
 def test_stale_pdb_rejected(self):
  with tempfile.TemporaryDirectory() as folder:
   root=Path(folder);binary=root/'hooks.dll';binary.write_bytes(b'binary');binary.with_suffix('.pdb').write_bytes(b'stale')
   with patch.object(symbols,'pe_identity',return_value={'pdb_guid':'a','pdb_age':1}),patch.object(symbols,'pdb_identity',return_value={'pdb_guid':'b','pdb_age':1}):
    with self.assertRaisesRegex(ValueError,'PDB identity mismatch'):symbols.archive(root,'fixture',{'main':binary})
   self.assertEqual(list(root.rglob('MODULES.json')),[])
 def test_matching_symbols_record_all_binary_hashes(self):
  with tempfile.TemporaryDirectory() as folder:
   root=Path(folder);components={}
   for role in ('main','gameplay','server'):
    binary=root/(role+'.dll');binary.write_bytes(role.encode());binary.with_suffix('.pdb').write_bytes((role+'-symbols').encode());components[role]=binary
   with patch.object(symbols,'pe_identity',return_value={'time_date_stamp':1,'size_of_image':2,'pdb_guid':'a','pdb_age':1}),patch.object(symbols,'pdb_identity',return_value={'pdb_guid':'a','pdb_age':1}):
    out=Path(symbols.archive(root,'fixture',components))
   rows=json.loads((out/'MODULES.json').read_text(encoding='utf-8'))['modules']
   self.assertEqual(set(rows),set(components))
   for role,row in rows.items():
    self.assertEqual(row['binary_sha256'],symbols.hashlib.sha256(components[role].read_bytes()).hexdigest())
    self.assertTrue((out/row['pdb']).is_file())

if __name__=='__main__':unittest.main()
