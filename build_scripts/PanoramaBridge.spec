# RETIRED: builds the Python application, which has been replaced by a .NET 8 one.
# The .NET build is `dotnet build PanoramaBridge.sln -c Release`, and releases are packed by
# .github/workflows/release.yml. See docs/PYTHON_REMOVAL_PLAN.md.
#
# -*- mode: python ; coding: utf-8 -*-


a = Analysis(
    ['../panoramabridge.py'],
    pathex=['..'],
    binaries=[],
    datas=[('../screenshots/panoramabridge-logo.png', 'screenshots'), ('../screenshots/panoramabridge-logo.ico', 'screenshots')],
    hiddenimports=['keyring.backends.Windows'],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name='PanoramaBridge',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon='../screenshots/panoramabridge-logo.ico',
)
