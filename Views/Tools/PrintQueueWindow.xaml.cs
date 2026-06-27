using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace SIGFUR.Wpf.Views.Tools;

public partial class PrintQueueWindow : Window
{
    private readonly ObservableCollection<PrintRow> _items=[]; private readonly CancellationTokenSource _closeCts=new(); private CancellationTokenSource? _printCts;
    private readonly string _settingsFile = Path.Combine(App.Paths.DataDirectory, "fila_impressao_wpf.json");
    private static readonly HashSet<string> Supported=new(StringComparer.OrdinalIgnoreCase){".pdf",".doc",".docx",".docm",".rtf",".txt",".odt",".xls",".xlsx",".xlsm",".csv",".ods",".ppt",".pptx",".pptm",".odp",".jpg",".jpeg",".png",".bmp",".tif",".tiff"};
    public PrintQueueWindow(IEnumerable<string>? initialPaths = null, int? initialCopies = null)
    {
        InitializeComponent(); App.UiState.Attach(this); QueueGrid.ItemsSource = _items;
        var startupPaths = (initialPaths ?? Array.Empty<string>()).Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
        LoadSettings();
        if (initialCopies.HasValue) CopiesBox.Text = Math.Clamp(initialCopies.Value, 1, 99).ToString(CultureInfo.InvariantCulture);
        Loaded += async (_, _) => { await LoadPrintersAsync(); if (startupPaths.Count > 0) AddPaths(startupPaths); UpdateStatus(); };
        Closed += (_, _) => { SaveSettings(); _closeCts.Cancel(); _printCts?.Cancel(); };
    }
    private async void RefreshPrinters_Click(object s,RoutedEventArgs e)=>await LoadPrintersAsync();
    private async Task LoadPrintersAsync()
    {
        var current = PrinterBox.Text;
        var list = await Task.Run(ListPrinters);
        PrinterBox.ItemsSource = list;
        if (!string.IsNullOrWhiteSpace(current)) PrinterBox.Text = current;
        else
        {
            var defaultPrinter = await Task.Run(GetDefaultPrinter);
            if (!string.IsNullOrWhiteSpace(defaultPrinter) && list.Contains(defaultPrinter, StringComparer.OrdinalIgnoreCase)) PrinterBox.Text = defaultPrinter;
            else if (list.Count > 0) PrinterBox.SelectedIndex = 0;
        }
        QueueStatusText.Text = list.Count == 0 ? "Nenhuma impressora encontrada pelo Windows." : $"{list.Count} impressora(s) disponível(is).";
    }
    private static List<string> ListPrinters(){try{var psi=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true};psi.ArgumentList.Add("-NoProfile");psi.ArgumentList.Add("-Command");psi.ArgumentList.Add("Get-Printer | Select-Object -ExpandProperty Name");using var p=Process.Start(psi)!;var output=p.StandardOutput.ReadToEnd();p.WaitForExit(10000);return output.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries).Select(x=>x.Trim()).Where(x=>x.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x).ToList();}catch{return[];}}
    private static string GetDefaultPrinter()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("(Get-CimInstance Win32_Printer | Where-Object {$_.Default -eq $true} | Select-Object -First 1 -ExpandProperty Name)");
            using var process = Process.Start(psi); if (process is null) return string.Empty;
            var output = process.StandardOutput.ReadToEnd().Trim(); process.WaitForExit(10000); return output;
        }
        catch { return string.Empty; }
    }
    private void AddFiles_Click(object s,RoutedEventArgs e){var d=new OpenFileDialog{Multiselect=true,Filter="Documentos suportados|*.pdf;*.doc;*.docx;*.rtf;*.txt;*.odt;*.xls;*.xlsx;*.csv;*.ods;*.ppt;*.pptx;*.odp;*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff|Todos|*.*"};if(d.ShowDialog(this)==true)AddPaths(d.FileNames);}
    private void AddFolder_Click(object s,RoutedEventArgs e){var d=new OpenFolderDialog{Title="Pasta com arquivos"};if(d.ShowDialog(this)==true)AddPaths([d.FolderName]);}
    private void AddPaths(IEnumerable<string> paths)
    {
        foreach (var path in ExpandPaths(paths))
        {
            if (!File.Exists(path) || !Supported.Contains(Path.GetExtension(path))) continue;
            if (_items.Any(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
            _items.Add(new PrintRow(path));
        }
        Renumber(); UpdateStatus();
    }
    private static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path)) { yield return path; continue; }
            if (!Directory.Exists(path)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToList(); }
            catch { continue; }
            foreach (var file in files) yield return file;
        }
    }
    private void Remove_Click(object s,RoutedEventArgs e){foreach(var x in QueueGrid.SelectedItems.Cast<PrintRow>().ToList())_items.Remove(x);Renumber();UpdateStatus();}
    private void Clear_Click(object s,RoutedEventArgs e){if(_printCts is not null)return;_items.Clear();QueueProgress.Value=0;UpdateStatus();}
    private void MarkPending_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in QueueGrid.SelectedItems.Cast<PrintRow>()) row.Status = "Pendente";
        QueueGrid.Items.Refresh(); UpdateStatus();
    }
    private void MoveUp_Click(object s,RoutedEventArgs e)=>Move(-1);private void MoveDown_Click(object s,RoutedEventArgs e)=>Move(1);
    private void Move(int delta){if(QueueGrid.SelectedItem is not PrintRow row)return;var i=_items.IndexOf(row);var j=i+delta;if(j<0||j>=_items.Count)return;_items.Move(i,j);Renumber();QueueGrid.SelectedItem=row;QueueGrid.ScrollIntoView(row);}
    private void Renumber(){for(int i=0;i<_items.Count;i++)_items[i].Order=i+1;QueueGrid.Items.Refresh();DropHint.Visibility=_items.Count==0?Visibility.Visible:Visibility.Collapsed;}
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths);
        e.Handled = true;
    }

    private void OpenLocation_Click(object s,RoutedEventArgs e){if(QueueGrid.SelectedItem is not PrintRow row)return;Process.Start(new ProcessStartInfo("explorer.exe",$"/select,\"{row.Path}\""){UseShellExecute=true});}
    private void Stop_Click(object s,RoutedEventArgs e)=>_printCts?.Cancel();
    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_printCts is not null) { SigfurDialog.Show(this,"A fila já está sendo impressa.","Impressão",MessageBoxButton.OK,MessageBoxImage.Information); return; }
        var targets = OnlySelectedBox.IsChecked == true ? QueueGrid.SelectedItems.Cast<PrintRow>().ToList() : _items.ToList();
        if (targets.Count == 0 || string.IsNullOrWhiteSpace(PrinterBox.Text)) { SigfurDialog.Show(this,"Adicione arquivos, selecione a impressora e, se necessário, marque as linhas desejadas.","Impressão",MessageBoxButton.OK,MessageBoxImage.Information); return; }
        var copies = int.TryParse(CopiesBox.Text, out var parsedCopies) ? Math.Clamp(parsedCopies, 1, 99) : 1;
        var delayText = DelayBox.Text.Replace(',', '.');
        var delay = double.TryParse(delayText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDelay) ? Math.Clamp(parsedDelay, 0, 30) : 1.2;
        SaveSettings();
        _printCts = CancellationTokenSource.CreateLinkedTokenSource(_closeCts.Token);
        QueueProgress.Minimum = 0; QueueProgress.Maximum = Math.Max(1, targets.Count); QueueProgress.Value = 0;
        try
        {
            for (var index = 0; index < targets.Count; index++)
            {
                var row = targets[index];
                if (_printCts.IsCancellationRequested) break;
                row.Status = "Imprimindo…"; QueueGrid.Items.Refresh(); QueueStatusText.Text = $"Enviando {index + 1}/{targets.Count}: {row.Name}";
                try { await Task.Run(() => PrintFile(row.Path, PrinterBox.Text, copies), _printCts.Token); row.Status = "Enviado"; }
                catch (OperationCanceledException) { row.Status = "Pendente"; throw; }
                catch (Exception ex) { row.Status = "Erro — " + ex.Message; }
                QueueProgress.Value = index + 1; QueueGrid.Items.Refresh();
                if (index < targets.Count - 1) await Task.Delay(TimeSpan.FromSeconds(delay), _printCts.Token);
            }
        }
        catch (OperationCanceledException) { QueueStatusText.Text = "Impressão interrompida após o arquivo atual."; }
        finally { _printCts.Dispose(); _printCts = null; UpdateStatus(); }
    }
    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsFile)) return;
            var data = JsonSerializer.Deserialize<PrintSettings>(File.ReadAllText(_settingsFile));
            if (data is null) return;
            PrinterBox.Text = data.Printer; CopiesBox.Text = Math.Clamp(data.Copies, 1, 99).ToString(CultureInfo.InvariantCulture);
            DelayBox.Text = Math.Clamp(data.Delay, 0, 30).ToString("0.0", CultureInfo.GetCultureInfo("pt-BR")); OnlySelectedBox.IsChecked = data.OnlySelected;
        }
        catch { }
    }
    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
            var delayText = DelayBox.Text.Replace(',', '.');
            var delay = double.TryParse(delayText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 1.2;
            var data = new PrintSettings { Printer = PrinterBox.Text.Trim(), Copies = int.TryParse(CopiesBox.Text, out var copies) ? copies : 1, Delay = delay, OnlySelected = OnlySelectedBox.IsChecked == true };
            File.WriteAllText(_settingsFile, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
    private void UpdateStatus()
    {
        var pending = _items.Count(x => x.Status == "Pendente");
        var sent = _items.Count(x => x.Status == "Enviado");
        var errors = _items.Count(x => x.HasError);
        QueueStatusText.Text = $"{_items.Count} arquivo(s) • {pending} pendente(s) • {sent} enviado(s)" + (errors > 0 ? $" • {errors} erro(s)" : string.Empty);
        DropHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void PrintFile(string path,string printer,int copies){var ext=Path.GetExtension(path);var sumatra=FindSumatra();if(ext.Equals(".pdf",StringComparison.OrdinalIgnoreCase)&&sumatra is not null){var psi=new ProcessStartInfo(sumatra){UseShellExecute=false,CreateNoWindow=true};psi.ArgumentList.Add("-silent");psi.ArgumentList.Add("-print-to");psi.ArgumentList.Add(printer);if(copies>1){psi.ArgumentList.Add("-print-settings");psi.ArgumentList.Add($"{copies}x");}psi.ArgumentList.Add(path);using var p=Process.Start(psi)??throw new InvalidOperationException("Falha ao iniciar SumatraPDF.");p.WaitForExit(180000);if(p.ExitCode!=0)throw new InvalidOperationException("SumatraPDF retornou erro.");return;}for(int i=0;i<copies;i++){var result=ShellExecute(IntPtr.Zero,"printto",path,$"\"{printer}\"",null,0);if(result.ToInt64()<=32)throw new InvalidOperationException($"O Windows não encontrou aplicativo para imprimir ({result}).");Thread.Sleep(700);}}
    private static string? FindSumatra(){foreach(var p in new[]{@"C:\Program Files\SumatraPDF\SumatraPDF.exe",@"C:\Program Files (x86)\SumatraPDF\SumatraPDF.exe",Path.Combine(App.Paths.DataDirectory,"SumatraPDF.exe")})if(File.Exists(p))return p;return null;}
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr ShellExecute(IntPtr hwnd,string operation,string file,string? parameters,string? directory,int showCmd);
    private sealed class PrintSettings { public string Printer { get; set; } = string.Empty; public int Copies { get; set; } = 1; public double Delay { get; set; } = 1.2; public bool OnlySelected { get; set; } }
    public sealed class PrintRow{public PrintRow(string path){Path=path;}public int Order{get;set;}public string Path{get;}public string Name=>System.IO.Path.GetFileName(Path);public string Extension=>System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();public string SizeText{get{var n=new FileInfo(Path).Length;return n<1024?$"{n} B":n<1048576?$"{n/1024d:N1} KB":$"{n/1048576d:N1} MB";}}public string Status{get;set;}="Pendente";public bool HasError=>Status.StartsWith("Erro",StringComparison.OrdinalIgnoreCase);}
}
