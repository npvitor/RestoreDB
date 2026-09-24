# Gera docs/MANUAL.pdf a partir de docs/MANUAL.md, sem dependências externas:
# Markdown -> HTML com o ConvertFrom-Markdown do PowerShell 7 (Markdig) e HTML -> PDF com o
# Chrome/Edge em modo headless. Uso (na raiz do repositório):  pwsh docs/build-manual-pdf.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$mdPath = Join-Path $root 'docs/MANUAL.md'
$pdfPath = Join-Path $root 'docs/MANUAL.pdf'

$html = (ConvertFrom-Markdown -Path $mdPath).Html

# Âncoras no padrão do GitHub (os links internos do manual foram escritos assim): minúsculas, sem
# pontuação, espaços -> '-', acentos mantidos. O Markdig gera outro formato (remove os acentos).
$html = [regex]::Replace($html, '<h([1-6]) id="[^"]*">(.*?)</h\1>', {
    param($m)
    $text = [System.Net.WebUtility]::HtmlDecode(([regex]::Replace($m.Groups[2].Value, '<[^>]+>', '')))
    $slug = ([regex]::Replace($text.ToLowerInvariant(), '[^\p{L}\p{M}\p{N} _-]', '')) -replace ' ', '-'
    "<h$($m.Groups[1].Value) id=""$slug"">$($m.Groups[2].Value)</h$($m.Groups[1].Value)>"
})

$title = 'Manual de Utilização — RestoreDB'
$page = @"
<!DOCTYPE html>
<html lang="pt-BR"><head><meta charset="utf-8"><title>$title</title>
<style>
  @page { size: A4; margin: 18mm 16mm; }
  body { font-family: 'Segoe UI', Arial, sans-serif; font-size: 10.5pt; line-height: 1.45; color: #1f2328; }
  h1 { font-size: 20pt; border-bottom: 2px solid #0b5cad; padding-bottom: 4px; color: #0b3d74; }
  h2 { font-size: 14.5pt; color: #0b3d74; border-bottom: 1px solid #d0d7de; padding-bottom: 3px; margin-top: 22px; page-break-after: avoid; }
  h3 { font-size: 12pt; color: #0b3d74; margin-top: 16px; page-break-after: avoid; }
  code { font-family: Consolas, monospace; font-size: 9.5pt; background: #f2f4f7; padding: 0 3px; border-radius: 3px; }
  pre { background: #f6f8fa; border: 1px solid #d0d7de; border-radius: 4px; padding: 8px; font-size: 8.5pt; overflow: hidden; white-space: pre; page-break-inside: avoid; }
  pre code { background: none; padding: 0; }
  table { border-collapse: collapse; width: 100%; margin: 8px 0; font-size: 9.5pt; page-break-inside: avoid; }
  th, td { border: 1px solid #d0d7de; padding: 4px 6px; vertical-align: top; text-align: left; }
  th { background: #eef3f9; }
  blockquote { margin: 8px 0; padding: 6px 10px; border-left: 4px solid #0b5cad; background: #f4f8fd; color: #333; }
  blockquote p { margin: 2px 0; }
  a { color: #0b5cad; text-decoration: none; }
  hr { border: none; border-top: 1px solid #d0d7de; margin: 18px 0; }
</style></head><body>
$html
</body></html>
"@

$stem = Join-Path ([System.IO.Path]::GetTempPath()) ("RestoreDB_manual_" + [guid]::NewGuid().ToString('N'))
$tmp = "$stem.html"
$tmpPdf = "$stem.pdf"
[System.IO.File]::WriteAllText($tmp, $page, [System.Text.UTF8Encoding]::new($false))

$browser = @(
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $browser) { throw 'Chrome/Edge não encontrado para gerar o PDF.' }

try {
    $uri = ([System.Uri]$tmp).AbsoluteUri
    & $browser --headless=new --disable-gpu --no-pdf-header-footer "--print-to-pdf=$tmpPdf" $uri 2>$null | Out-Null
    # O navegador pode retornar antes de gravar o arquivo por completo.
    for ($i = 0; $i -lt 30 -and -not (Test-Path $tmpPdf); $i++) { Start-Sleep -Milliseconds 500 }
    if (-not (Test-Path $tmpPdf)) { throw 'O navegador não gerou o PDF.' }
    Move-Item $tmpPdf $pdfPath -Force   # só substitui o PDF antigo quando o novo existe
    Write-Host "PDF gerado: $pdfPath ($([math]::Round((Get-Item $pdfPath).Length / 1KB)) KB)"
}
finally { Remove-Item $tmp, $tmpPdf -ErrorAction SilentlyContinue }
