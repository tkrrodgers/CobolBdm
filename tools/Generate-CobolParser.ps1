# Regenerates the C# COBOL-85 parser (MlCpu\src\MlCpu.Cobol.Antlr\Generated) from Grammar\Cobol85.g4 with
# tools\antlr\antlr4-4.13.1-complete.jar (needs Java 11 or later). The output is checked in; on the unmodified
# grammars-v4 grammar this command reproduces the checked-in files exactly. Local grammar changes: docs/AST-GRAMMAR.md.
param([string]$Java = $(if ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME 'bin\java.exe' } else { 'java' }))

$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo 'MlCpu\src\MlCpu.Cobol.Antlr'
$jar = Join-Path $repo 'tools\antlr\antlr4-4.13.1-complete.jar'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('antlr-' + [guid]::NewGuid())
Push-Location $proj
try {
    & $Java -jar $jar -Dlanguage=CSharp -package MlCpu.Cobol.Antlr.Generated -visitor -no-listener -o $tmp Grammar/Cobol85.g4
    if ($LASTEXITCODE -ne 0) { throw "ANTLR failed (exit $LASTEXITCODE)" }
    foreach ($f in 'Cobol85Parser.cs', 'Cobol85Lexer.cs', 'Cobol85Visitor.cs', 'Cobol85BaseVisitor.cs') {
        Copy-Item (Join-Path $tmp $f) (Join-Path $proj "Generated\$f") -Force
    }
    Write-Host "regenerated $proj\Generated from Grammar\Cobol85.g4"
}
finally {
    Pop-Location
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
