$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# A 16-pixel shield and verification mark, scaled without interpolation.
$pixels = @(
    '................',
    '...BBBBBBBBBB...',
    '..BFFFFFFFFFFB..',
    '..BFFFFFFFFFFB..',
    '..BFFFFFFFGGFB..',
    '..BFFFFFFGGGFB..',
    '..BFGGFFGGGFFB..',
    '..BFGGGGGGFFFB..',
    '..BFFGGGGFFFFB..',
    '...BFFGGFFFFB...',
    '...BFFFFFFFFB...',
    '....BFFFFFFB....',
    '.....BFFFFB.....',
    '......BFFB......',
    '.......BB.......',
    '................'
)
$palette = @{
    '.' = [System.Drawing.Color]::Transparent
    'B' = [System.Drawing.ColorTranslator]::FromHtml('#234A80')
    'F' = [System.Drawing.ColorTranslator]::FromHtml('#3874CB')
    'G' = [System.Drawing.ColorTranslator]::FromHtml('#A7F3C1')
}
$frames = foreach ($size in @(16, 32, 48)) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $stream = [System.IO.MemoryStream]::new()
    try {
        for ($y = 0; $y -lt $size; $y++) {
            for ($x = 0; $x -lt $size; $x++) {
                $pixel = [string]$pixels[[int][Math]::Floor($y * 16 / $size)][[int][Math]::Floor($x * 16 / $size)]
                $bitmap.SetPixel($x, $y, $palette[$pixel])
            }
        }
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        [PSCustomObject]@{ Size = $size; Bytes = $stream.ToArray() }
    }
    finally {
        $bitmap.Dispose()
        $stream.Dispose()
    }
}

$output = Join-Path $PSScriptRoot '../TrueModel/wwwroot/favicon.ico'
$writer = [System.IO.BinaryWriter]::new([System.IO.File]::Create($output))
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $writer.Write([byte]$frame.Size)
        $writer.Write([byte]$frame.Size)
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
}
finally { $writer.Dispose() }
