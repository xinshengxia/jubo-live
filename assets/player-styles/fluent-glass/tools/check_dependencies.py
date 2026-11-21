"""Check the project's simple pinned/ranged requirements without needing pip.

Only ordinary package names and numeric ==, !=, <, <=, >, >= constraints are
accepted. More complex requirements require updating this checker explicitly.
"""
import importlib.metadata
import operator
from pathlib import Path
import re
import sys


def check(requirements):
    comparisons={'==':operator.eq,'!=':operator.ne,'<':operator.lt,
                 '<=':operator.le,'>':operator.gt,'>=':operator.ge}
    for line in Path(requirements).read_text('utf-8-sig').splitlines():
        line=line.split('#',1)[0].strip()
        if not line:continue
        match=re.fullmatch(r'([A-Za-z0-9_.-]+)\s*(.*)',line)
        if not match:raise ValueError('Unsupported requirement: '+line)
        name,spec=match.groups()
        installed=importlib.metadata.version(name)
        if not re.fullmatch(r'\d+(?:\.\d+)*',installed):
            raise ValueError('A stable numeric package version is required: '+name)
        actual=tuple(int(v) for v in installed.split('.'))
        for constraint in filter(None,(v.strip() for v in spec.split(','))):
            rule=re.fullmatch(r'(==|!=|<=|>=|<|>)\s*(\d+(?:\.\d+)*)',constraint)
            if not rule:raise ValueError('Unsupported constraint: '+constraint)
            comparison,version=rule.groups()
            wanted=tuple(int(v) for v in version.split('.'))
            width=max(len(actual),len(wanted))
            if not comparisons[comparison](actual+(0,)*(width-len(actual)),
                                           wanted+(0,)*(width-len(wanted))):
                raise ValueError(f'{name} {installed} does not satisfy {constraint}')
    # Also load the native modules, catching incomplete/broken installations.
    from PIL import Image
    import resvg_py


if __name__=='__main__':
    try:check(sys.argv[1])
    except (OSError,ValueError,ImportError,importlib.metadata.PackageNotFoundError) as error:
        print(str(error),file=sys.stderr)
        raise SystemExit(1)
