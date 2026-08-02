# ImageViewer

A WPF image viewer for Windows with HEIC, AVIF and RAW format support.

## Features

- Fast image loading via WIC and native decoders
- HEIC/HEIF/AVIF support
- RAW format support (CR2, CR3, NEF, ARW, DNG, and more)
- Mouse wheel zoom, pan, drag
- Fullscreen mode
- Thumbnail strip with scroll
- Minimap navigation
- Crop, rotate, save, copy
- Color picker
- File info panel

## Supported Formats

| Type | Extensions |
|------|-----------|
| Common | `.jpg` `.jpeg` `.png` `.bmp` `.gif` `.webp` `.tiff` `.tif` `.ico` |
| HEIC | `.heif` `.heic` `.avif` |
| RAW | `.cr2` `.cr3` `.nef` `.arw` `.dng` `.orf` `.rw2` `.pef` `.raf` `.3fr` `.kdc` `.mrw` `.nrw` `.raw` `.rwl` `.srw` `.x3f` `.erf` `.mef` `.mos` `.iiq` |

## Build

```bash
dotnet build -c Release
```

Output: `bin/Release/net9.0-windows10.0.19041.0/ImageViewer.exe`

## Register File Associations

Run `register.bat` as administrator to associate supported image formats.
