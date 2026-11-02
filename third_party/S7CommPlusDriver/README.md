# S7CommPlusDriver (bundled copy)

Driver for symbolic access to S7-1200/1500 over S7CommPlus with TLS, by Thomas Wiens.

- Upstream: <https://github.com/thomas-v2/S7CommPlusDriver>
- Copied from commit `dbd61e447c7aaf4486cf1f1fe0201212a6bd93c8` (24 Mar 2026)
- License: **LGPL-3.0**, see `LICENSE`. It is built as its own DLL (`S7CommPlusDriver.dll`), so it can be replaced.
- Upstream status: "development state, not intended for production use". It is reverse-engineered, not a Siemens product.
- Needs S7-1500 firmware V2.9+ or S7-1200 V4.3+, configured with TIA Portal V17 or newer.

## Changes in this copy

1. **Write functions removed:** `S7CommPlusConnection.WriteValues`, `S7CommPlusConnection.SetPlcOperatingState`
   and the `WriteTags` extension. PLC-Ki only reads. The protocol messages the driver needs to open a session and
   log in are unchanged; they do not touch process data.
2. Replaced the .NET Framework 4.7.2 project file with `S7CommPlusDriver.csproj` (SDK style, .NET 8, 64-bit OpenSSL names).
3. Removed the 32-bit OpenSSL DLLs, `App.config` and `Properties/AssemblyInfo.cs`. The test program and GUI browser are not included.

## OpenSSL

`S7CommPlusDriver/OpenSSL-dll-x64/` holds `libcrypto-3-x64.dll` and `libssl-3-x64.dll` from upstream.
OpenSSL 3 is under the Apache License 2.0. They are copied next to the app at build time; the app must run as a
64-bit process.

## Zlib.net

`Zlib.net/` is ComponentAce zlib for .NET (BSD-style license, see `Zlib.net/LICENSE`), used by the driver.
