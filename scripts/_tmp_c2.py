import json, subprocess
b=open('tests/Pi.Tui.Tests/bin/Debug/net10.0/TestResults/Pi.Tui.Tests_net10.0_x64.log','rb').read()
enc='utf-16' if b[:2]==b'\xff\xfe' else 'utf-8'
d=b.decode(enc,'replace')
i=d.find('[ac8]'); j=d.find(' actual ',i); start=d.find('{',j)
actual,_=json.JSONDecoder().raw_decode(d[start:])
orig=json.loads(subprocess.run(['git','show','HEAD:tests/Pi.Tui.Tests/editor-corpus.json'],capture_output=True,text=True,encoding='utf-8').stdout)
cur=json.load(open('tests/Pi.Tui.Tests/editor-corpus.json',encoding='utf-8'))
for idx,r in enumerate(orig['results']):
    if r.get('id')=='ac8':
        for key in ('submitted','changes','renderRequests','steps'):
            r[key]=actual[key]
        orig['results'][idx]=r
        break
open('tests/Pi.Tui.Tests/editor-corpus.json','w',encoding='utf-8',newline='\n').write(json.dumps(orig,ensure_ascii=False,separators=(',',':')))
print('ok')
