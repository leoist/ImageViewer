@echo off
set "APP=%~dp0bin\Release\net9.0-windows10.0.19041.0\ImageViewer.exe"
set "PROGID=ImageViewer.ImageViewer"

echo Registering %PROGID%...

reg add "HKCU\Software\Classes\%PROGID%" /ve /d "ImageViewer" /f >nul
reg add "HKCU\Software\Classes\%PROGID%" /v "FriendlyTypeName" /d "ImageViewer" /f >nul
reg add "HKCU\Software\Classes\%PROGID%" /v "EditFlags" /t REG_DWORD /d 2 /f >nul
reg add "HKCU\Software\Classes\%PROGID%\shell\open\command" /ve /d "\"%APP%\" \"%%1\"" /f >nul
reg add "HKCU\Software\Classes\%PROGID%\DefaultIcon" /ve /d "\"%APP%\",1" /f >nul

echo Registering file types...
for %%e in (
    .jpg .jpeg .png .bmp .gif .webp .tiff .tif .ico
    .heif .heic .avif
    .cr2 .cr3 .nef .arw .dng .orf .rw2 .pef
    .raf .3fr .kdc .mrw .nrw .raw .rwl .srw
    .x3f .erf .mef .mos .iiq
) do (
    reg add "HKCU\Software\Classes\%%~e" /ve /d "%PROGID%" /f >nul
)

echo Refreshing shell...
assoc .dummy >nul 2>&1

echo Done.
pause
