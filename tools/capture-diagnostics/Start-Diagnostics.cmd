@echo off
setlocal
set "SBMS_CAPTURE_RUN=%~dp0run.ps1"
powershell.exe -NoLogo -NoProfile -Command "$ErrorActionPreference='Stop'; $p=$env:SBMS_CAPTURE_RUN; if((Get-AuthenticodeSignature -LiteralPath $p).Status -ne 'Valid'){throw 'Diagnostic script signature is not trusted.'}; Unblock-File -LiteralPath $p; if((Get-ExecutionPolicy) -eq 'Restricted'){Set-ExecutionPolicy -Scope Process RemoteSigned -Force}; & $p"
set "SBMS_CAPTURE_EXIT=%ERRORLEVEL%"
echo.
echo Reports: %~dp0reports
echo Exit code: %SBMS_CAPTURE_EXIT%
pause
exit /b %SBMS_CAPTURE_EXIT%
