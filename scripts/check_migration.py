"""Portable, read-only product naming check. No game or launcher execution."""
from pathlib import Path
import argparse,json,xml.etree.ElementTree as ET

def inspect(root):
    launcher=root/'src/launcher/src'
    xaml=ET.parse(launcher/'MainWindow.xaml').getroot()
    project=ET.parse(launcher/'SplinterCellCNLauncher.csproj').getroot()
    return {'window_title':xaml.attrib['Title'],
            'product':project.findtext('.//Product'),
            'assembly':project.findtext('.//AssemblyName'),
            'release':json.loads((root/'release-identity.json').read_text('utf-8-sig')),
            'vss_emf_policy_present':(root/'src/native/client/hooks/gameplay/src/vss_emf_glass_policy.rs').is_file(),
            'legacy_wire_identity_retained':'c.Product != "SCBL LAN"' in (launcher/'Services/LanConnection.cs').read_text('utf-8-sig')}

def main():
    p=argparse.ArgumentParser();p.add_argument('--root',type=Path,default=Path(__file__).resolve().parents[1]);p.add_argument('--require-fifth-freedom',action='store_true');a=p.parse_args()
    result=inspect(a.root);print(json.dumps(result,sort_keys=True,ensure_ascii=False))
    if a.require_fifth_freedom:
        assert result['window_title']==result['product']=='Fifth Freedom'
        assert result['assembly']=='FifthFreedom'
        assert result['vss_emf_policy_present']
        assert result['legacy_wire_identity_retained']
    return 0
if __name__=='__main__':raise SystemExit(main())
