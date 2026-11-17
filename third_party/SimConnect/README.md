# SimConnect.dll

SkyLink talks to MSFS 2024 through Microsoft's native `SimConnect.dll`. When this folder
contains it, every build copies it next to `SkyLink.exe` and `cli\skylink.exe`, and releases
ship it, so pilots don't need to find it themselves.

**Only use the official copy from the MSFS 2024 SDK:**

```
<MSFS 2024 SDK>\SimConnect SDK\lib\SimConnect.dll
```

(Install the SDK from MSFS: Options > General > Developers > Developer Mode, then
Help > SDK Installer.) Record its version and SHA-256 below when adding or updating it.

| File | SDK version | SHA-256 |
|---|---|---|
| SimConnect.dll | MSFS 2024 SDK 1.8.2 (build 2634044) | `8F386401A990EB5DCE561CD9ECEBF4C3E7565F4501ED48E1785EDF2467FEEE41` |

Note: Microsoft ships this DLL without a digital signature or version resource, so the SDK
version and the hash above are how a copy is identified. The MSFS SDK licence permits
distributing "distributable code" without listing which files that covers; bundling
SimConnect.dll with SimConnect clients is common practice, and the project owner chose to do so.
