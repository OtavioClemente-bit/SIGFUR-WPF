from __future__ import annotations
from pathlib import Path
import re, json, hashlib, xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
checks=[]
def add(name, ok, detail=''):
    checks.append({'name':name,'ok':bool(ok),'detail':detail})

# XML validity
bad=[]
for p in ROOT.rglob('*.xaml'):
    try: ET.parse(p)
    except Exception as e: bad.append(f'{p.relative_to(ROOT)}: {e}')
add('Todos os XAML são XML válido', not bad, '; '.join(bad))

# New UI resources
controls=(ROOT/'Themes/Controls.xaml').read_text(encoding='utf-8')
adj=(ROOT/'Views/Finance/AdjustmentAccountsWindow.xaml').read_text(encoding='utf-8')
adjcs=(ROOT/'Views/Finance/AdjustmentAccountsWindow.xaml.cs').read_text(encoding='utf-8')
ea=(ROOT/'Views/Finance/ExercisePreviousDialogs.cs').read_text(encoding='utf-8')
off=(ROOT/'Views/Finance/OfficialSalaryReferenceWindow.cs').read_text(encoding='utf-8')
proj=(ROOT/'SIGFUR.Wpf.csproj').read_text(encoding='utf-8')
splash=(ROOT/'Views/SplashWindow.xaml').read_text(encoding='utf-8')

add('Versão 5.0.25', '<Version>5.0.25</Version>' in proj)
add('Estilo de acordeão registrado', 'x:Key="AccordionExpanderStyle"' in controls)
add('Quatro seções recolhíveis', sum(adj.count(f'x:Name="{x}"') for x in ['PeriodsExpander','BizuExpander','RightsExpander','ValuesExpander']) == 4)
add('Acordeão fecha as demais seções', 'SidebarExpander_Expanded' in adjcs and 'ReferenceEquals(section, opened)' in adjcs)
add('Posto/graduação compacto e legível', 'Posto / graduação do cálculo' in adj and 'FontSize="14" FontWeight="SemiBold"' in adj)
add('Consulta oficial não altera soldo', 'new OfficialSalaryReferenceWindow' in adjcs and 'GetSalaryByRankAsync' not in adjcs[adjcs.find('private void OfficialSalary_Click'):adjcs.find('private void CopyValue_Click')])
add('Fonte oficial do Planalto', 'L15167.htm' in off and 'somente leitura' in off)
add('EA com espaçamento ampliado', 'Margin = new Thickness(26, 24, 26, 22)' in ea and 'new GridLength(26)' in ea)
add('Ícone novo no executável', '<ApplicationIcon' in proj and (ROOT/'Assets/sigfur.ico').exists())
add('Loading preserva ícone anterior', 'sigfur-loading.ico' in splash and 'sigfur-loading.png' in splash)
add('Recursos de loading incluídos', 'Assets\\sigfur-loading.png' in proj and 'Assets\\sigfur-loading.ico' in proj)
add('Script de publicação 5.0.25', (ROOT/'COMPILAR_E_PUBLICAR_5.0.25.bat').exists())

# ICO signatures
for name in ['sigfur.ico','sigfur-loading.ico']:
    p=ROOT/'Assets'/name
    sig=p.read_bytes()[:4] if p.exists() else b''
    add(f'{name} é um ICO Windows real', sig == b'\x00\x00\x01\x00', sig.hex())

# Check XAML event handlers against paired code-behind where possible.
event_attrs={'Click','Loaded','Closing','Closed','SelectionChanged','TextChanged','Checked','Unchecked','Expanded','Collapsed','PreviewKeyDown','KeyDown','MouseDoubleClick','CellEditEnding','StateChanged','MouseLeftButtonDown'}
missing=[]
ns={'x':'http://schemas.microsoft.com/winfx/2006/xaml'}
for xp in ROOT.rglob('*.xaml'):
    txt=xp.read_text(encoding='utf-8')
    m=re.search(r'x:Class="([^"]+)"',txt)
    if not m: continue
    cp=xp.with_suffix('.xaml.cs')
    if not cp.exists(): continue
    cs=cp.read_text(encoding='utf-8')
    for attr in event_attrs:
        for handler in re.findall(rf'\b{attr}="([A-Za-z_][A-Za-z0-9_]*)"',txt):
            if not re.search(rf'\b{re.escape(handler)}\s*\(',cs):
                missing.append(f'{xp.relative_to(ROOT)}: {attr}={handler}')
add('Eventos XAML possuem métodos', not missing, '; '.join(missing))

# Lightweight C# lexer for unterminated strings/comments and balanced delimiters.
def scan_cs(text:str):
    stack=[]; i=0; line=1; state='code'; verbatim=False; interpolated=False
    pairs={')':'(',']':'[','}':'{'}
    while i<len(text):
        c=text[i]; n=text[i+1] if i+1<len(text) else ''
        if c=='\n': line+=1
        if state=='code':
            if c=='/' and n=='/': state='line'; i+=2; continue
            if c=='/' and n=='*': state='block'; i+=2; continue
            # string prefixes: $" @" $@" @$"
            if c=='"': state='string'; verbatim=False; i+=1; continue
            if c=='@' and n=='"': state='string'; verbatim=True; i+=2; continue
            if c=='$' and n=='"': state='string'; verbatim=False; i+=2; continue
            if c=='$' and n=='@' and i+2<len(text) and text[i+2]=='"': state='string'; verbatim=True; i+=3; continue
            if c=='@' and n=='$' and i+2<len(text) and text[i+2]=='"': state='string'; verbatim=True; i+=3; continue
            if c=="'": state='char'; i+=1; continue
            if c in '([{': stack.append((c,line))
            elif c in ')]}':
                if not stack or stack[-1][0]!=pairs[c]: return False,f'delimitador {c} inesperado na linha {line}'
                stack.pop()
            i+=1; continue
        if state=='line':
            if c=='\n': state='code'
            i+=1; continue
        if state=='block':
            if c=='*' and n=='/': state='code'; i+=2; continue
            i+=1; continue
        if state=='char':
            if c=='\\': i+=2; continue
            if c=="'": state='code'; i+=1; continue
            if c=='\n': return False,f'char não terminado na linha {line-1}'
            i+=1; continue
        if state=='string':
            if verbatim:
                if c=='"' and n=='"': i+=2; continue
                if c=='"': state='code'; i+=1; continue
                i+=1; continue
            else:
                if c=='\\': i+=2; continue
                if c=='"': state='code'; i+=1; continue
                if c=='\n': return False,f'string não terminada na linha {line-1}'
                i+=1; continue
    if state not in ('code','line'): return False,f'estado final {state}'
    if stack: return False,f'delimitadores abertos: {stack[-5:]}'
    return True,''

csbad=[]
for rel in ['Views/Finance/AdjustmentAccountsWindow.xaml.cs','Views/Finance/OfficialSalaryReferenceWindow.cs','Views/Finance/ExercisePreviousDialogs.cs','App.xaml.cs']:
    p=ROOT/rel
    ok,detail=scan_cs(p.read_text(encoding='utf-8-sig'))
    if not ok: csbad.append(f'{p.relative_to(ROOT)}: {detail}')
add('Arquivos C# alterados sem strings/comentários/delimitadores quebrados', not csbad, '; '.join(csbad))

result={'version':'5.0.25','passed':sum(x['ok'] for x in checks),'total':len(checks),'checks':checks}
out=ROOT/'VALIDACAO_UI_5.0.25.json'
out.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(result,ensure_ascii=False,indent=2))
raise SystemExit(0 if result['passed']==result['total'] else 1)
