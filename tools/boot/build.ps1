param([switch]$Verify)
$ErrorActionPreference = 'Stop'
# Development-only rebuild. GNU binutils in an existing WSL distribution are
# assembler/linker tools, not runtime dependencies or bundled boot code.
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$output = Join-Path $repo 'artifacts/bootstrap-build'
New-Item -ItemType Directory -Force $output | Out-Null
foreach ($item in @(@('dos98', '0x7c00', 'dos98'), @('mbr', '0x600', 'dosmbr'))) {
    $source = (wsl --exec wslpath -u ((Join-Path $PSScriptRoot ($item[0] + '.S')).Replace('\', '/'))).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'WSL path conversion failed.' }
    $object = (wsl --exec wslpath -u ((Join-Path $output ($item[0] + '.o')).Replace('\', '/'))).Trim()
    $binary = Join-Path $output ($item[2] + '.bin')
    $unixBinary = (wsl --exec wslpath -u ($binary.Replace('\', '/'))).Trim()
    wsl --exec as --32 -o $object $source
    if ($LASTEXITCODE -ne 0) { throw 'Bootstrap assembly failed.' }
    wsl --exec ld -m elf_i386 -Ttext $item[1] --oformat binary -o $unixBinary $object
    if ($LASTEXITCODE -ne 0) { throw 'Bootstrap linking failed.' }
    if ((Get-Item -LiteralPath $binary).Length -ne 512) { throw 'Bootstrap must be exactly 512 bytes.' }
    $target = Join-Path $repo ('src/Everboot/Services/Vm/' + $item[2] + '.bin')
    if ($Verify) {
        if ((Get-FileHash -LiteralPath $binary).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
            throw ('Bootstrap differs from source: ' + $item[2])
        }
        Write-Output ('Verified ' + $item[2] + '.bin')
    } else {
        Copy-Item -LiteralPath $binary -Destination $target
    }
}
