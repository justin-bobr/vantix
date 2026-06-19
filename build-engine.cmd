@echo off
setlocal
cd /d "%~dp0"

set "GODOT_SRC=%~dp0EngineSource\godot"
set "ENGINE_DIR=%~dp0Engine"
set "TEMPLATES_DIR=%ENGINE_DIR%\Templates"

if not exist "%GODOT_SRC%\SConstruct" (
    where git >nul 2>&1
    if errorlevel 1 (
        echo.
        echo [ERR] git not in PATH
        echo Install: https://git-scm.com/download/win
        exit /b 1
    )
    if not exist "%~dp0EngineSource" mkdir "%~dp0EngineSource"
    echo.
    echo [setup] Godot source missing - fetching 4.7-stable commit 5b4e0cb, depth=1...
    git init "%GODOT_SRC%"
    git -C "%GODOT_SRC%" remote add origin https://github.com/godotengine/godot.git
    git -C "%GODOT_SRC%" fetch --depth 1 origin 5b4e0cb0fd279832bbdd69fed5354d4e5ad26f88
    if errorlevel 1 (
        echo [ERR] git fetch failed
        exit /b 1
    )
    git -C "%GODOT_SRC%" checkout --detach FETCH_HEAD
    if errorlevel 1 (
        echo [ERR] git checkout failed
        exit /b 1
    )
    echo [setup] EngineSource ready at %GODOT_SRC%
    echo.
)

set "PATCH_DIR=%~dp0EngineSource\patches"
set "PATCH_STATE=%~dp0EngineSource\.applied-patches.txt"
set "PATCH_STATE_NEW=%~dp0EngineSource\.applied-patches.new"

powershell -NoProfile -Command "$ErrorActionPreference='Stop'; $p=@(Get-ChildItem '%PATCH_DIR%\*.patch' -ErrorAction SilentlyContinue | Sort-Object Name); if($p.Count -gt 0){ $p | ForEach-Object { '{0} {1}' -f (Get-FileHash $_.FullName -Algorithm SHA1).Hash, $_.Name } | Set-Content -Path '%PATCH_STATE_NEW%' -Encoding ASCII } else { Set-Content -Path '%PATCH_STATE_NEW%' -Value '' -Encoding ASCII -NoNewline }"
if errorlevel 1 (
    echo [ERR] Failed to compute patch fingerprint via PowerShell
    exit /b 1
)

if not exist "%PATCH_STATE%" goto :patches_need_reapply
fc /b "%PATCH_STATE%" "%PATCH_STATE_NEW%" >nul 2>&1
if errorlevel 1 goto :patches_need_reapply

echo.
echo [patch] no patch changes since last build - skipping reset and apply
del "%PATCH_STATE_NEW%" >nul 2>&1
goto :patches_done

:patches_need_reapply
echo.
echo === Patches changed - hard-resetting EngineSource/godot and re-applying ===
git -C "%GODOT_SRC%" reset --hard HEAD
if errorlevel 1 goto :patch_reset_failed
git -C "%GODOT_SRC%" clean -fd
if errorlevel 1 goto :patch_clean_failed

if exist "%PATCH_DIR%\*.patch" (
    for %%P in ("%PATCH_DIR%\*.patch") do (
        git -C "%GODOT_SRC%" apply --whitespace=nowarn "%%P"
        if errorlevel 1 (
            echo [ERR] git apply failed for %%~nxP
            echo The source tree may have drifted from what the patch expects.
            echo Inspect the patch and reconcile manually.
            exit /b 1
        )
        echo [patch] applied %%~nxP
    )
) else (
    echo [patch] no patches present - tree is now vanilla
)

echo [glue] engine tree changed - dropping mono glue + assemblies so they regenerate
if exist "%GODOT_SRC%\modules\mono\glue\GodotSharp\GodotSharp\Generated" rmdir /s /q "%GODOT_SRC%\modules\mono\glue\GodotSharp\GodotSharp\Generated"
if exist "%GODOT_SRC%\modules\mono\glue\GodotSharp\GodotSharpEditor\Generated" rmdir /s /q "%GODOT_SRC%\modules\mono\glue\GodotSharp\GodotSharpEditor\Generated"
if exist "%GODOT_SRC%\bin\GodotSharp" rmdir /s /q "%GODOT_SRC%\bin\GodotSharp"

