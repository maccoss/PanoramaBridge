# -*- mode: python ; coding: utf-8 -*-
# PyInstaller spec file for ARM64/Snapdragon processors

a = Analysis(
    ['../panoramabridge.py'],
    pathex=['..'],
    binaries=[],
    datas=[('../screenshots/panoramabridge-logo.png', 'screenshots'), ('../screenshots/panoramabridge-logo.ico', 'screenshots')],
    hiddenimports=[
        'keyring.backends.Windows',  # Windows keyring support
        'keyring.backends._OS_X_API',  # macOS keyring support (if cross-platform)
        'keyring.backends.SecretService',  # Linux keyring support
    ],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[
        # Exclude unused modules to reduce size
        'tkinter',
        'matplotlib',
        'numpy',
        'pandas',
    ],
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
    name='PanoramaBridge-arm64',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,  # Disable UPX for ARM64 - often causes issues
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch='arm64',  # Specify ARM64 architecture
    codesign_identity=None,
    entitlements_file=None,
    icon='../screenshots/panoramabridge-logo.ico',
)
