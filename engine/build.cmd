@ECHO OFF
REM Rust motorunu derler ve payload\engine\ altina kopyalar.
SETLOCAL
PUSHD "%~dp0"

SET CARGO=cargo
WHERE cargo >NUL 2>&1
IF ERRORLEVEL 1 (
  IF EXIST "%USERPROFILE%\.cargo\bin\cargo.exe" (
    SET CARGO=%USERPROFILE%\.cargo\bin\cargo.exe
  ) ELSE (
    echo [ATLA] cargo bulunamadi - Rust motoru derlenmeyecek.
    POPD & EXIT /B 1
  )
)

echo Rust motoru derleniyor...
"%CARGO%" build --release --manifest-path "%~dp0Cargo.toml"
IF ERRORLEVEL 1 (echo [HATA] Rust motoru derlenemedi. & POPD & EXIT /B 1)

IF NOT EXIST "%~dp0..\payload\engine" MKDIR "%~dp0..\payload\engine"
COPY /Y "%~dp0target\release\dpifix-engine.exe" "%~dp0..\payload\engine\dpifix-engine.exe" >NUL
IF ERRORLEVEL 1 (echo [HATA] Kopyalama basarisiz. & POPD & EXIT /B 1)

echo [OK] payload\engine\dpifix-engine.exe
POPD
ENDLOCAL