@ECHO OFF
REM DpiFix derleme - .NET Framework csc.exe, harici bagimlilik yok.
SETLOCAL
PUSHD "%~dp0"

SET CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
IF NOT EXIST "%CSC%" SET CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
IF NOT EXIST "%CSC%" (
  echo [HATA] csc.exe bulunamadi. .NET Framework 4 kurulu olmali.
  POPD & EXIT /B 1
)

SET REF=/reference:System.dll,System.Drawing.dll,System.Windows.Forms.dll,System.ServiceProcess.dll
SET SRC=src\DpiCore.cs src\DpiFix.cs
SET RES=/resource:payload\x86\goodbyedpi.exe,pl_x86_goodbyedpi_exe ^
 /resource:payload\x86\WinDivert.dll,pl_x86_WinDivert_dll ^
 /resource:payload\x86\WinDivert32.sys,pl_x86_WinDivert32_sys ^
 /resource:payload\x86\WinDivert64.sys,pl_x86_WinDivert64_sys ^
 /resource:payload\x86_64\goodbyedpi.exe,pl_x86_64_goodbyedpi_exe ^
 /resource:payload\x86_64\WinDivert.dll,pl_x86_64_WinDivert_dll ^
 /resource:payload\x86_64\WinDivert64.sys,pl_x86_64_WinDivert64_sys

REM ---------- 1) DpiFix.exe : motor gomulu tek dosya uygulama ----------
"%CSC%" /nologo /target:winexe /codepage:65001 /optimize+ /out:DpiFix.exe ^
  /win32manifest:src\app.manifest %REF% %RES% %SRC%
IF ERRORLEVEL 1 (echo [HATA] DpiFix.exe derlenemedi. & POPD & EXIT /B 1)
echo [OK] DpiFix.exe

REM ---------- 2) selftest.exe : konsol teshis (UAC istemez, ayni kaynaklar gomulu) ----------
"%CSC%" /nologo /target:exe /codepage:65001 /out:selftest.exe %REF% %RES% %SRC% >NUL 2>&1
IF EXIST selftest.exe echo [OK] selftest.exe

REM ---------- 3) Setup.exe : kurulum dosyasi (DpiFix.exe gomulu) ----------
"%CSC%" /nologo /target:winexe /codepage:65001 /optimize+ /out:Setup.exe ^
  /win32manifest:src\app.manifest %REF% ^
  /resource:DpiFix.exe,payload_app ^
  src\Setup.cs
IF ERRORLEVEL 1 (echo [HATA] Setup.exe derlenemedi. & POPD & EXIT /B 1)
echo [OK] Setup.exe

POPD
ENDLOCAL
