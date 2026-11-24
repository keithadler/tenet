"""Add Eta437.boxEta : forall f : Bool -> Box, f = Box.mk := fun f => Eq.refl f to the eta437 export.
The type comes from boxType's hypothesis and the value from boxValue, so every piece was written by Lean."""
import json, sys
L=[json.loads(l) for l in open(sys.argv[1])]
names={}
for r in L:
    if 'in' in r:
        k='str' if 'str' in r else 'num'
        names[r['in']]=(r[k]['pre'],str(r[k][k if k=='str' else 'i']))
def full(i):
    if i==0: return ''
    p,s=names[i]; f=full(p); return (f+'.' if f else '')+s
ex={r['ie']:r for r in L if 'ie' in r}
thm={full(r['thm']['name']):r['thm'] for r in L if 'thm' in r}
bt=ex[thm['Eta437.boxType']['type']]['forallE']
dom_h=ex[bt['body']]['forallE']['type']
maxie=max(ex); maxin=max(names)
pre=[k for k in names if full(k)=='Eta437'][0]
d=lambda o: json.dumps(o,separators=(',',':'))
out=[d(r) for r in L]
out.append(d({"in":maxin+1,"str":{"pre":pre,"str":"boxEta"}}))
out.append(d({"ie":maxie+1,"forallE":{"binderInfo":bt['binderInfo'],"body":dom_h,"name":bt['name'],"type":bt['type']}}))
out.append(d({"thm":{"all":[maxin+1],"levelParams":[],"name":maxin+1,"type":maxie+1,"value":thm['Eta437.boxValue']['value']}}))
open(sys.argv[2],'w').write('\n'.join(out)+'\n')
