using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace SIGFUR.Wpf.Views.Tools;

public partial class PrintQueueWindow : Window
{
    private readonly ObservableCollection<PrintRow> _items=[]; private readonly CancellationTokenSource _closeCts=new(); private CancellationTokenSource? _printCts;
    private readonly bool _forceSingleCopy;
    private readonly string _settingsFile = Path.Combine(App.Paths.DataDirectory, "fila_impressao_wpf.json");
    private static readonly HashSet<string> Supported=new(StringComparer.OrdinalIgnoreCase){".pdf",".doc",".docx",".docm",".rtf",".txt",".odt",".xls",".xlsx",".xlsm",".csv",".ods",".ppt",".pptx",".pptm",".odp",".jpg",".jpeg",".png",".bmp",".tif",".tiff"};
    private static readonly HashSet<string> LibreOfficeDocuments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".docm", ".rtf", ".txt", ".odt",
        ".xls", ".xlsx", ".xlsm", ".csv", ".ods",
        ".ppt", ".pptx", ".pptm", ".odp"
    };

    public PrintQueueWindow(IEnumerable<string>? initialPaths = null, int? initialCopies = null, bool forceSingleCopy = false)
    {
        InitializeComponent(); App.UiState.Attach(this); QueueGrid.ItemsSource = _items;
        _forceSingleCopy = forceSingleCopy;
        var startupPaths = (initialPaths ?? Array.Empty<string>()).Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
        LoadSettings();
        if (_forceSingleCopy)
        {
            CopiesBox.Text = "1";
            CopiesBox.IsEnabled = false;
            CopiesBox.ToolTip = "Este documento será enviado exatamente uma vez para evitar cópias duplicadas.";
        }
        else if (initialCopies.HasValue)
        {
            CopiesBox.Text = Math.Clamp(initialCopies.Value, 1, 99).ToString(CultureInfo.InvariantCulture);
        }
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
        var candidates = OnlySelectedBox.IsChecked == true ? QueueGrid.SelectedItems.Cast<PrintRow>().ToList() : _items.ToList();
        // Não reenviar automaticamente uma linha já concluída. Para uma
        // reimpressão intencional, o operador deve usar "Marcar pendente".
        var targets = candidates.Where(row => !row.Status.Equals("Enviado", StringComparison.OrdinalIgnoreCase)).ToList();
        if (targets.Count == 0)
        {
            var message = candidates.Count > 0
                ? "Os arquivos escolhidos já foram enviados. Para imprimir novamente, selecione as linhas e clique em “Marcar pendente”."
                : "Adicione arquivos ou selecione as linhas que deseja imprimir.";
            SigfurDialog.Show(this, message, "Impressão", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(PrinterBox.Text))
        {
            SigfurDialog.Show(this, "Selecione a impressora antes de iniciar.", "Impressão", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // Capture todos os valores da interface antes de entrar no Task.Run. Controles WPF
        // pertencem ao thread da janela e nao podem ser lidos pelo thread de impressao.
        var printer = PrinterBox.Text.Trim();
        var copies = _forceSingleCopy
            ? 1
            : int.TryParse(CopiesBox.Text, out var parsedCopies) ? Math.Clamp(parsedCopies, 1, 99) : 1;
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
                try { await Task.Run(() => PrintFile(row.Path, printer, copies), _printCts.Token); row.Status = "Enviado"; }
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

    private static void PrintFile(string path, string printer, int copies)
    {
        var extension = Path.GetExtension(path);
        var sumatra = FindSumatra();
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) && sumatra is not null)
        {
            var startInfo = new ProcessStartInfo(sumatra) { UseShellExecute = false, CreateNoWindow = true };
            startInfo.ArgumentList.Add("-silent");
            startInfo.ArgumentList.Add("-print-to");
            startInfo.ArgumentList.Add(printer);
            // Sempre informe a quantidade. Sem este argumento o leitor de PDF pode
            // reaproveitar a última quantidade escolhida pelo operador.
            startInfo.ArgumentList.Add("-print-settings");
            startInfo.ArgumentList.Add($"{copies}x");
            startInfo.ArgumentList.Add(path);
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Falha ao iniciar SumatraPDF.");
            if (!process.WaitForExit(180000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("A impressão demorou mais de 3 minutos para ser enviada.");
            }
            if (process.ExitCode != 0) throw new InvalidOperationException("SumatraPDF retornou erro.");
            return;
        }

        var libreOffice = LibreOfficeDocuments.Contains(extension) ? FindLibreOffice() : null;
        if (libreOffice is not null)
        {
            PrintWithIsolatedLibreOffice(libreOffice, path, printer, copies);
            return;
        }

        for (var index = 0; index < copies; index++)
        {
            var result = ShellExecute(IntPtr.Zero, "printto", path, $"\"{printer}\"", null, 0);
            if (result.ToInt64() <= 32)
                throw new InvalidOperationException($"O Windows não encontrou aplicativo para imprimir ({result}).");
            Thread.Sleep(700);
        }
    }

    private static void PrintWithIsolatedLibreOffice(string executable, string path, string printer, int copies)
    {
        for (var index = 0; index < copies; index++)
        {
            // O perfil comum do LibreOffice conserva opções da última caixa de
            // impressão (inclusive cópias). Um perfil temporário garante que cada
            // chamada da fila corresponda a um único trabalho com uma única cópia.
            var profileDirectory = Path.Combine(Path.GetTempPath(), $"sigfur_print_{Guid.NewGuid():N}");
            Directory.CreateDirectory(profileDirectory);
            try
            {
                var profileUri = new Uri(profileDirectory + Path.DirectorySeparatorChar).AbsoluteUri;
                var startInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                startInfo.ArgumentList.Add($"-env:UserInstallation={profileUri}");
                startInfo.ArgumentList.Add("--headless");
                startInfo.ArgumentList.Add("--nologo");
                startInfo.ArgumentList.Add("--nodefault");
                startInfo.ArgumentList.Add("--nofirststartwizard");
                startInfo.ArgumentList.Add("--nolockcheck");
                startInfo.ArgumentList.Add("--pt");
                startInfo.ArgumentList.Add(printer);
                startInfo.ArgumentList.Add(path);

                using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Falha ao iniciar o LibreOffice para impressão.");
                var errorTask = process.StandardError.ReadToEndAsync();
                _ = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(180000))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    throw new TimeoutException("O LibreOffice demorou mais de 3 minutos para enviar a impressão.");
                }
                var error = errorTask.GetAwaiter().GetResult().Trim();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("O LibreOffice não conseguiu imprimir." + (error.Length == 0 ? string.Empty : $" {error}"));
            }
            finally
            {
                try { Directory.Delete(profileDirectory, recursive: true); } catch { }
            }
        }
    }

    private static string? FindSumatra(){foreach(var p in new[]{@"C:\Program Files\SumatraPDF\SumatraPDF.exe",@"C:\Program Files (x86)\SumatraPDF\SumatraPDF.exe",Path.Combine(App.Paths.DataDirectory,"SumatraPDF.exe")})if(File.Exists(p))return p;return null;}
    private static string? FindLibreOffice()
    {
        foreach (var path in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreOffice", "program", "soffice.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LibreOffice", "program", "soffice.exe")
                 })
            if (File.Exists(path)) return path;
        return null;
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr ShellExecute(IntPtr hwnd,string operation,string file,string? parameters,string? directory,int showCmd);
    private sealed class PrintSettings { public string Printer { get; set; } = string.Empty; public int Copies { get; set; } = 1; public double Delay { get; set; } = 1.2; public bool OnlySelected { get; set; } }
    public sealed class PrintRow{public PrintRow(string path){Path=path;}public int Order{get;set;}public string Path{get;}public string Name=>System.IO.Path.GetFileName(Path);public string Extension=>System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();public string SizeText{get{var n=new FileInfo(Path).Length;return n<1024?$"{n} B":n<1048576?$"{n/1024d:N1} KB":$"{n/1048576d:N1} MB";}}public string Status{get;set;}="Pendente";public bool HasError=>Status.StartsWith("Erro",StringComparison.OrdinalIgnoreCase);}
}
