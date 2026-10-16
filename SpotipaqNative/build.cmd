@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem SpotipaqNative.dll - XP / Pentium II helper: https via OpenSSL 1.0.2u, big-number maths, Vorbis decoding + waveOut.
rem Same proven toolchain as NativePlayer: VS2017 14.16 x86, /arch:IA32 /MT, subsystem 5.01.
rem   build.cmd          build and copy to ..\Spotipaq\native
rem   build.cmd clean    delete build output
set "ROOT=%~dp0"
set "OBJ=%ROOT%obj"
set "DEST=%ROOT%..\Spotipaq\native"

if /i "%~1"=="clean" (
  if exist "%OBJ%" rmdir /s /q "%OBJ%"
  if exist "%DEST%\SpotipaqNative.dll" del /q "%DEST%\SpotipaqNative.dll"
  echo SpotipaqNative: cleaned.
  exit /b 0
)

rem ---- locate the XP OpenSSL build (static ssl.lib / crypto.lib) ----
set "OSSL="
if defined OPENSSL_XP if exist "%OPENSSL_XP%\include\openssl\ssl.h" set "OSSL=%OPENSSL_XP%"
if not defined OSSL if exist "%ROOT%..\openssl-1.0.2u-xp\include\openssl\ssl.h" set "OSSL=%ROOT%..\openssl-1.0.2u-xp"
rem Local machine override (ignored by Git); use it only when no environment override was supplied.
if not defined OSSL if not defined OPENSSL_XP if exist "%ROOT%openssl.local.user" (
  rem Read the path and expand environment placeholders such as %%USERPROFILE%%.
  rem A FOR variable is substituted before CALL's second expansion pass.
  for /f "usebackq delims=" %%P in ("%ROOT%openssl.local.user") do call set "OSSL=%%P"
  if defined OSSL call if not exist "%%OSSL%%\include\openssl\ssl.h" set "OSSL="
)
if not defined OSSL (
  echo SpotipaqNative : error : openssl-1.0.2u-xp not found. Set OPENSSL_XP to its folder.
  exit /b 1
)
echo SpotipaqNative: OpenSSL = "%OSSL%"

rem ---- up to date? (only recompile when a source file or this script changed) ----
set "SRCS=spqnative.c spq_audio.c spq_vorbis.c spq_vorbis.h spq_vorbis_db.inc build.cmd"
set "NEED=1"
if exist "%OBJ%\SpotipaqNative.dll" if exist "%DEST%\SpotipaqNative.dll" if exist "%OBJ%\stamp\build.cmd" (
  set "NEED=0"
  for %%F in (%SRCS%) do (
    if not exist "%OBJ%\stamp\%%F" set "NEED=1"
    for /f %%N in ('xcopy /D /L /Y "%ROOT%%%F" "%OBJ%\stamp\" ^| find /c /i "%%F"') do if not "%%N"=="0" set "NEED=1"
  )
)
if "%NEED%"=="0" (echo SpotipaqNative: up to date.& exit /b 0)

set "VS_ENV=C:\Program Files (x86)\Microsoft Visual Studio\2017\BuildTools\VC\Auxiliary\Build\vcvarsall.bat"
if not exist "%VS_ENV%" (echo SpotipaqNative : error : VS2017 Build Tools not found.& exit /b 1)
for %%V in (INCLUDE LIB LIBPATH VSINSTALLDIR VCINSTALLDIR VCIDEInstallDir VCToolsInstallDir VCToolsRedistDir VCToolsVersion VisualStudioVersion DevEnvDir VSCMD_VER VSCMD_ARG_app_plat VSCMD_ARG_HOST_ARCH VSCMD_ARG_TGT_ARCH __VSCMD_PREINIT_PATH __VSCMD_script_err_count WindowsSdkDir WindowsSDKVersion WindowsSdkBinPath WindowsSdkVerBinPath WindowsLibPath UniversalCRTSdkDir UCRTVersion ExtensionSdkDir Platform) do set "%%V="
call "%VS_ENV%" x86 10.0.19041.0 >nul
if errorlevel 1 (echo SpotipaqNative : error : vcvarsall failed.& exit /b 1)
set "VS_BIN=C:\Program Files (x86)\Microsoft Visual Studio\2017\BuildTools\VC\Tools\MSVC\14.16.27023\bin\Hostx86\x86"
if not exist "%VS_BIN%\cl.exe" (echo SpotipaqNative : error : VS2017 14.16 x86 cl.exe not found.& exit /b 1)

if not exist "%OBJ%" mkdir "%OBJ%"
if not exist "%DEST%" mkdir "%DEST%"

"%VS_BIN%\cl.exe" /nologo /LD /O2 /MT /arch:IA32 /W3 /DWINVER=0x0501 /D_WIN32_WINNT=0x0501 /D_CRT_SECURE_NO_WARNINGS ^
  /I"%OSSL%\include" /Fo"%OBJ%\\" "%ROOT%spqnative.c" "%ROOT%spq_audio.c" "%ROOT%spq_vorbis.c" ^
  /link /NOLOGO /MACHINE:X86 /SUBSYSTEM:WINDOWS,5.01 /OUT:"%OBJ%\SpotipaqNative.dll" /IMPLIB:"%OBJ%\SpotipaqNative.lib" ^
  /LIBPATH:"%OSSL%\lib" ssl.lib crypto.lib ws2_32.lib gdi32.lib advapi32.lib user32.lib crypt32.lib winmm.lib
if errorlevel 1 (echo SpotipaqNative : error : compile/link failed.& exit /b 1)

rem No SSE in OUR code: the Pentium II would crash with an illegal instruction. (The whole DLL can't be
rem checked: the static VS2017 C runtime contains SSE2 memcpy/strlen variants, but it only uses them after
rem checking the CPU, so they never run on the Compaq. OpenSSL was checked when it was built with no-asm.)
for %%O in (spqnative spq_audio spq_vorbis) do (
  "%VS_BIN%\dumpbin.exe" /DISASM "%OBJ%\%%O.obj" | findstr /i /c:"xmm" >nul
  if not errorlevel 1 (echo SpotipaqNative : error : SSE instructions found in %%O.obj.& exit /b 1)
)

copy /y "%OBJ%\SpotipaqNative.dll" "%DEST%\" >nul || (echo SpotipaqNative : error : cannot copy - is Spotipaq.exe running?& exit /b 1)
if not exist "%OBJ%\stamp" mkdir "%OBJ%\stamp"
for %%F in (%SRCS%) do copy /y "%ROOT%%%F" "%OBJ%\stamp\" >nul
echo SpotipaqNative: SpotipaqNative.dll is in "%DEST%"
exit /b 0