move /y "%PATCH_STATE_NEW%" "%PATCH_STATE%" >nul
goto :patches_done

:patch_reset_failed
echo.
echo [ERR] git reset --hard HEAD failed in %GODOT_SRC%
exit /b 1
:patch_clean_failed
echo.
echo [ERR] git clean -fd failed in %GODOT_SRC%
exit /b 1

:patches_done
echo.

if defined CI goto :winget_ok
where winget >nul 2>&1
if errorlevel 1 (
    echo.
    echo [ERR] winget not available - requires Windows 10 1809+ or Windows 11
    echo Install Python 3.10+, scons, and VS 2022 with C++ Build Tools manually.
    exit /b 1
)
:winget_ok

where python >nul 2>&1
if errorlevel 1 (
    echo.
    echo [setup] Python not installed - winget install Python.Python.3.12 ...
    winget install -e --id Python.Python.3.12 --silent --accept-source-agreements --accept-package-agreements
    echo.
    echo Python installed. Close cmd and open a new one,
    echo then run build-engine.cmd again. PATH update needs a fresh shell.
    exit /b 0
)

python -c "import SCons" >nul 2>&1
if errorlevel 1 (
    echo.
    echo [setup] scons not installed - installing via pip...
    python -m pip install scons
    if errorlevel 1 (
        echo [ERR] pip install scons failed
        exit /b 1
    )
    python -c "import SCons" >nul 2>&1
    if errorlevel 1 (
        echo [ERR] scons installed but Python cannot import it
        exit /b 1
    )
)

reg query "HKLM\SOFTWARE\Microsoft\VisualStudio\Setup" /v SharedInstallationPath >nul 2>&1
if errorlevel 1 (
    echo.
    echo [setup] VS 2022 Build Tools not installed - winget install, ~6 GB ...
    winget install -e --id Microsoft.VisualStudio.2022.BuildTools --silent --accept-source-agreements --accept-package-agreements --override "--wait --quiet --norestart --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
    if errorlevel 1 (
        echo [ERR] winget install VS Build Tools failed
        exit /b 1
    )
    echo.
    echo VS 2022 Build Tools installed. Close cmd and open a new one,
    echo then run build-engine.cmd again.
    exit /b 0
)

if not exist "%LOCALAPPDATA%\Godot\build_deps\agility_sdk" (
    echo.
    echo [setup] Installing D3D12 SDK dependencies...
    pushd "%GODOT_SRC%"
    python misc\scripts\install_d3d12_sdk_windows.py
    popd
)
if not exist "%LOCALAPPDATA%\Godot\build_deps\agility_sdk" (
    echo [ERR] D3D12 SDK install did not produce the expected files.
    echo Alternative: edit build-engine.cmd, add 'd3d12=no' to the scons args.
    exit /b 1
)

if not exist "%TEMPLATES_DIR%" mkdir "%TEMPLATES_DIR%"

set "EDITOR_EXE=%~dp0Engine\Editor_console.exe"
if not exist "%EDITOR_EXE%" (
    echo.
    echo [ERR] Editor binary missing at %EDITOR_EXE%
    echo Run setup-editor.cmd first.
    exit /b 1
)
if not exist "%GODOT_SRC%\modules\mono\glue\GodotSharp\GodotSharp\Generated" (
    echo.
    echo [mono] generating mono glue via stock Editor...
    "%EDITOR_EXE%" --headless --generate-mono-glue "%GODOT_SRC%\modules\mono\glue"
    if errorlevel 1 (
        echo [ERR] mono glue generation failed
        exit /b 1
    )
)
if not exist "%GODOT_SRC%\bin\GodotSharp\Api" (
    echo.
    echo [mono] building managed assemblies...
    if exist "%GODOT_SRC%\bin\GodotSharp" rmdir /s /q "%GODOT_SRC%\bin\GodotSharp"
    if not exist "%ENGINE_DIR%\GodotSharp\Tools\nupkgs" mkdir "%ENGINE_DIR%\GodotSharp\Tools\nupkgs"
    pushd "%GODOT_SRC%"
    python modules\mono\build_scripts\build_assemblies.py --godot-output-dir=bin --push-nupkgs-local "%ENGINE_DIR%\GodotSharp\Tools\nupkgs"
    popd
)
if not exist "%GODOT_SRC%\bin\GodotSharp\Api" (
    echo [ERR] build_assemblies.py did not produce bin\GodotSharp\Api
    echo Re-run build-engine.cmd or check the python script output above.
    exit /b 1
)

