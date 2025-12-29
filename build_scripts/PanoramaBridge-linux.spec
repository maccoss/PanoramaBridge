# -*- mode: python ; coding: utf-8 -*-
# PyInstaller spec file for Linux

a = Analysis(
    ['../panoramabridge.py'],
    pathex=['..'],
    binaries=[],
    datas=[('../screenshots/panoramabridge-logo.png', 'screenshots')],
    hiddenimports=[
        'keyring.backends.SecretService',  # Linux keyring support
        'keyring.backends.kwallet',  # KDE Wallet support
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
)
