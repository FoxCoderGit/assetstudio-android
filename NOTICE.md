# Attribution

This is an Android port using the bundled AssetStudio 0.16.47 source tree,
not an unrelated implementation of AssetStudio.

Upstream: https://github.com/Perfare/AssetStudio — MIT, Radu / Perfare.
The upstream README lists component dependencies; their notices remain in source.
Android UI, foreground export integration and packaging: FoxCoderGit, 2026.
The public packaging updates ImageSharp/Drawing dependencies; those NuGet packages
have separate SixLabors licensing terms. They are not relicensed by this repository's MIT file.

The bundled Android texture-decoder libraries correspond to the native decoder
source in `AssetStudio-0.16.47/Texture2DDecoderNative`. Native source comments carry
their component copyright/license notices. FMOD and Autodesk FBX SDK binaries are
not distributed by this standalone package; optional functions requiring them may be unavailable.
The project is not affiliated with Unity Technologies.
