# fpkg2ftp

Unpacks a PS5 fPKG straight onto a jailbroken console over FTP, as a game folder in
`/data/homebrew`, without writing the game to the local disk.

The package's inner image is read block by block and every file goes into an FTP upload as
it is decoded. Only the `sce_sys` metadata, which the package keeps in its CNT entry table
outside the inner image, passes through a small temporary folder. The result is the same
`/app0` tree as `ffpfsc-pkg-tool extract-inner` from PS5 UltraPack writes, byte for byte.

## Usage

```sh
fpkg2ftp <game.pkg> --host <ps5 ip>
```

| Option | Default | |
|---|---|---|
| `--host IP` | | the console's address |
| `--port N` | 2121 | FTP port |
| `--user` / `--password` | anonymous / empty | FTP login |
| `--dest PATH` | `<root>/<title id>` | target folder |
| `--root PATH` | `/data/homebrew` | parent of the default target |
| `--passcode HEX32` | 32 zeros | package passcode |
| `--overwrite` | off | upload files again even if they are already there at the right size |
| `--list` | | print what the package holds, upload nothing |

The title id comes from `sce_sys/param.json` (`titleId`, else the `PPSAxxxxx` part of
`contentId`). A run that stopped can be started again: files already on the console at the
right size are skipped. Each file's size is checked on the console after its upload.

Only PS5 packages (FIH images, as PS5 UltraPack and LibProsperoPkg build them) are read; PS4
packages are not.

## Building

.NET SDK 9:

```sh
cd src
dotnet publish -c Release -r osx-arm64 --self-contained true \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false -p:DebugType=None -o ../dist
```

`dist/fpkg2ftp` then runs without .NET installed.

## Licence

GPL-3.0-or-later (`LICENSE`). Built from:

- LibProsperoPkg 1.2.0 (build d7090eb6), GPL-3.0-or-later, by drakmor, a fork of
  SvenGDK/LibProsperoPKG: `src/lib/LibProsperoPkg.dll` is the assembly as patched by PS5
  UltraPack (SHA-256 `934e1d06d33796902ca949ac5abf65833af40a4a4caa5e356847ba1ccdf7486c`),
  see `NOTICE.LibProsperoPkg`.
- `src/InnerImage.cs` from PS5 UltraPack's `ffpfsc-pkg-tool` (knutwurst/ps5-ultrapack,
  commit 9a5131d), unchanged.
- FluentFTP (MIT), BCnEncoder.Net and CommunityToolkit.HighPerformance (MIT), from NuGet.
