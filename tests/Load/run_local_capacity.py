"""Compatibility entry for the shared synthetic coordinator; private fixtures stay private."""
from pathlib import Path
import argparse
import json
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'eng/testing'))
from run_synthetic import run_fixture

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--fixture',type=Path,required=True)
    parser.add_argument('--report',type=Path,required=True)
    parser.add_argument('--duration-seconds',type=int,default=120)
    parser.add_argument('--port',type=int,default=5091)
    parser.add_argument('--run-mode',choices=('mixed','separate'),default='mixed')
    args=parser.parse_args()
    if not 1<=args.duration_seconds<=86400 or not 1024<=args.port<=65535: parser.error('Invalid duration or port')
    count=json.loads((args.fixture/'setup.json').read_text(encoding='utf-8-sig'))['count']
    return run_fixture(args.fixture,args.report,count,args.duration_seconds,'load' if args.run_mode=='mixed' else 'separate',args.port)

if __name__=='__main__':
    try: raise SystemExit(main())
    except Exception as error: print('Capacity check failed: '+type(error).__name__);raise SystemExit(1)
