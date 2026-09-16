using Microsoft.Win32;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SIGFUR.Wpf.Views.Tools;

public partial class PdfToolsWindow : Window
{
    private string _lastResult = string.Empty;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif", ".webp" };

    public PdfToolsWindow()
    {
        InitializeComponent();
        App.UiState.Attach(this);
        ConvertOutputBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Loaded += (_, _) => RefreshResources();
    }

    private static string? Find(params string[] paths)
    {
        var candidates = new List<string>();
        foreach (var path in paths.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            candidates.Add(Environment.ExpandEnvironmentVariables(path));
        }

        var targetNames = paths.Select(Path.GetFileName).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        AddCommonCandidates(candidates, targetNames);

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var clean = CleanPath(candidate);
                if (File.Exists(clean)) return clean;
            }
            catch { }
        }

        foreach (var name in targetNames)
        {
            var onPath = FindOnPath(name);
            if (onPath is not null) return onPath;

            var found = FindInCommonFolders(name!);
            if (found is not null) return found;
        }

        return null;
    }

    private static void AddCommonCandidates(List<string> candidates, IReadOnlyCollection<string> targetNames)
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var baseDir = AppContext.BaseDirectory;

        void Add(string path) { if (!string.IsNullOrWhiteSpace(path)) candidates.Add(path); }

        bool Wants(string name) => targetNames.Contains(name, StringComparer.OrdinalIgnoreCase);

        foreach (var root in new[] { baseDir, Path.Combine(baseDir, "tools"), Path.Combine(baseDir, "Tools") })
        {
            if (Wants("qpdf.exe")) { Add(Path.Combine(root, "qpdf.exe")); Add(Path.Combine(root, "qpdf", "bin", "qpdf.exe")); }
            if (Wants("gswin64c.exe")) { Add(Path.Combine(root, "gswin64c.exe")); Add(Path.Combine(root, "ghostscript", "bin", "gswin64c.exe")); }
            if (Wants("gswin32c.exe")) Add(Path.Combine(root, "gswin32c.exe"));
            if (Wants("pdftk.exe")) Add(Path.Combine(root, "pdftk.exe"));
            if (Wants("soffice.exe")) Add(Path.Combine(root, "soffice.exe"));
            if (Wants("python.exe")) Add(Path.Combine(root, ".venv", "Scripts", "python.exe"));
        }

        foreach (var root in new[] { pf, pf86, Path.Combine(local, "Programs") })
        {
            if (Wants("soffice.exe"))
            {
                Add(Path.Combine(root, "LibreOffice", "program", "soffice.exe"));
                Add(Path.Combine(root, "OpenOffice 4", "program", "soffice.exe"));
            }
            if (Wants("qpdf.exe"))
            {
                Add(Path.Combine(root, "qpdf", "bin", "qpdf.exe"));
                Add(Path.Combine(root, "qpdf", "qpdf.exe"));
            }
            if (Wants("pdftk.exe")) Add(Path.Combine(root, "PDFtk Server", "bin", "pdftk.exe"));
        }

        foreach (var name in targetNames)
        {
            Add(Path.Combine(programData, "chocolatey", "bin", name));
            Add(Path.Combine(user, "scoop", "shims", name));
        }
    }

    private static string? FindOnPath(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            try
            {
                var cleanDir = CleanPath(dir);
                if (string.IsNullOrWhiteSpace(cleanDir)) continue;
                var path = Path.Combine(cleanDir, name);
                if (File.Exists(path)) return path;
            }
            catch { }
        }
        return null;
    }

    private static string? FindInCommonFolders(string fileName)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "apps")
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var root in roots)
        {
            var found = FindFileBreadthFirst(root, fileName, maxDepth: 4);
            if (found is not null) return found;
        }

        return null;
    }

    private static string? FindFileBreadthFirst(string root, string fileName, int maxDepth)
    {
        try
        {
            var queue = new Queue<(string Directory, int Depth)>();
            queue.Enqueue((root, 0));
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var direct = Path.Combine(current.Directory, fileName);
                if (File.Exists(direct)) return direct;
                if (current.Depth >= maxDepth) continue;

                IEnumerable<string> children;
                try { children = Directory.EnumerateDirectories(current.Directory); }
                catch { continue; }

                foreach (var child in children)
                {
                    var folder = Path.GetFileName(child);
                    if (folder.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)) continue;
                    queue.Enqueue((child, current.Depth + 1));
                }
            }
        }
        catch { }

        return null;
    }

    private string? Soffice => Find(@"%ProgramFiles%\LibreOffice\program\soffice.exe", @"%ProgramFiles(x86)%\LibreOffice\program\soffice.exe", @"%LOCALAPPDATA%\Programs\LibreOffice\program\soffice.exe", "soffice.exe");
    private string? Ghostscript => Find(@"%ProgramFiles%\gs\gs10.06.0\bin\gswin64c.exe", @"%ProgramFiles%\gs\gs10.05.1\bin\gswin64c.exe", @"%ProgramFiles%\gs\gs10.04.0\bin\gswin64c.exe", @"%ProgramFiles%\gs\gs10.03.1\bin\gswin64c.exe", "gswin64c.exe", "gswin32c.exe");
    private string? Qpdf => Find(@"%ProgramFiles%\qpdf\bin\qpdf.exe", @"%ProgramFiles%\qpdf\qpdf.exe", @"%LOCALAPPDATA%\Programs\qpdf\bin\qpdf.exe", "qpdf.exe");
    private string? Pdftk => Find(@"%ProgramFiles%\PDFtk Server\bin\pdftk.exe", @"%ProgramFiles(x86)%\PDFtk Server\bin\pdftk.exe", "pdftk.exe");
    private string? Python => Find(Path.Combine(AppContext.BaseDirectory, ".venv", "Scripts", "python.exe"), Path.Combine(Directory.GetCurrentDirectory(), ".venv", "Scripts", "python.exe"), @"%LOCALAPPDATA%\Programs\Python\Python314\python.exe", @"%LOCALAPPDATA%\Programs\Python\Python313\python.exe", @"%LOCALAPPDATA%\Programs\Python\Python312\python.exe", "python.exe");

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshResources();
    private void RefreshResources()
    {
        ResourceGrid.ItemsSource = new[]
        {
            new ResourceRow("Conversão de imagens", "Disponível", "Mecanismo nativo do SIGFUR"),
            new ResourceRow("LibreOffice", Soffice is null ? "Não encontrado" : "Disponível", Soffice ?? "Word, Excel, PowerPoint, ODT e outros documentos"),
            new ResourceRow("Ghostscript", Ghostscript is null ? "Não encontrado" : "Disponível", Ghostscript ?? "Compactação de PDF"),
            new ResourceRow("qpdf", Qpdf is null ? "Não encontrado" : "Disponível", Qpdf ?? "Juntar, extrair, girar e proteger PDFs"),
            new ResourceRow("PDFtk", Pdftk is null ? "Não encontrado" : "Disponível", Pdftk ?? "Alternativa para juntar e extrair PDFs"),
            new ResourceRow("Python reserva", Python is null ? "Não encontrado" : "Disponível", Python ?? "Reserva com pypdf para juntar, extrair, girar e proteger quando qpdf não estiver disponível")
        };
    }

    private void AddConvert_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Documentos suportados|*.doc;*.docx;*.odt;*.rtf;*.xls;*.xlsx;*.ods;*.ppt;*.pptx;*.odp;*.txt;*.csv;*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif;*.webp|Todos os arquivos|*.*"
        };
        if (dialog.ShowDialog(this) == true)
            foreach (var file in dialog.FileNames) if (!ConvertList.Items.Contains(file)) ConvertList.Items.Add(file);
    }

    private void RemoveConvert_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in ConvertList.SelectedItems.Cast<object>().ToList()) ConvertList.Items.Remove(item);
    }
    private void ClearConvert_Click(object sender, RoutedEventArgs e) => ConvertList.Items.Clear();
    private void ChooseConvertOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Pasta de saída" };
        if (dialog.ShowDialog(this) == true) ConvertOutputBox.Text = dialog.FolderName;
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        var files = ConvertList.Items.Cast<string>().ToList();
        if (files.Count == 0) { Warn("Adicione pelo menos um arquivo."); return; }
        var outputFolder = CleanPath(ConvertOutputBox.Text);
        if (string.IsNullOrWhiteSpace(outputFolder)) { Warn("Escolha a pasta de saída."); return; }
        var office = files.Where(x => !Path.GetExtension(x).Equals(".pdf", StringComparison.OrdinalIgnoreCase) && !ImageExtensions.Contains(Path.GetExtension(x))).ToList();
        if (office.Count > 0 && Soffice is null) { Warn("LibreOffice não encontrado. Ele é necessário para converter documentos do Office, ODT, TXT e CSV."); return; }
        Directory.CreateDirectory(outputFolder);
        ConvertOutputBox.Text = outputFolder;
        SetBusy(true, "Convertendo arquivos para PDF…");
        var errors = new List<string>();
        var outputs = new List<string>();
        await Task.Run(() =>
        {
            foreach (var file in files)
            {
                try
                {
                    var ext = Path.GetExtension(file);
                    if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        var output = Unique(Path.Combine(outputFolder, Path.GetFileName(file)));
                        File.Copy(file, output, false); outputs.Add(output); continue;
                    }
                    if (ImageExtensions.Contains(ext))
                    {
                        var output = Unique(Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(file) + ".pdf"));
                        ConvertImageToPdf(file, output); outputs.Add(output); continue;
                    }
                    var before = Directory.EnumerateFiles(outputFolder, "*.pdf").ToHashSet(StringComparer.OrdinalIgnoreCase);
                    Run(Soffice!, ["--headless", "--nologo", "--nofirststartwizard", "--convert-to", "pdf", "--outdir", outputFolder, file], 180);
                    var expected = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(file) + ".pdf");
                    var generated = File.Exists(expected) ? expected : Directory.EnumerateFiles(outputFolder, "*.pdf").Except(before, StringComparer.OrdinalIgnoreCase).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                    if (generated is null) throw new FileNotFoundException("O conversor terminou, mas o PDF não foi localizado.");
                    outputs.Add(generated);
                }
                catch (Exception ex) { errors.Add(Path.GetFileName(file) + ": " + ex.Message); }
            }
        });
        if (outputs.Count > 0) SetLastResult(outputs[^1]);
        SetBusy(false, errors.Count == 0 ? $"Conversão concluída: {outputs.Count} arquivo(s)." : $"Conversão parcial: {outputs.Count} concluído(s) e {errors.Count} falha(s).");
        if (errors.Count > 0) SigfurDialog.Show(this, string.Join(Environment.NewLine, errors.Take(12)), "Conversão", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ChooseCompressInput_Click(object sender, RoutedEventArgs e)
    {
        var path = ChooseOpenPdf(); if (path is null) return;
        CompressInputBox.Text = path;
        CompressOutputBox.Text = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_compactado.pdf");
    }
    private void ChooseCompressOutput_Click(object sender, RoutedEventArgs e) { var path = ChooseSavePdf("PDF compactado"); if (path is not null) CompressOutputBox.Text = path; }
    private async void Compress_Click(object sender, RoutedEventArgs e)
    {
        if (Ghostscript is null) { Warn("Ghostscript não encontrado. Instale-o para compactar PDFs. O SIGFUR agora procura também em Program Files, Chocolatey e Scoop; se acabou de instalar, clique em Revalidar recursos."); return; }
        var input = CleanPath(CompressInputBox.Text);
        var output = CleanPath(CompressOutputBox.Text);
        if (!ValidateInputOutput(input, output)) return;
        CompressInputBox.Text = input;
        CompressOutputBox.Text = output;
        var setting = (QualityBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "/ebook";
        await RunJobAsync("Compactando PDF…", output, () => Run(Ghostscript!, ["-sDEVICE=pdfwrite", "-dCompatibilityLevel=1.4", $"-dPDFSETTINGS={setting}", "-dDetectDuplicateImages=true", "-dCompressFonts=true", "-dSubsetFonts=true", "-dNOPAUSE", "-dQUIET", "-dBATCH", $"-sOutputFile={output}", input], 240));
    }

    private void AddMerge_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Filter = "PDF|*.pdf" };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames)
        {
            if (!MergeList.Items.Contains(file)) MergeList.Items.Add(file);
        }
        if (string.IsNullOrWhiteSpace(MergeOutputBox.Text) && dialog.FileNames.Length > 0)
        {
            MergeOutputBox.Text = Unique(Path.Combine(Path.GetDirectoryName(dialog.FileNames[0])!, "PDF_unificado.pdf"));
        }
    }
    private void RemoveMerge_Click(object sender, RoutedEventArgs e) { foreach (var item in MergeList.SelectedItems.Cast<object>().ToList()) MergeList.Items.Remove(item); }
    private void ClearMerge_Click(object sender, RoutedEventArgs e) => MergeList.Items.Clear();
    private void MergeUp_Click(object sender, RoutedEventArgs e) => MoveMerge(-1);
    private void MergeDown_Click(object sender, RoutedEventArgs e) => MoveMerge(1);
    private void MoveMerge(int delta)
    {
        if (MergeList.SelectedItem is not string selected) return;
        var index = MergeList.Items.IndexOf(selected); var target = index + delta;
        if (target < 0 || target >= MergeList.Items.Count) return;
        MergeList.Items.RemoveAt(index); MergeList.Items.Insert(target, selected); MergeList.SelectedItem = selected; MergeList.ScrollIntoView(selected);
    }
    private void ChooseMergeOutput_Click(object sender, RoutedEventArgs e) { var path = ChooseSavePdf("PDF unificado"); if (path is not null) MergeOutputBox.Text = path; }
    private async void Merge_Click(object sender, RoutedEventArgs e)
    {
        var files = MergeList.Items.Cast<string>().Select(CleanPath).ToList();
        if (files.Count < 2) { Warn("Adicione pelo menos dois PDFs."); return; }
        var missing = files.FirstOrDefault(x => !File.Exists(x));
        if (missing is not null) { Warn($"Este PDF da lista não foi encontrado:\n{missing}"); return; }
        var output = CleanPath(MergeOutputBox.Text);
        if (string.IsNullOrWhiteSpace(output)) { Warn("Escolha o PDF de saída."); return; }
        if (files.Any(x => Path.GetFullPath(x).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))) { Warn("O PDF de saída deve ser diferente dos PDFs de entrada."); return; }
        EnsureOutputDirectory(output);
        MergeOutputBox.Text = output;
        if (Qpdf is not null)
        {
            var args = new List<string> { "--empty", "--pages" };
            foreach (var file in files) { args.Add(file); args.Add("1-z"); }
            args.Add("--"); args.Add(output);
            await RunJobAsync("Juntando PDFs…", output, () => Run(Qpdf, args, 240));
            return;
        }
        if (Pdftk is not null)
        {
            var args = files.Concat(new[] { "cat", "output", output }).ToArray();
            await RunJobAsync("Juntando PDFs…", output, () => Run(Pdftk, args, 240));
            return;
        }
        if (Python is not null)
        {
            await RunJobAsync("Juntando PDFs…", output, () => RunPythonPdf("merge", new[] { output }.Concat(files), 240));
            return;
        }
        Warn("qpdf, PDFtk ou Python com pypdf não foram encontrados. O SIGFUR agora tenta localizar os programas em Program Files, Chocolatey, Scoop e no .venv; se instalou agora, clique em Revalidar recursos.");
    }

    private void ChooseExtractInput_Click(object sender, RoutedEventArgs e)
    {
        var path = ChooseOpenPdf(); if (path is null) return;
        ExtractInputBox.Text = path; ExtractOutputBox.Text = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_paginas.pdf");
    }
    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        var input = CleanPath(ExtractInputBox.Text);
        var output = CleanPath(ExtractOutputBox.Text);
        if (!ValidateInputOutput(input, output)) return;
        ExtractInputBox.Text = input;
        ExtractOutputBox.Text = output;
        string pages;
        try { pages = NormalizePageSpec(ExtractPagesBox.Text); }
        catch (Exception ex) { Warn(ex.Message); return; }
        if (string.IsNullOrWhiteSpace(pages)) { Warn("Informe as páginas, por exemplo: 1,3-5,8."); return; }
        if (Qpdf is not null)
        {
            await RunJobAsync("Extraindo páginas…", output, () => Run(Qpdf, ["--empty", "--pages", input, pages, "--", output], 180));
            return;
        }
        if (Pdftk is not null)
        {
            var args = new List<string> { input, "cat" };
            args.AddRange(pages.Split(',', StringSplitOptions.RemoveEmptyEntries));
            args.Add("output"); args.Add(output);
            await RunJobAsync("Extraindo páginas…", output, () => Run(Pdftk, args, 180));
            return;
        }
        if (Python is not null)
        {
            await RunJobAsync("Extraindo páginas…", output, () => RunPythonPdf("extract", [output, pages, input], 180));
            return;
        }
        Warn("qpdf, PDFtk ou Python com pypdf não foram encontrados. O SIGFUR agora procura em mais locais; se instalou agora, clique em Revalidar recursos.");
    }

    private void ChooseRotateInput_Click(object sender, RoutedEventArgs e)
    {
        var path = ChooseOpenPdf(); if (path is null) return;
        RotateInputBox.Text = path; RotateOutputBox.Text = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_girado.pdf");
    }
    private async void Rotate_Click(object sender, RoutedEventArgs e)
    {
        var input = CleanPath(RotateInputBox.Text);
        var output = CleanPath(RotateOutputBox.Text);
        if (!ValidateInputOutput(input, output)) return;
        RotateInputBox.Text = input;
        RotateOutputBox.Text = output;
        var angle = (RotateAngleBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "90";
        string pages;
        try { pages = NormalizePageSpec(RotatePagesBox.Text); }
        catch (Exception ex) { Warn(ex.Message); return; }
        if (string.IsNullOrWhiteSpace(pages)) pages = "1-z";
        if (Qpdf is not null)
        {
            await RunJobAsync("Girando páginas…", output, () => Run(Qpdf, [$"--rotate=+{angle}:{pages}", input, output], 180));
            return;
        }
        if (Python is not null)
        {
            await RunJobAsync("Girando páginas…", output, () => RunPythonPdf("rotate", [output, pages, angle, input], 180));
            return;
        }
        Warn("qpdf ou Python com pypdf não foram encontrados. Se já instalou, clique em Revalidar recursos.");
    }

    private void ChooseEncryptInput_Click(object sender, RoutedEventArgs e)
    {
        var path = ChooseOpenPdf(); if (path is null) return;
        EncryptInputBox.Text = path; EncryptOutputBox.Text = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_protegido.pdf");
    }
    private async void Encrypt_Click(object sender, RoutedEventArgs e)
    {
        var input = CleanPath(EncryptInputBox.Text);
        var output = CleanPath(EncryptOutputBox.Text);
        if (!ValidateInputOutput(input, output)) return;
        EncryptInputBox.Text = input;
        EncryptOutputBox.Text = output;
        if (string.IsNullOrEmpty(EncryptPasswordBox.Password)) { Warn("Informe uma senha."); return; }
        if (!EncryptPasswordBox.Password.Equals(EncryptConfirmBox.Password, StringComparison.Ordinal)) { Warn("As senhas não conferem."); return; }
        var password = EncryptPasswordBox.Password;
        if (Qpdf is not null)
        {
            await RunJobAsync("Protegendo PDF…", output, () => Run(Qpdf, ["--encrypt", password, password, "256", "--", input, output], 180));
            return;
        }
        if (Python is not null)
        {
            await RunJobAsync("Protegendo PDF…", output, () => RunPythonPdf("encrypt", [output, password, input], 180));
            return;
        }
        Warn("qpdf ou Python com pypdf não foram encontrados. Se já instalou, clique em Revalidar recursos.");
    }

    private async Task RunJobAsync(string status, string output, Action action)
    {
        SetBusy(true, status);
        try
        {
            output = CleanPath(output);
            await Task.Run(action);
            if (!File.Exists(output)) throw new FileNotFoundException("O processo terminou, mas o arquivo de saída não foi criado.", output);
            SetLastResult(output); SetBusy(false, $"Concluído: {Path.GetFileName(output)}");
        }
        catch (Exception ex)
        {
            SetBusy(false, "Falha na operação.");
            SigfurDialog.Show(this, ex.Message, "Ferramentas PDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool ValidateInputOutput(string input, string output)
    {
        input = CleanPath(input);
        output = CleanPath(output);
        if (string.IsNullOrWhiteSpace(input) || !File.Exists(input) || string.IsNullOrWhiteSpace(output)) { Warn("Escolha um arquivo de entrada existente e o caminho de saída."); return false; }
        if (Path.GetFullPath(input).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase)) { Warn("O arquivo de saída deve ser diferente do original."); return false; }
        EnsureOutputDirectory(output);
        return true;
    }

    private static string CleanPath(string? value)
        => (value ?? string.Empty).Trim().Trim('"');

    private static void EnsureOutputDirectory(string output)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
    }

    private static string NormalizePageSpec(string? value)
    {
        value = Regex.Replace(value ?? string.Empty, @"\s+", string.Empty);
        if (value.Length == 0) return string.Empty;
        if (!Regex.IsMatch(value, @"^\d+(?:-\d+)?(?:,\d+(?:-\d+)?)*$")) throw new InvalidOperationException("Formato de páginas inválido. Use 1,3-5,8.");
        return value;
    }

    private static string? ChooseOpenPdf()
    {
        var dialog = new OpenFileDialog { Filter = "PDF|*.pdf" };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    private string? ChooseSavePdf(string title)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = "PDF|*.pdf", DefaultExt = ".pdf", AddExtension = true };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void SetLastResult(string path)
    {
        _lastResult = path; OpenResultButton.IsEnabled = File.Exists(path);
    }
    private void OpenResult_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_lastResult)) return;
        Process.Start(new ProcessStartInfo(_lastResult) { UseShellExecute = true });
    }

    private static void Run(string exe, IEnumerable<string> args, int seconds)
    {
        exe = CleanPath(exe);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        var argList = args.Select(CleanPath).ToList();
        foreach (var arg in argList) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o recurso externo.");
        if (!process.WaitForExit(seconds * 1000)) { process.Kill(true); throw new TimeoutException($"Tempo limite excedido ao executar {Path.GetFileName(exe)}."); }

        var stderr = process.StandardError.ReadToEnd();
        var stdout = process.StandardOutput.ReadToEnd();
        if (process.ExitCode == 0) return;

        var detail = (stderr + Environment.NewLine + stdout).Trim();
        if (string.IsNullOrWhiteSpace(detail)) detail = $"O recurso terminou com código {process.ExitCode}.";
        var command = Path.GetFileName(exe) + " " + string.Join(" ", argList.Select(QuoteArg));
        throw new InvalidOperationException($"Falha ao executar {Path.GetFileName(exe)}.{Environment.NewLine}{Environment.NewLine}Comando:{Environment.NewLine}{command}{Environment.NewLine}{Environment.NewLine}Detalhe:{Environment.NewLine}{detail}");
    }

    private static string QuoteArg(string arg)
        => arg.Contains(' ') ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg;


    private void RunPythonPdf(string operation, IEnumerable<string> args, int seconds)
    {
        if (Python is null) throw new InvalidOperationException("Python não encontrado para operar PDFs.");
        var scriptPath = Path.Combine(Path.GetTempPath(), "sigfur_pdf_tools.py");
        File.WriteAllText(scriptPath, PythonPdfScript, new UTF8Encoding(false));
        Run(Python, new[] { scriptPath, operation }.Concat(args), seconds);
    }

    private const string PythonPdfScript = """
import os
import sys

try:
    from pypdf import PdfReader, PdfWriter
except Exception as exc:
    print("pypdf não está instalado neste Python. Execute PREPARAR_AUTOMACAO.bat ou instale com: python -m pip install pypdf", file=sys.stderr)
    raise

def ensure_parent(path):
    parent = os.path.dirname(os.path.abspath(path))
    if parent:
        os.makedirs(parent, exist_ok=True)

def parse_pages(spec, total):
    spec = (spec or "").strip().replace(" ", "")
    if not spec or spec.lower() in ("all", "1-z"):
        return list(range(total))
    result = []
    for part in spec.split(','):
        if not part:
            continue
        if '-' in part:
            left, right = part.split('-', 1)
            start = int(left)
            end = total if right.lower() == 'z' else int(right)
            if start > end:
                start, end = end, start
            result.extend(range(start - 1, end))
        else:
            result.append(int(part) - 1)
    for index in result:
        if index < 0 or index >= total:
            raise ValueError(f"Página fora do intervalo: {index + 1}. O PDF tem {total} página(s).")
    return result

op = sys.argv[1]

if op == "merge":
    output = sys.argv[2]
    inputs = sys.argv[3:]
    writer = PdfWriter()
    for item in inputs:
        reader = PdfReader(item)
        for page in reader.pages:
            writer.add_page(page)
    ensure_parent(output)
    with open(output, "wb") as f:
        writer.write(f)

elif op == "extract":
    output, spec, input_pdf = sys.argv[2], sys.argv[3], sys.argv[4]
    reader = PdfReader(input_pdf)
    writer = PdfWriter()
    for index in parse_pages(spec, len(reader.pages)):
        writer.add_page(reader.pages[index])
    ensure_parent(output)
    with open(output, "wb") as f:
        writer.write(f)

elif op == "rotate":
    output, spec, angle, input_pdf = sys.argv[2], sys.argv[3], int(sys.argv[4]), sys.argv[5]
    reader = PdfReader(input_pdf)
    writer = PdfWriter()
    rotate_indexes = set(parse_pages(spec, len(reader.pages)))
    for i, page in enumerate(reader.pages):
        if i in rotate_indexes:
            page.rotate(angle)
        writer.add_page(page)
    ensure_parent(output)
    with open(output, "wb") as f:
        writer.write(f)

elif op == "encrypt":
    output, password, input_pdf = sys.argv[2], sys.argv[3], sys.argv[4]
    reader = PdfReader(input_pdf)
    writer = PdfWriter()
    for page in reader.pages:
        writer.add_page(page)
    writer.encrypt(password)
    ensure_parent(output)
    with open(output, "wb") as f:
        writer.write(f)

else:
    raise ValueError("Operação desconhecida: " + op)
""";

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path)!; var stem = Path.GetFileNameWithoutExtension(path); var extension = Path.GetExtension(path);
        for (var index = 2; index < 999; index++) { var candidate = Path.Combine(directory, $"{stem}_{index}{extension}"); if (!File.Exists(candidate)) return candidate; }
        return Path.Combine(directory, $"{stem}_{DateTime.Now:HHmmss}{extension}");
    }

    private static void ConvertImageToPdf(string input, string output)
    {
        BitmapFrame frame;
        using (var stream = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
        }
        var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Rgb24, null, 0);
        var width = bitmap.PixelWidth; var height = bitmap.PixelHeight; var stride = width * 3;
        var pixels = new byte[stride * height]; bitmap.CopyPixels(pixels, stride, 0);
        byte[] compressed;
        using (var buffer = new MemoryStream())
        {
            using (var z = new ZLibStream(buffer, CompressionLevel.Optimal, true)) z.Write(pixels, 0, pixels.Length);
            compressed = buffer.ToArray();
        }
        var landscape = width > height;
        var pageWidth = landscape ? 842d : 595d; var pageHeight = landscape ? 595d : 842d; const double margin = 24d;
        var scale = Math.Min((pageWidth - margin * 2) / width, (pageHeight - margin * 2) / height);
        var drawWidth = width * scale; var drawHeight = height * scale; var x = (pageWidth - drawWidth) / 2; var y = (pageHeight - drawHeight) / 2;
        var content = $"q {drawWidth.ToString("0.###", CultureInfo.InvariantCulture)} 0 0 {drawHeight.ToString("0.###", CultureInfo.InvariantCulture)} {x.ToString("0.###", CultureInfo.InvariantCulture)} {y.ToString("0.###", CultureInfo.InvariantCulture)} cm /Im0 Do Q\n";
        var contentBytes = Encoding.ASCII.GetBytes(content);
        using var pdf = new MemoryStream(); var offsets = new List<long> { 0 };
        void WriteAscii(string text) { var bytes = Encoding.ASCII.GetBytes(text); pdf.Write(bytes, 0, bytes.Length); }
        WriteAscii("%PDF-1.5\n%\xE2\xE3\xCF\xD3\n");
        void Start(int id) { offsets.Add(pdf.Position); WriteAscii($"{id} 0 obj\n"); }
        Start(1); WriteAscii("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Start(2); WriteAscii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Start(3); WriteAscii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageWidth:0} {pageHeight:0}] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");
        Start(4); WriteAscii($"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {compressed.Length} >>\nstream\n"); pdf.Write(compressed, 0, compressed.Length); WriteAscii("\nendstream\nendobj\n");
        Start(5); WriteAscii($"<< /Length {contentBytes.Length} >>\nstream\n"); pdf.Write(contentBytes, 0, contentBytes.Length); WriteAscii("endstream\nendobj\n");
        var xref = pdf.Position; WriteAscii($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) WriteAscii($"{offset:0000000000} 00000 n \n");
        WriteAscii($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        EnsureOutputDirectory(output); File.WriteAllBytes(output, pdf.ToArray());
    }

    private void SetBusy(bool busy, string text) { BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; StatusText.Text = text; }
    private void Warn(string text) => SigfurDialog.Show(this, text, "Ferramentas PDF", MessageBoxButton.OK, MessageBoxImage.Information);
    private sealed record ResourceRow(string Name, string Status, string Detail);
}
