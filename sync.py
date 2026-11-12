#!/usr/bin/env python3
"""Copies the telemetry package (and the 2ANEND bridge, for reference) from the game into
this repo, so this repo can be submitted without the game.

The package is developed embedded in the game (2ANENDUNITY/Packages/com.twoanend.telemetry).
Once this repo is on GitHub the game can switch to pulling it by git URL instead, and this
script is no longer needed (see README, "Using it in a game").

    python sync.py                      # the game is next to this repo
    python sync.py "D:/path/to/2ANENDUNITY"
"""
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
GAME = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "..", "2ANEND_UNITY", "2ANENDUNITY")
PKG = "com.twoanend.telemetry"

src = os.path.join(GAME, "Packages", PKG)
dst = os.path.join(HERE, "Packages", PKG)
if not os.path.isdir(src):
    sys.exit("can't find the package at " + src)

# metas first (in the game too, so both copies share the same GUIDs)
subprocess.check_call([sys.executable, os.path.join(HERE, "Tools", "make_metas.py"), src])
if os.path.isdir(dst):
    shutil.rmtree(dst)
shutil.copytree(src, dst, ignore=shutil.ignore_patterns("*.tmp"))
print("package -> " + dst)

bridge = os.path.join(GAME, "Assets", "Scripts", "Telemetry", "TelemetryBridge.cs")
if os.path.isfile(bridge):
    out = os.path.join(HERE, "Integration", "2ANEND")
    os.makedirs(out, exist_ok=True)
    shutil.copy2(bridge, os.path.join(out, "TelemetryBridge.cs"))
    print("bridge  -> " + out)
