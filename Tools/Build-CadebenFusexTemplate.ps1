param(
    [Parameter(Mandatory = $true)][string]$ReferencePath,
    [Parameter(Mandatory = $true)][string]$DestinationPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Set-OdtText {
    param([System.Xml.XmlElement]$Element, [string]$Text)
    while ($Element.HasChildNodes) { [void]$Element.RemoveChild($Element.FirstChild) }
    [void]$Element.AppendChild($Element.OwnerDocument.CreateTextNode($Text))
}

$source = [System.IO.Compression.ZipFile]::OpenRead($ReferencePath)
try {
    $contentEntry = $source.GetEntry('content.xml')
    if ($null -eq $contentEntry) { throw 'O ODT de referência não possui content.xml.' }
    $reader = [System.IO.StreamReader]::new($contentEntry.Open(), [System.Text.Encoding]::UTF8, $true)
    try { $contentXml = $reader.ReadToEnd() } finally { $reader.Dispose() }

    $document = [System.Xml.XmlDocument]::new()
    $document.PreserveWhitespace = $true
    $document.LoadXml($contentXml)
    $namespaces = [System.Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespaces.AddNamespace('text', 'urn:oasis:names:tc:opendocument:xmlns:text:1.0')
    $namespaces.AddNamespace('table', 'urn:oasis:names:tc:opendocument:xmlns:table:1.0')
    $namespaces.AddNamespace('office', 'urn:oasis:names:tc:opendocument:xmlns:office:1.0')

    $intro = $document.SelectNodes('//office:body//text:p', $namespaces) |
        Where-Object { $_.InnerText.Trim().StartsWith('Eu, LUCAS PHELIPE', [System.StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1
    if ($null -eq $intro) { throw 'Parágrafo de identificação não encontrado no documento de referência.' }
    $introText = '"Eu, {{MILITAR_NOME}}, {{MILITAR_PG_ABREV}}, benefici\u00e1rio(a) titular do Fundo de Sa\u00fade do Ex\u00e9rcito, Idt: {{MILITAR_IDT}}, CPF: {{MILITAR_CPF}}, e Prec-CP {{MILITAR_PREC_CP}}, declaro expressamente, sob as penas da lei, que s\u00e3o meus benefici\u00e1rios dependentes para fim de assist\u00eancia m\u00e9dico-hospitalar pelo Fundo de Sa\u00fade do Ex\u00e9rcito (FUSEx), com amparo no que est\u00e1 disposto nos art. 5\u00ba, 6\u00ba e 7\u00ba das IG 30-32:"' | ConvertFrom-Json
    Set-OdtText $intro $introText

    $signature = $document.SelectNodes('//office:body//text:p', $namespaces) |
        Where-Object { $_.InnerText -match '^\s*LUCAS PHELIPE.*Sd EP\s*$' } |
        Select-Object -First 1
    if ($null -eq $signature) { throw 'Assinatura do militar não encontrada no documento de referência.' }
    $signatureText = '"{{MILITAR_NOME}} \u2013 {{MILITAR_PG_ABREV}}"' | ConvertFrom-Json
    Set-OdtText $signature $signatureText

    $tables = $document.SelectNodes('//table:table', $namespaces)
    if ($tables.Count -lt 2) { throw 'As tabelas esperadas não foram encontradas.' }

    $beneficiaryRows = $tables[0].SelectNodes('./table:table-row', $namespaces)
    if ($beneficiaryRows.Count -lt 2) { throw 'A linha de beneficiário não foi encontrada.' }
    $prototype = $beneficiaryRows[1]
    for ($number = 1; $number -le 10; $number++) {
        $row = if ($number -eq 1) { $prototype } else { $prototype.CloneNode($true) }
        $cells = $row.SelectNodes('./table:table-cell', $namespaces)
        if ($cells.Count -lt 4) { throw 'A tabela de beneficiários precisa ter quatro colunas.' }
        $tokens = @("{{DEP${number}_NOME}}", "{{DEP${number}_PARENTESCO}}", "{{DEP${number}_DATA_NASC}}", "{{DEP${number}_OBS}}")
        for ($column = 0; $column -lt 4; $column++) {
            $paragraph = $cells[$column].SelectSingleNode('.//text:p', $namespaces)
            if ($null -eq $paragraph) { throw "Célula $column da linha de beneficiário sem parágrafo." }
            Set-OdtText $paragraph $tokens[$column]
        }
        if ($number -gt 1) { [void]$tables[0].AppendChild($row) }
    }

    $dateParagraph = $tables[1].SelectSingleNode('.//text:p', $namespaces)
    if ($null -eq $dateParagraph) { throw 'Campo de local e data não encontrado.' }
    Set-OdtText $dateParagraph '{{LOCAL_DATA}}'

    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $settings.Indent = $false
    $builder = [System.Text.StringBuilder]::new()
    $xmlWriter = [System.Xml.XmlWriter]::Create($builder, $settings)
    try { $document.Save($xmlWriter) } finally { $xmlWriter.Dispose() }
    $updatedContent = $builder.ToString().Replace('encoding="utf-16"', 'encoding="UTF-8"')

    $destinationDirectory = Split-Path -Parent $DestinationPath
    if ($destinationDirectory) { [System.IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null }
    $tempPath = $DestinationPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $outputStream = [System.IO.File]::Create($tempPath)
    $output = [System.IO.Compression.ZipArchive]::new($outputStream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        $mime = $source.GetEntry('mimetype')
        if ($null -ne $mime) {
            $targetMime = $output.CreateEntry('mimetype', [System.IO.Compression.CompressionLevel]::NoCompression)
            $inputStream = $mime.Open(); $targetStream = $targetMime.Open()
            try { $inputStream.CopyTo($targetStream) } finally { $inputStream.Dispose(); $targetStream.Dispose() }
        }
        foreach ($entry in $source.Entries) {
            if ($entry.FullName -eq 'mimetype') { continue }
            $target = $output.CreateEntry($entry.FullName, [System.IO.Compression.CompressionLevel]::Optimal)
            $targetStream = $target.Open()
            try {
                if ($entry.FullName -eq 'content.xml') {
                    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($updatedContent)
                    $targetStream.Write($bytes, 0, $bytes.Length)
                } else {
                    $inputStream = $entry.Open()
                    try { $inputStream.CopyTo($targetStream) } finally { $inputStream.Dispose() }
                }
            } finally { $targetStream.Dispose() }
        }
    } finally {
        $output.Dispose()
        $outputStream.Dispose()
    }
    [System.IO.File]::Copy($tempPath, $DestinationPath, $true)
    [System.IO.File]::Delete($tempPath)
} finally {
    $source.Dispose()
}

Write-Output "Template Cadeben FuSEx criado em: $DestinationPath"
