# Consysto fork: builds the icon sets offered in Properties > Customization.
# ConsystoFolders.dll - own macOS-like folders in several colors with an emblem from Fluent UI System Icons (MIT).
# ConsystoObjects.dll - color objects from Fluent Emoji (MIT).
# desktop.ini stores an icon by its index, so new icons may only be APPENDED to the lists below, never inserted or reordered.
#
# Needs: Google Chrome (renders the SVG), rc.exe from Windows Kits and link.exe from Visual Studio.
# Sources: git sparse checkouts of microsoft/fluentui-system-icons (fonts) and microsoft/fluentui-emoji (assets/*/Color).

param(
	# Clones of microsoft/fluentui-system-icons and microsoft/fluentui-emoji (MIT)
	[Parameter(Mandatory)][string]$ThirdParty,
	[string]$Out = (Join-Path $PSScriptRoot '..\..\..\src\Files.App\Assets\Consysto\Icons'),
	[string]$Work = (Join-Path $env:TEMP 'ConsystoIcons')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Out = [IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force $Out, $Work | Out-Null

$chrome = @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe", "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
$rc = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter rc.exe | Where-Object FullName -like '*\x64\*' | Sort-Object FullName | Select-Object -Last 1 -ExpandProperty FullName
$link = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio' -Recurse -Filter link.exe -ErrorAction SilentlyContinue | Where-Object FullName -like '*Hostx64\x64*' | Sort-Object FullName | Select-Object -Last 1 -ExpandProperty FullName
if (-not $chrome -or -not $rc -or -not $link) { throw "Chrome, rc.exe or link.exe not found" }

$Cell = 256
$Columns = 16
$IcoSizes = 256, 48, 32, 24, 16

# ---------- Folders ----------

$colors = [ordered]@{
	blue   = '#3D9BF0'
	teal   = '#1FB5AD'
	green  = '#45AC4F'
	yellow = '#F0BE2A'
	orange = '#F4862A'
	red    = '#E5484D'
	purple = '#9A68DE'
	gray   = '#8A94A6'
}

# '' is the plain folder
$emblems = @('', 'book', 'image', 'music_note_2', 'video', 'document', 'code', 'archive', 'settings', 'star', 'heart',
	'home', 'briefcase', 'games', 'arrow_download', 'cloud', 'lock_closed', 'people', 'wrench', 'cube', 'ruler',
	'camera', 'airplane', 'hat_graduation', 'money', 'gift', 'animal_dog', 'leaf_one', 'window_console', 'globe', 'database')

$glyphs = Get-Content (Join-Path $ThirdParty 'fluentui-system-icons\fonts\FluentSystemIcons-Filled.json') -Raw | ConvertFrom-Json
function Get-GlyphCode([string]$name) {
	foreach ($size in 48, 32, 28, 24, 20, 16, 12) {
		$value = $glyphs."ic_fluent_$($name)_$($size)_filled"
		if ($null -ne $value) { return [int]$value }
	}
	throw "Glyph not found: $name"
}

function Get-Mix([string]$hex, [string]$toward, [double]$amount) {
	$a = [Drawing.ColorTranslator]::FromHtml($hex)
	$b = [Drawing.ColorTranslator]::FromHtml($toward)
	$r = [int]($a.R + ($b.R - $a.R) * $amount)
	$g = [int]($a.G + ($b.G - $a.G) * $amount)
	$bl = [int]($a.B + ($b.B - $a.B) * $amount)
	return '#{0:X2}{1:X2}{2:X2}' -f $r, $g, $bl
}

function Get-FolderSvg([int]$id, [string]$base, [string]$emblem) {
	$back = Get-Mix $base '#000000' 0.14
	$top = Get-Mix $base '#FFFFFF' 0.28
	$bottom = $base
	$ink = Get-Mix $base '#000000' 0.38
	$text = ''
	if ($emblem) {
		$code = '&#x{0:X};' -f (Get-GlyphCode $emblem)
		$text = "<text x='128' y='156' font-family='FSI' font-size='80' text-anchor='middle' dominant-baseline='central' fill='#FFFFFF' fill-opacity='0.35'>$code</text>" +
			"<text x='128' y='154' font-family='FSI' font-size='80' text-anchor='middle' dominant-baseline='central' fill='$ink' fill-opacity='0.9'>$code</text>"
	}

	return @"
<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 256 256' width='256' height='256'>
<defs>
<linearGradient id='g$id' x1='0' y1='0' x2='0' y2='1'><stop offset='0' stop-color='$top'/><stop offset='1' stop-color='$bottom'/></linearGradient>
<filter id='s$id' x='-10%' y='-10%' width='120%' height='130%'><feDropShadow dx='0' dy='3' stdDeviation='4' flood-color='#000' flood-opacity='0.22'/></filter>
</defs>
<g filter='url(#s$id)'>
<path d='M20 62a16 16 0 0 1 16-16h58a16 16 0 0 1 11.3 4.7L118 64h102a16 16 0 0 1 16 16v124H20z' fill='$back'/>
<rect x='32' y='72' width='192' height='60' rx='6' fill='#FFFFFF' fill-opacity='0.92'/>
<rect x='20' y='88' width='216' height='132' rx='16' fill='url(#g$id)'/>
<rect x='22' y='89' width='212' height='2' rx='1' fill='#FFFFFF' fill-opacity='0.45'/>
</g>
$text
</svg>
"@
}

# ---------- Objects ----------

$objects = @('Books', 'Camera', 'Guitar', 'Package', 'Card index dividers', 'Clapper board', 'Toolbox', 'House', 'Airplane',
	'Floppy disk', 'Laptop', 'Mobile phone', 'Video game', 'Musical note', 'Headphone', 'Framed picture', 'Artist palette',
	'Briefcase', 'Graduation cap', 'Money bag', 'Shopping cart', 'Wrapped gift', 'Dog face', 'Cat face', 'Seedling',
	'Evergreen tree', 'Sun', 'Snowflake', 'Globe showing europe-africa', 'Star', 'Red heart', 'Locked', 'Key', 'Gear',
	'Hammer and wrench', 'Triangular ruler', 'Straight ruler', 'Pencil', 'Memo', 'Calendar', 'Bookmark tabs',
	'Open file folder', 'File folder', 'Card file box', 'Desktop computer', 'Keyboard', 'Printer', 'Television',
	'Movie camera', 'Film frames', 'Microphone', 'Radio', 'Rocket', 'Automobile', 'Bicycle', 'Soccer ball', 'Basketball',
	'Trophy', 'Birthday cake', 'Hot beverage', 'Pizza', 'Red apple', 'Party popper', 'Balloon', 'Christmas tree',
	'Beach with umbrella', 'Camping', 'Mountain', 'World map', 'Link', 'Light bulb', 'Magnifying glass tilted left',
	'Chart increasing', 'Bar chart', 'Receipt', 'Credit card', 'Envelope', 'Inbox tray', 'Outbox tray', 'Wastebasket',
	'Dna', 'Test tube', 'Microscope', 'Stethoscope', 'Pill', 'Brick', 'Building construction', 'Nut and bolt',
	'Electric plug', 'Battery', 'Joystick', 'Puzzle piece', 'Teddy bear', 'Crown', 'Gem stone', 'Fire', 'Water wave', 'Cloud')

# ---------- Rendering ----------

function Invoke-Sheet([string]$name, [string[]]$cells, [string]$head) {
	$rows = [Math]::Ceiling($cells.Count / $Columns)
	$width = $Columns * $Cell
	$height = $rows * $Cell
	$html = New-Object Text.StringBuilder
	[void]$html.Append("<!doctype html><html><head><meta charset='utf-8'><style>html,body{margin:0;background:transparent;overflow:hidden}.c{position:absolute;width:${Cell}px;height:${Cell}px}.c img{display:block;width:216px;height:216px;margin:20px}$head</style></head><body>")
	for ($i = 0; $i -lt $cells.Count; $i++) {
		$x = ($i % $Columns) * $Cell
		$y = [Math]::Floor($i / $Columns) * $Cell
		[void]$html.Append("<div class='c' style='left:${x}px;top:${y}px'>$($cells[$i])</div>")
	}
	[void]$html.Append('</body></html>')

	$page = Join-Path $Work "$name.html"
	$shot = Join-Path $Work "$name.png"
	[IO.File]::WriteAllText($page, $html.ToString(), (New-Object Text.UTF8Encoding $false))
	Remove-Item $shot -ErrorAction SilentlyContinue

	$arguments = @('--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1',
		'--default-background-color=00000000', '--virtual-time-budget=8000', "--user-data-dir=`"$Work\chrome`"",
		"--window-size=$width,$height", "--screenshot=`"$shot`"", "`"file:///$($page -replace '\\', '/')`"")
	Start-Process -FilePath $chrome -ArgumentList $arguments -Wait -WindowStyle Hidden
	if (-not (Test-Path $shot)) { throw "Chrome did not render $name" }

	$sheet = [Drawing.Bitmap]::FromFile($shot)
	try {
		if ($sheet.Width -lt $width -or $sheet.Height -lt $height) { throw "Sheet $name is $($sheet.Width)x$($sheet.Height), expected ${width}x$height" }
		$icons = New-Item -ItemType Directory -Force (Join-Path $Work $name)
		$rcLines = New-Object Collections.Generic.List[string]
		for ($i = 0; $i -lt $cells.Count; $i++) {
			$rect = New-Object Drawing.Rectangle ((($i % $Columns) * $Cell), ([Math]::Floor($i / $Columns) * $Cell), $Cell, $Cell)
			$cellBitmap = $sheet.Clone($rect, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
			$ico = Join-Path $icons ('{0:D3}.ico' -f $i)
			Save-Ico $cellBitmap $ico
			$cellBitmap.Dispose()
			$rcLines.Add(('{0} ICON "{1}"' -f ($i + 1), ($ico -replace '\\', '\\')))
		}
	}
	finally {
		$sheet.Dispose()
	}

	$rcFile = Join-Path $Work "$name.rc"
	$resFile = Join-Path $Work "$name.res"
	[IO.File]::WriteAllLines($rcFile, $rcLines)
	& $rc /nologo /fo $resFile $rcFile
	if ($LASTEXITCODE -ne 0) { throw "rc failed for $name" }
	& $link /nologo /DLL /NOENTRY /MACHINE:X64 "/OUT:$(Join-Path $Out "$name.dll")" $resFile
	if ($LASTEXITCODE -ne 0) { throw "link failed for $name" }
	Remove-Item (Join-Path $Out "$name.lib"), (Join-Path $Out "$name.exp") -ErrorAction SilentlyContinue
	"$name.dll: $($cells.Count) icons"
}

function Save-Ico([Drawing.Bitmap]$source, [string]$path) {
	$images = New-Object Collections.Generic.List[byte[]]
	foreach ($size in $IcoSizes) {
		$bitmap = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
		$graphics = [Drawing.Graphics]::FromImage($bitmap)
		$graphics.InterpolationMode = 'HighQualityBicubic'
		$graphics.PixelOffsetMode = 'HighQuality'
		$graphics.CompositingQuality = 'HighQuality'
		$graphics.DrawImage($source, (New-Object Drawing.Rectangle 0, 0, $size, $size))
		$graphics.Dispose()
		$stream = New-Object IO.MemoryStream
		$bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
		$bitmap.Dispose()
		$images.Add($stream.ToArray())
	}

	$file = [IO.File]::Create($path)
	$writer = New-Object IO.BinaryWriter $file
	try {
		$writer.Write([uint16]0)
		$writer.Write([uint16]1)
		$writer.Write([uint16]$IcoSizes.Count)
		$offset = 6 + 16 * $IcoSizes.Count
		for ($i = 0; $i -lt $IcoSizes.Count; $i++) {
			$side = if ($IcoSizes[$i] -ge 256) { 0 } else { $IcoSizes[$i] }
			$writer.Write([byte]$side)
			$writer.Write([byte]$side)
			$writer.Write([byte]0)
			$writer.Write([byte]0)
			$writer.Write([uint16]1)
			$writer.Write([uint16]32)
			$writer.Write([uint32]$images[$i].Length)
			$writer.Write([uint32]$offset)
			$offset += $images[$i].Length
		}
		foreach ($image in $images) { $writer.Write($image) }
	}
	finally {
		$writer.Dispose()
	}
}

# Folders: one row block per color
$fontData = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $ThirdParty 'fluentui-system-icons\fonts\FluentSystemIcons-Filled.ttf')))
$folderCells = New-Object Collections.Generic.List[string]
$id = 0
foreach ($color in $colors.Values) {
	foreach ($emblem in $emblems) {
		$folderCells.Add((Get-FolderSvg $id $color $emblem))
		$id++
	}
}
Invoke-Sheet 'ConsystoFolders' $folderCells.ToArray() "@font-face{font-family:FSI;src:url(data:font/ttf;base64,$fontData)}"

# Objects: each SVG as its own image, so the gradient ids of different emoji do not collide
$objectCells = foreach ($object in $objects) {
	$svg = Get-ChildItem (Join-Path $ThirdParty "fluentui-emoji\assets\$object\Color") -Filter *.svg | Select-Object -First 1
	if (-not $svg) { throw "Emoji not found: $object" }
	"<img src='data:image/svg+xml;base64,$([Convert]::ToBase64String([IO.File]::ReadAllBytes($svg.FullName)))'>"
}
Invoke-Sheet 'ConsystoObjects' @($objectCells) ''

Copy-Item (Join-Path $ThirdParty 'fluentui-system-icons\LICENSE') (Join-Path $Out 'LICENSE-fluentui-system-icons.txt') -Force
Copy-Item (Join-Path $ThirdParty 'fluentui-emoji\LICENSE') (Join-Path $Out 'LICENSE-fluentui-emoji.txt') -Force
