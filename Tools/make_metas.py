#!/usr/bin/env python3
"""Writes Unity .meta files for the telemetry package.

A package installed from a git URL is read-only, and Unity ignores any file in it that has
no .meta, so the metas have to be committed. Their GUIDs are derived from each file's path
inside the package, so running this again (or on another machine) gives the same GUIDs,
and anything referencing them (the demo scene) keeps working. Existing metas are left
alone.

    python Tools/make_metas.py Packages/com.twoanend.telemetry
"""
import hashlib
import os
import sys

PACKAGE = "com.twoanend.telemetry"
TAIL = "  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
IMPORTERS = {
    ".cs": "MonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n",
    ".asmdef": "AssemblyDefinitionImporter:\n" + TAIL,
    ".json": "TextScriptImporter:\n" + TAIL,
    ".md": "TextScriptImporter:\n" + TAIL,
    ".txt": "TextScriptImporter:\n" + TAIL,
}


def guid_for(rel):
    return hashlib.md5((PACKAGE + "/" + rel.replace(os.sep, "/")).encode("utf-8")).hexdigest()


def write_meta(path, rel, body):
    meta = path + ".meta"
    if os.path.exists(meta):
        return 0
    with open(meta, "w", encoding="utf-8", newline="\n") as f:
        f.write("fileFormatVersion: 2\nguid: %s\n%s" % (guid_for(rel), body))
    return 1


def main(root):
    root = os.path.abspath(root)
    made = 0
    for dirpath, dirnames, filenames in os.walk(root):
        # Samples~ / Documentation~ and hidden folders are never imported
        dirnames[:] = [d for d in dirnames if not d.endswith("~") and not d.startswith(".")]
        for d in dirnames:
            p = os.path.join(dirpath, d)
            made += write_meta(p, os.path.relpath(p, root), "folderAsset: yes\nDefaultImporter:\n" + TAIL)
        for fn in filenames:
            if fn.endswith(".meta") or fn.startswith("."):
                continue
            p = os.path.join(dirpath, fn)
            rel = os.path.relpath(p, root)
            if fn == "package.json":
                body = "PackageManifestImporter:\n" + TAIL
            else:
                body = IMPORTERS.get(os.path.splitext(fn)[1].lower(), "DefaultImporter:\n" + TAIL)
            made += write_meta(p, rel, body)
    print("wrote %d meta files under %s" % (made, root))


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else os.path.join("Packages", PACKAGE))
