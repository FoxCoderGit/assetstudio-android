# AssetStudio for Android

An experimental .NET for Android port of [AssetStudio](https://github.com/Perfare/AssetStudio).
Choose a folder through Android's Storage Access Framework and export supported
Unity objects using a foreground service with progress reporting.

## Build

Install .NET SDK 10, its Android workload and a supported Android SDK/JDK:

```sh
dotnet workload install android
dotnet build AssetStudio.Android/AssetStudio.Android.csproj -c Debug
```

Minimum Android API is 24. `build_debug.cmd` and `install_debug.cmd` provide Windows
shortcuts. The app project references the included C# core; no separate AssetStudio
checkout is required. Debug APKs appear in the normal .NET Android `bin/Debug` output.

The public snapshot upgrades the imaging utility to .NET 8 and current-compatible
ImageSharp APIs. SixLabors dependencies have their own terms, separate from the
MIT source: see https://sixlabors.com/pricing/. The pinned ImageSharp 3.2 package
requires an appropriate SixLabors license key for Release builds; no key is bundled.
Debug compilation was verified. This is source for an experimental port, not a
ready-to-distribute production Release APK.

To reproduce the included native decoders, install Android NDK r27+ and CMake/Ninja:

```sh
python tools/build_native.py --ndk /path/to/ndk
```

Use `--cmake` / `--ninja` to specify executable paths. The linker uses 16 KB ELF
alignment; both bundled ABIs were rebuilt from the included sources.

## Scope

The Android layer lives in `AssetStudio.Android/`; upstream C# code is in
`AssetStudio-0.16.47/`. Export includes texture, text, audio and raw-object paths;
actual support depends on the Unity version, object and decoder availability.
Android texture libraries are bundled for **arm64-v8a and x86_64**. Other ABIs are
not supplied. Some formats and optional FBX/FMOD-backed operations may fail or fall
back to raw data. This initial publication is not a compatibility guarantee for every Unity asset.

Restore/build still reports legacy BitStream framework compatibility and Android
API-level analyzer warnings. ImageSharp 3.2 also has a published tiled-TIFF issue
([GHSA-v76p-62qx-wwq2](https://github.com/advisories/GHSA-v76p-62qx-wwq2)); this source
snapshot is not advertised as a hardened parser for hostile files. Its texture path
constructs images from decoded Unity pixels. Runtime validation on devices and a
complete dependency/security review remain separate work.

No games, extracted commercial assets, personal exports or signing keys are included.
Preserve the upstream notices in [NOTICE.md](NOTICE.md) and LICENSE when redistributing.

Русский: экспериментальный перенос AssetStudio на Android с выбором папки и фоновым экспортом.
