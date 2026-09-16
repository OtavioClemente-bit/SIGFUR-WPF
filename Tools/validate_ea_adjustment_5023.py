from __future__ import annotations
from pathlib import Path
import json, re, shutil, tempfile, zipfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
checks=[]
def add(name, ok, details=None): checks.append({'name':name,'ok':bool(ok),'details':details})

# Version
csproj=(ROOT/'SIGFUR.Wpf.csproj').read_text(encoding='utf-8')
add('Versão 5.0.23', '<Version>5.0.23</Version>' in csproj)

# XAML integrity and handlers
for name in ['AdjustmentAccountsWindow','AdjustmentBulletinWindow']:
    xaml=ROOT/'Views/Finance'/f'{name}.xaml'
    cs=ROOT/'Views/Finance'/f'{name}.xaml.cs'
    try:
        ET.parse(xaml); xml_ok=True; xml_error=''
    except Exception as e:
        xml_ok=False; xml_error=str(e)
    xt=xaml.read_text(encoding='utf-8'); ct=cs.read_text(encoding='utf-8')
    handlers=set(re.findall(r'\b(?:Click|SelectionChanged|TextChanged|PreviewKeyDown|MouseDoubleClick|CellEditEnding)="([A-Za-z_][A-Za-z0-9_]*)"',xt))
    missing=sorted(h for h in handlers if not re.search(r'\b'+re.escape(h)+r'\s*\(',ct))
    add(f'{name}: XAML e eventos', xml_ok and not missing, {'xml_error':xml_error,'missing_handlers':missing})

adjx=(ROOT/'Views/Finance/AdjustmentAccountsWindow.xaml').read_text(encoding='utf-8')
adjc=(ROOT/'Views/Finance/AdjustmentAccountsWindow.xaml.cs').read_text(encoding='utf-8')
bulx=(ROOT/'Views/Finance/AdjustmentBulletinWindow.xaml').read_text(encoding='utf-8')
bulc=(ROOT/'Views/Finance/AdjustmentBulletinWindow.xaml.cs').read_text(encoding='utf-8')

old_names=['MilitaryBox','VacationAdditionalCheck','VacationIndemnityCheck','ChristmasCheck','ReceivedChristmasCheck','PecuniaryCheck']
add('Ajuste sem seleção de militar na tela principal', all(n not in adjx+adjc for n in old_names), {'old_controls_found':[n for n in old_names if n in adjx+adjc]})
new_names=['VacationAdditionalEntitlementBox','VacationIndemnityEntitlementBox','ChristmasEntitlementBox','ReceivedChristmasStatusBox','PecuniaryEntitlementBox']
add('Direitos separados e legíveis', all(n in adjx and n in adjc for n in new_names) and adjx.count('Padding="12" Margin="0,6"')>=5)
add('Militares escolhidos somente no boletim', 'Gerar boletim / escolher militares' in adjx and 'MilitaryFilterText' in bulx and '.Where(x => SameRank(x.Rank, Settings.Rank))' in bulc)
add('Boletim casa com o ajuste', 'settings.Rank = military.Rank' not in bulc and 'GetSalaryByRankAsync(military.Rank)' not in bulc and 'ApplyMilitaryValues' not in bulc and 'AdjustmentAccountsService.Calculate(settings, _rubrics)' in bulc)
add('Compatibilidade de posto completo/abreviado', 'MilitaryRankService.GetOrder(left)' in adjc and 'MilitaryRankService.GetOrder(left)' in bulc)

excel=(ROOT/'Services/ExercisePreviousExcelService.cs').read_text(encoding='utf-8')
ea_window=(ROOT/'Views/Finance/ExercisePreviousWindow.cs').read_text(encoding='utf-8')
ea_dialog=(ROOT/'Views/Finance/ExercisePreviousDialogs.cs').read_text(encoding='utf-8')
ea_repo=(ROOT/'Services/ExercisePreviousRepository.cs').read_text(encoding='utf-8')
add('EA remove proteção somente da cópia gerada', 'File.Copy(_assets.TemplateWorkbook, output, true);' in excel and 'RemoveSheetProtection(output);' in excel and 'sheetProtection|workbookProtection' in excel)
add('PDF EA exportado primeiro no TEMP e validado', 'temporaryPdf' in excel and 'new FileInfo(temporaryPdf).Length == 0' in excel and 'File.Copy(temporaryPdf, pdf, true)' in excel)
add('Lançamento profissional por nome do código', 'ExercisePreviousEntryEditorWindow' in ea_dialog and 'CodeOption' in ea_dialog and 'Display => $"{Order:00}' in ea_dialog and 'Novo lançamento' in ea_window)
add('Lançamentos evitam conflito de competência/código', 'FindMatchingEntry' in ea_window and 'GroupBy(x => (x.CodeOrder, x.Year, x.Month))' in ea_repo)
add('Lançamentos alimentam recebido e devido', 'FillLaunchSheet(workbook, "Contracheque - F Financeira", p.Entries, true' in excel and 'FillLaunchSheet(workbook, "Lançar Valor Devido", p.Entries, false' in excel)

