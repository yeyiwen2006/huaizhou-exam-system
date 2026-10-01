import argparse
from pathlib import Path
from xml.etree.ElementTree import ParseError
from evidence import aggregate, validate_report, write_json

parser=argparse.ArgumentParser(description='Validate evidence, optionally aggregate required CI profiles')
parser.add_argument('reports',type=Path,nargs='+')
parser.add_argument('--require-profile',action='append')
parser.add_argument('--output',type=Path)
args=parser.parse_args()
try:
    if args.require_profile:
        value=aggregate(args.reports,args.require_profile)
    else:
        values=[validate_report(path) for path in args.reports]
        if any(v['status']!='passed' for v in values): raise ValueError('Required report did not pass')
        value={'status':'passed'}
    if args.output: write_json(args.output,value)
    print('Evidence validation passed')
except (ValueError,KeyError,TypeError,OSError,ParseError) as error:
    parser.exit(1,f'Evidence validation failed: {type(error).__name__}\n')
