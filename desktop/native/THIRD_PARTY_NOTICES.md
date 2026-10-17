# Native Steam interface references

`steam-stats-helper.mm` is a separate Objective-C++ implementation of the JSON
protocol, bounded schema reader, validation, callback handling and result model.
It does not include Valve SDK headers, runtime binaries or Steam account files.
The source is not an upstream SAM distribution. Its ABI and schema facts were
checked against these fixed zlib-licensed primary sources:

- [gibbed SAM Client and interface layouts, de8b71048a0cee3c3e97cd8535e0f55ca86513e4](https://github.com/gibbed/SteamAchievementManager/tree/de8b71048a0cee3c3e97cd8535e0f55ca86513e4/SAM.API)
- [Enhanced macOS adapter, f6c7bbabad55d61802766d94b12bb4e16e8bbc76](https://github.com/asdfghj1237890/SteamAchievementManager-Enhanced/blob/f6c7bbabad55d61802766d94b12bb4e16e8bbc76/web/steam-core/src/imp_macos.rs)
- [Enhanced binary KeyValues/schema fields, same commit](https://github.com/asdfghj1237890/SteamAchievementManager-Enhanced/blob/f6c7bbabad55d61802766d94b12bb4e16e8bbc76/web/steam-core/src/lib.rs)

Callback packing and the payload identity field were independently checked in
Valve's public Source SDK commit `be57e3d01ac7d7aa6a2cacbfef187c0b46616db0`:
[steamclientpublic.h __APPLE__ pack(4)](https://github.com/ValveSoftware/source-sdk-2013/blob/be57e3d01ac7d7aa6a2cacbfef187c0b46616db0/src/public/steam/steamclientpublic.h#L1155-L1164)
and [UserStatsReceived_t fields](https://github.com/ValveSoftware/source-sdk-2013/blob/be57e3d01ac7d7aa6a2cacbfef187c0b46616db0/src/public/steam/isteamuserstats.h#L325-L331).
No Valve header or implementation was copied or redistributed.

The upstream notice is retained below. GPL SamRewritten code is not included.
Valve and other third-party binaries are not licensed by this notice and are
not bundled; the operator's installed Steam client supplies the implementation.

```text
zlib License

Copyright (c) 2024 Rick (rick 'at' gibbed 'dot' us)

This software is provided 'as-is', without any express or implied
warranty. In no event will the authors be held liable for any damages
arising from the use of this software.

Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:

1. The origin of this software must not be misrepresented; you must not
   claim that you wrote the original software. If you use this software
   in a product, an acknowledgment in the product documentation would
   be appreciated but is not required.

2. Altered source versions must be plainly marked as such, and must not
   be misrepresented as being the original software.

3. This notice may not be removed or altered from any source
   distribution.
```
