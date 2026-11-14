@ECHO OFF
REM DpiFix derleme
REM   .NET arayuz : csc.exe (.NET Framework) - harici bagimlilik yok
REM   Rust motoru : cargo (zorunlu)
REM Cikti: dist\ klasoru (depoda tutulmaz, Release ile dagitilir)
SETLOCAL EnableDelayedExpansion
PUSHD "%~dp0"

SET CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
IF NOT EXIST "%CSC%" SET CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
IF NOT EXIST "%CSC%" (
  echo [HATA] csc.exe bulunamadi. .NET Framework 4 kurulu olmali.
  POPD & EXIT /B 1
)

CALL engine\build.cmd
IF ERRORLEVEL 1 (POPD & EXIT /B 1)
SET ENGINE_HASH=
SET ENGINE_VERSION=
FOR /F "usebackq delims=" %%H IN (`powershell -NoProfile -Command "$sha=[Security.Cryptography.SHA256]::Create(); $f=[IO.File]::OpenRead('payload\engine\dpifix-engine.exe'); try {[BitConverter]::ToString($sha.ComputeHash($f)).Replace('-','').ToLowerInvariant()} finally {$f.Dispose(); $sha.Dispose()}"`) DO SET ENGINE_HASH=%%H
FOR /F "tokens=2" %%V IN ('payload\engine\dpifix-engine.exe --version') DO SET ENGINE_VERSION=%%V
IF NOT DEFINED ENGINE_HASH (echo [HATA] Motor hash degeri okunamadi. & POPD & EXIT /B 1)
IF NOT DEFINED ENGINE_VERSION (echo [HATA] Motor surumu okunamadi. & POPD & EXIT /B 1)
> "src\EngineInfo.g.cs" echo static class EngineInfo { public const string Sha256 = "!ENGINE_HASH!"; public const string Version = "!ENGINE_VERSION!"; }

SET BRAND_RES=
IF EXIST "assets\brand\dpifix-mark.png" SET BRAND_RES=/resource:assets\brand\dpifix-mark.png,brand_mark
SET ICON_RES=
IF EXIST "assets\brand\dpifix.ico" SET ICON_RES=/win32icon:assets\brand\dpifix.ico

SET OUT=dist
IF NOT EXIST "%OUT%" MKDIR "%OUT%"

SET REF=/reference:System.dll,System.Drawing.dll,System.Windows.Forms.dll,System.ServiceProcess.dll
SET SRC=src\Ui.cs src\EngineInfo.g.cs src\DpiCore.cs src\DpiFix.cs
SET RES=/resource:payload\x86_64\WinDivert.dll,pl_x86_64_WinDivert_dll ^
 /resource:payload\x86_64\WinDivert64.sys,pl_x86_64_WinDivert64_sys ^
 /resource:payload\engine\dpifix-engine.exe,pl_engine

REM ---------- 1) DpiFix.exe : motorlar gomulu tek dosya uygulama ----------
"%CSC%" /nologo /target:winexe /codepage:65001 /optimize+ /out:%OUT%\DpiFix.exe ^
  /win32manifest:src\app.manifest !ICON_RES! !BRAND_RES! %REF% %RES% %SRC%
IF ERRORLEVEL 1 (echo [HATA] DpiFix.exe derlenemedi. & POPD & EXIT /B 1)
echo [OK] %OUT%\DpiFix.exe

REM ---------- 2) selftest.exe : konsol teshis (UAC istemez) ----------
"%CSC%" /nologo /target:exe /codepage:65001 /out:%OUT%\selftest.exe !BRAND_RES! %REF% %RES% %SRC%
IF ERRORLEVEL 1 (echo [HATA] selftest.exe derlenemedi. & POPD & EXIT /B 1)
echo [OK] %OUT%\selftest.exe

REM ---------- 3) Setup.exe : kurulum dosyasi (DpiFix.exe gomulu) ----------
"%CSC%" /nologo /target:winexe /codepage:65001 /optimize+ /out:%OUT%\Setup.exe ^
  /win32manifest:src\app.manifest !ICON_RES! %REF% ^
  /resource:%OUT%\DpiFix.exe,payload_app ^
  src\Setup.cs
IF ERRORLEVEL 1 (echo [HATA] Setup.exe derlenemedi. & POPD & EXIT /B 1)
echo [OK] %OUT%\Setup.exe

POPD
ENDLOCAL