echo.
echo === GODOT EDITOR BUILD - full, mono, patched ===
pushd "%GODOT_SRC%"
python -m SCons platform=windows target=editor production=yes module_mono_enabled=yes winrt=no accesskit=no
set "SCONS_EDITOR_RC=%errorlevel%"
popd

if not "%SCONS_EDITOR_RC%"=="0" (
    echo.
    echo [ERR] editor scons build failed with rc=%SCONS_EDITOR_RC%
    exit /b 1
)

echo.
echo [Copy] godot.windows.editor.x86_64.mono.exe -^> Engine\Editor.exe
copy /Y "%GODOT_SRC%\bin\godot.windows.editor.x86_64.mono.exe" "%ENGINE_DIR%\Editor.exe" >nul 2>&1
if errorlevel 1 goto :editor_copy_failed

if exist "%GODOT_SRC%\bin\godot.windows.editor.x86_64.mono.console.exe" (
    echo [Copy] godot.windows.editor.x86_64.mono.console.exe -^> Engine\Editor_console.exe
    copy /Y "%GODOT_SRC%\bin\godot.windows.editor.x86_64.mono.console.exe" "%ENGINE_DIR%\Editor_console.exe" >nul 2>&1
    if errorlevel 1 goto :editor_console_copy_failed
)
goto :editor_copy_ok

:editor_copy_failed
echo.
echo [ERR] copy of Editor.exe failed.
echo Source: %GODOT_SRC%\bin\godot.windows.editor.x86_64.mono.exe
echo Target: %ENGINE_DIR%\Editor.exe
echo Most common cause: the Godot Editor is currently running, which locks
echo the .exe. Close all Godot Editor windows and re-run build-engine.cmd.
echo Other causes: source file missing, target dir not writable.
exit /b 1

:editor_console_copy_failed
echo.
echo [ERR] copy of Editor_console.exe failed - the file is most likely in use.
echo Close Editor_console.exe and re-run build-engine.cmd.
exit /b 1

:editor_copy_ok
if not exist "%GODOT_SRC%\bin\GodotSharp" goto :godotsharp_copy_ok
if not exist "%ENGINE_DIR%\GodotSharp" goto :godotsharp_xcopy
rmdir /s /q "%ENGINE_DIR%\GodotSharp"
if errorlevel 1 goto :godotsharp_remove_failed

:godotsharp_xcopy
xcopy /E /I /Y "%GODOT_SRC%\bin\GodotSharp" "%ENGINE_DIR%\GodotSharp" >nul
if errorlevel 1 goto :godotsharp_copy_failed
goto :godotsharp_copy_ok

:godotsharp_remove_failed
echo.
echo [ERR] Could not delete %ENGINE_DIR%\GodotSharp before re-copying.
echo This usually means a Godot host process still has the managed runtime DLLs
echo (GodotSharp.dll, hostfxr.dll, etc.) loaded. Close ALL Godot windows
echo (Editor.exe + Editor_console.exe + any running client/server build) and
echo re-run build-engine.cmd.
exit /b 1

:godotsharp_copy_failed
echo.
echo [ERR] xcopy of GodotSharp folder failed.
echo Source: %GODOT_SRC%\bin\GodotSharp
echo Target: %ENGINE_DIR%\GodotSharp
echo Most likely cause: a Godot host process has runtime DLLs loaded - close
echo it and re-run. Otherwise verify the source folder exists and the target
echo is writable.
exit /b 1

