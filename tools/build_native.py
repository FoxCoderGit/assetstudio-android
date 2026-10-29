"""Build the bundled Android texture decoder from source with 16 KB ELF alignment."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--ndk', type=Path, default=os.getenv('ANDROID_NDK_HOME'))
    parser.add_argument('--cmake', default='cmake')
    parser.add_argument('--ninja', default='ninja')
    args = parser.parse_args()
    if not args.ndk:
        parser.error('Set ANDROID_NDK_HOME or --ndk to an installed Android NDK (r27+)')
    for abi in ['arm64-v8a', 'x86_64']:
        build = ROOT / 'native-build' / abi
        subprocess.run([args.cmake, '-S', str(ROOT/'AssetStudio-0.16.47/Texture2DDecoderNative'),
                        '-B', str(build), '-G', 'Ninja', '-DCMAKE_MAKE_PROGRAM='+args.ninja,
                        '-DCMAKE_TOOLCHAIN_FILE='+str(args.ndk/'build/cmake/android.toolchain.cmake'),
                        '-DANDROID_ABI='+abi, '-DANDROID_PLATFORM=android-24',
                        '-DANDROID_SUPPORT_FLEXIBLE_PAGE_SIZES=ON', '-DCMAKE_BUILD_TYPE=Release'], check=True)
        subprocess.run([args.cmake, '--build', str(build), '--parallel', '2'], check=True)
        output = ROOT/'AssetStudio.Android/NativeLibs'/abi/'libTexture2DDecoderNative.so'
        output.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(build/output.name, output)

if __name__ == '__main__':
    main()
