@ECHO OFF
chcp 65001 >NUL
SETLOCAL
PUSHD "%~dp0"

REM ============================================================
REM  DPI Fix - GitHub'a yayinlama
REM  Gereksinim: GitHub CLI (gh) + giris
REM  Kurulum : winget install --id GitHub.cli -e
REM  Giris   : gh auth login
REM ============================================================

SET REPO=dpi-fix
SET DESC=Turkiye icin DPI/DNS zorlamasini kaldiran Windows uygulamasi (GoodbyeDPI tabanli)

WHERE gh >NUL 2>&1
IF ERRORLEVEL 1 (
  echo [HATA] gh CLI bulunamadi.
  echo         Kur: winget install --id GitHub.cli -e
  GOTO :manuel
)

gh auth status >NUL 2>&1
IF ERRORLEVEL 1 (
  echo [HATA] GitHub girisi yapilmamis.
  echo         Calistir: gh auth login
  GOTO :manuel
)

echo [1/2] Depo olusturuluyor ve push ediliyor: %REPO%
gh repo create %REPO% --public --source=. --remote=origin --push --description "%DESC%"
IF ERRORLEVEL 1 (
  echo.
  echo [HATA] Olusturma basarisiz. Depo zaten varsa once uzak adresi ekleyip push edin:
  GOTO :manuel
)

echo [2/2] Tamamlandi.
gh repo view --web
GOTO :son

:manuel
echo.
echo --- Elle yayinlama ---
echo 1^) https://github.com/new adresinde "%REPO%" adinda PUBLIC depo acin (README/gitignore EKLEMEYIN)
echo 2^) Su komutlari bu klasorde calistirin:
echo.
echo    git remote add origin https://github.com/KULLANICI/%REPO%.git
echo    git branch -M main
echo    git push -u origin main
echo.

:son
POPD
ENDLOCAL
PAUSE