:godotsharp_copy_ok

echo.
echo === GODOT TEMPLATE BUILD - custom, stripped, mono ===
echo Source:    %GODOT_SRC%
echo Output:    %TEMPLATES_DIR%\windows_release.x86_64.exe
echo.

pushd "%GODOT_SRC%"
python -m SCons platform=windows target=template_release production=yes ^
    winrt=no accesskit=no ^
    module_mono_enabled=yes ^
    module_bullet_enabled=no ^
    module_navigation_2d_enabled=no ^
    module_navigation_3d_enabled=no ^
    module_webxr_enabled=no ^
    module_openxr_enabled=no ^
    module_mobile_vr_enabled=no ^
    module_csg_enabled=no ^
    module_gridmap_enabled=no ^
    module_camera_enabled=no ^
    module_websocket_enabled=no ^
    module_webrtc_enabled=no ^
    module_enet_enabled=no ^
    module_multiplayer_enabled=no ^
    module_mbedtls_enabled=no ^
    module_upnp_enabled=no ^
    module_basis_universal_enabled=no ^
    module_squish_enabled=no ^
    module_gltf_enabled=no ^
    module_ktx_enabled=no ^
    module_godot_physics_2d_enabled=no ^
    module_godot_physics_3d_enabled=no
set "SCONS_RC=%errorlevel%"
popd

if not "%SCONS_RC%"=="0" (
    echo.
    echo [ERR] scons build failed with rc=%SCONS_RC%
    echo If error mentions a missing module dependency,
    echo comment out the corresponding disable line in build-engine.cmd.
    exit /b 1
)

echo.
echo [Copy] template -^> Engine\Templates
if exist "%GODOT_SRC%\bin\godot.windows.template_release.x86_64.mono.exe" (
    copy /Y "%GODOT_SRC%\bin\godot.windows.template_release.x86_64.mono.exe" "%TEMPLATES_DIR%\windows_release.x86_64.exe" >nul
) else (
    copy /Y "%GODOT_SRC%\bin\godot.windows.template_release.x86_64.exe" "%TEMPLATES_DIR%\windows_release.x86_64.exe" >nul
)
if not exist "%GODOT_SRC%\bin\GodotSharp" goto :templates_godotsharp_ok
if not exist "%TEMPLATES_DIR%\GodotSharp" goto :templates_godotsharp_xcopy
rmdir /s /q "%TEMPLATES_DIR%\GodotSharp"
if errorlevel 1 goto :templates_godotsharp_remove_failed

:templates_godotsharp_xcopy
xcopy /E /I /Y "%GODOT_SRC%\bin\GodotSharp" "%TEMPLATES_DIR%\GodotSharp" >nul
if errorlevel 1 goto :templates_godotsharp_copy_failed
goto :templates_godotsharp_ok

:templates_godotsharp_remove_failed
echo.
echo [ERR] Could not delete %TEMPLATES_DIR%\GodotSharp before re-copying.
echo A process likely has the runtime DLLs loaded - close any running game
echo client/server that uses the export template and re-run.
exit /b 1

:templates_godotsharp_copy_failed
echo.
echo [ERR] xcopy of Templates\GodotSharp failed.
echo Source: %GODOT_SRC%\bin\GodotSharp
echo Target: %TEMPLATES_DIR%\GodotSharp
exit /b 1

:templates_godotsharp_ok

echo.
echo === BUILD OK ===
echo Template:
for %%F in ("%TEMPLATES_DIR%\windows_release.x86_64.exe") do echo   %%~zF bytes  %%~nxF
echo.
echo Next step: in Godot Editor -^> Project -^> Export -^> Windows preset
echo   -^> Advanced Settings ON
echo   -^> Custom Template Release: %TEMPLATES_DIR%\windows_release.x86_64.exe
echo Then run build-windows.cmd.
echo.
echo For Linux template: copy build-engine.sh + EngineSource/godot to a Linux host
echo or WSL, then bash build-engine.sh - same module strip, native gcc build.
echo.
endlocal
