$app = "C:\Users\Mason\Desktop\ImageViewerWPF\bin\Release\net9.0-windows10.0.19041.0\ImageViewer.exe"
$progId = "ImageViewer.ImageViewer"

# ProgID
$p = "HKCU:\Software\Classes\$progId"
New-Item -Path $p -Force | Out-Null
New-ItemProperty -Path $p -Name "(default)" -Value "ImageViewerWPF" -Force | Out-Null
New-ItemProperty -Path $p -Name "FriendlyTypeName" -Value "ImageViewerWPF" -Force | Out-Null
New-ItemProperty -Path $p -Name "EditFlags" -Value 2 -PropertyType DWord -Force | Out-Null

New-Item -Path "$p\shell\open\command" -Force | Out-Null
New-ItemProperty -Path "$p\shell\open\command" -Name "(default)" -Value "`"$app`" `"%1`"" -Force | Out-Null

New-Item -Path "$p\DefaultIcon" -Force | Out-Null
New-ItemProperty -Path "$p\DefaultIcon" -Name "(default)" -Value "`"$app`",1" -Force | Out-Null

# Extensions
$exts = @(
    ".jpg",".jpeg",".png",".bmp",".gif",".webp",".tiff",".tif",".ico",
    ".heif",".heic",".avif",
    ".cr2",".cr3",".nef",".arw",".dng",".orf",".rw2",".pef",
    ".raf",".3fr",".kdc",".mrw",".nrw",".raw",".rwl",".srw",
    ".x3f",".erf",".mef",".mos",".iiq"
)
foreach ($ext in $exts) {
    New-Item -Path "HKCU:\Software\Classes\$ext" -Force | Out-Null
    New-ItemProperty -Path "HKCU:\Software\Classes\$ext" -Name "(default)" -Value $progId -Force | Out-Null
}

# Notify shell
$null = $app
& cmd /c assoc .dummy 2> $null | Out-Null
& cmd /c ftype .dummy 2> $null | Out-Null