# Macro/protection package proof using same removal semantics.
template=ROOT/'Resources/EA/EA_IPCAE_Template.xlsm'
macro_before=False; protected_before=0; macro_after=False; protected_after=-1
try:
    with zipfile.ZipFile(template) as z:
        macro_before='xl/vbaProject.bin' in z.namelist() and z.getinfo('xl/vbaProject.bin').file_size>0
        protected_before=sum(1 for n in z.namelist() if ((n.startswith('xl/worksheets/') and n.endswith('.xml')) or n=='xl/workbook.xml') and (b'sheetProtection' in z.read(n) or b'workbookProtection' in z.read(n)))
    pat=re.compile(rb'<(?:[A-Za-z_][\w.-]*:)?(?:sheetProtection|workbookProtection)\b[^>]*/>|<(?:[A-Za-z_][\w.-]*:)?(?:sheetProtection|workbookProtection)\b[^>]*>.*?</(?:[A-Za-z_][\w.-]*:)?(?:sheetProtection|workbookProtection)>',re.I|re.S)
    tmp=Path(tempfile.mkstemp(suffix='.xlsm')[1])
    try:
        with zipfile.ZipFile(template) as zin, zipfile.ZipFile(tmp,'w',zipfile.ZIP_DEFLATED) as zout:
            for info in zin.infolist():
                data=zin.read(info.filename)
                if (info.filename.startswith('xl/worksheets/') and info.filename.endswith('.xml')) or info.filename=='xl/workbook.xml':
                    data=pat.sub(b'',data)
                zout.writestr(info,data)
        with zipfile.ZipFile(tmp) as z:
            macro_after='xl/vbaProject.bin' in z.namelist() and z.getinfo('xl/vbaProject.bin').file_size>0
            protected_after=sum(1 for n in z.namelist() if ((n.startswith('xl/worksheets/') and n.endswith('.xml')) or n=='xl/workbook.xml') and (b'sheetProtection' in z.read(n) or b'workbookProtection' in z.read(n)))
    finally: tmp.unlink(missing_ok=True)
except Exception as e:
    add('Teste do pacote XLSM',False,{'error':str(e)})
else:
    add('Teste do pacote XLSM', macro_before and protected_before>0 and macro_after and protected_after==0, {'macro_before':macro_before,'protected_entries_before':protected_before,'macro_after':macro_after,'protected_entries_after':protected_after})

# Modified C# brace sanity.
modified=['Services/ExercisePreviousExcelService.cs','Services/ExercisePreviousRepository.cs','Views/Finance/ExercisePreviousDialogs.cs','Views/Finance/ExercisePreviousWindow.cs','Views/Finance/AdjustmentAccountsWindow.xaml.cs','Views/Finance/AdjustmentBulletinWindow.xaml.cs']
brace={}
for rel in modified:
    text=(ROOT/rel).read_text(encoding='utf-8')
    brace[rel]=(text.count('{'),text.count('}'))
add('Balanceamento estrutural dos arquivos alterados',all(a==b for a,b in brace.values()),brace)

result={'version':'5.0.23','checks':len(checks),'passed':sum(x['ok'] for x in checks),'failed':sum(not x['ok'] for x in checks),'items':checks}
out=ROOT/'VALIDACAO_EA_AJUSTE_5.0.23.json'
out.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'checks':result['checks'],'passed':result['passed'],'failed':result['failed']},ensure_ascii=False))
if result['failed']:
    for c in checks:
        if not c['ok']: print('FAIL:',c['name'],c.get('details'))
    raise SystemExit(1)
